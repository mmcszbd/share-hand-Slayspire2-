using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.TestSupport;

namespace SharedHandPrototype;
// One ordered vanilla envelope contains both players and the original target.
// Never distribute an ownership change in a separate sidecar message.
internal static class AllyPlay
{
    internal const string Command = "__shared_hand_ally_v15 ";
    internal sealed record Cast(CardModel Card, Player Payer, Player Recipient);
    private sealed record Payment(CardModel Card, Player Payer);
    private static readonly AsyncLocal<Cast?> Current = new();
    private static readonly AsyncLocal<Payment?> Paying = new();
    private static readonly ConditionalWeakTable<GameAction, Packet> Payloads = new();
    private static readonly MethodInfo SpendEnergy = AccessTools.Method(typeof(CardModel), "SpendEnergy");
    private static readonly MethodInfo SpendStars = AccessTools.Method(typeof(CardModel), "SpendStars");
    internal static bool Enabled => RuleSession.Ready && RuleSession.Current.Harmony &&
        SharedRules.IsSupportedParty(CombatManager.Instance.DebugOnlyGetState());

    // Native ally/player-targeted cards already work on teammates. Keep that
    // existing target/actor meaning instead of demanding another player target.
    internal static bool Eligible(CardModel card) => card.Type is CardType.Skill or CardType.Power && card.TargetType is not (TargetType.AnyAlly or TargetType.AnyPlayer);
    internal static Player? PaymentActor(CardModel card) => Paying.Value is { } p && p.Card == card ? p.Payer : null;
    internal static Player SpendingOwner(CardModel card) => PaymentActor(card) ?? card.Owner;
    internal static Cast? Context(CardModel card) => Current.Value is { } c && c.Card == card ? c : null;
    // Smog counts the initiating player's Skills, including effects nested in
    // an ally-use. Other play effects continue to use their native recipient.
    internal static Player? Initiator => Current.Value?.Payer;
    private sealed class Scope : IDisposable
    {
        private readonly Cast? _previous;
        internal Scope(Cast cast)
        {
            _previous = Current.Value;
            Current.Value = cast;
        }

        public void Dispose() => Current.Value = _previous;
    }

    private static T AsRecipient<T>(Cast cast, Func<T> read) => CardEvaluation.As(cast.Card, cast.Recipient, read);
    internal static (int Energy, int Stars) Price(Cast cast) => AsRecipient(cast, () =>
    {
        var card = cast.Card;
        var bank = cast.Payer.PlayerCombatState!;
        int energy = card.EnergyCost.CostsX ? bank.Energy : card.EnergyCost.GetAmountToSpend();
        int stars = card.HasStarCostX ? bank.Stars : Math.Max(0, card.GetStarCostWithModifiers());
        if (energy > bank.Energy && Hook.ShouldPayExcessEnergyCostWithStars(card.CombatState!, cast.Payer))
        {
            stars += (energy - bank.Energy) * 2;
            energy = bank.Energy;
        }

        return (energy, stars);
    });
    internal static bool Affordable(Cast cast, out UnplayableReason reason)
    {
        var price = Price(cast);
        var bank = cast.Payer.PlayerCombatState!;
        reason = UnplayableReason.None;
        if (price.Energy > bank.Energy)
            reason |= UnplayableReason.EnergyCostTooHigh;
        if (price.Stars > bank.Stars)
            reason |= UnplayableReason.StarCostTooHigh;
        return reason == UnplayableReason.None;
    }

    internal static string? Validate(CardModel card, Player payer, ulong recipientId, Creature? target, bool checkTarget = true)
    {
        if (!Enabled || !Eligible(card))
            return "和谐友爱未开启，或此牌不支持代打。";
        var state = card.CombatState;
        var recipient = state?.Players.FirstOrDefault(p => p.NetId == recipientId);
        if (recipient == null || recipient == payer || !recipient.Creature.IsAlive || !payer.Creature.IsAlive)
            return "请选择仍存活的另一名队友。";
        var cast = new Cast(card, payer, recipient);
        using var scope = new Scope(cast);
        // The giver cannot evade personal play prohibitions such as Enthralled.
        bool mayGive = CardEvaluation.As(card, payer, () => Hook.ShouldPlay(state!, card, out _, AutoPlayType.None));
        if (!mayGive)
            return "出牌者受到原版禁止出牌效果影响。";
        bool mayReceive = AsRecipient(cast, () => card.CanPlay() && (!checkTarget || card.IsValidTarget(target)));
        return mayReceive ? null : "出牌者资源不足，或队友当前无法使用此牌／目标无效。";
    }

    internal static ConsoleCmdGameAction Action(Player payer, Packet packet) => new(payer, Command + Convert.ToBase64String(Protocol.Encode(packet)), true);
    internal static Packet? Payload(GameAction action)
    {
        if (action is not ConsoleCmdGameAction console || !console.Cmd.StartsWith(Command, StringComparison.Ordinal))
            return null;
        if (Payloads.TryGetValue(action, out var cached))
            return cached;
        if (console.Cmd.Length > 4096)
            return null;
        try
        {
            var packet = Protocol.Decode(Convert.FromBase64String(console.Cmd.Substring(Command.Length)));
            if (packet.Kind != PacketKind.Play || packet.Recipient == null || packet.Actor != console.Player.NetId)
                return null;
            Payloads.Add(action, packet);
            return packet;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static async Task Execute(ConsoleCmdGameAction action)
    {
        var p = Payload(action);
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (p == null || state == null || p.Battle != Entry.Battle || !Enabled)
            return;
        if (!NetCombatCardDb.Instance.TryGetCard(p.Card, out var card) || card == null)
            return;
        var payer = action.Player;
        if (TeammatePlayLimit.Blocked(card, payer)) { TeammatePlayLimit.Speak(payer); HandEntries.Advance(card); return; }
        var target = state.GetCreature(p.Target);
        if (card.Owner.NetId != p.Holder || card.Pile?.Type != PileType.Hand || HandEntries.Get(card) != p.Generation || !card.Owner.Creature.IsAlive || payer.PlayerCombatState?.Phase != PlayerTurnPhase.Play || payer.PlayerCombatState.TurnNumber != p.ActorTurn || card.Owner.PlayerCombatState!.TurnNumber != p.OwnerTurn || CombatManager.Instance.IsPlayerReadyToEndTurn(payer) || state.CurrentSide != CombatSide.Player || (CombatManager.Instance.PlayersTakingExtraTurn.Count > 0 && !CombatManager.Instance.PlayersTakingExtraTurn.Contains(payer)) || CombatManager.Instance.IsOverOrEnding || Validate(card, payer, p.Recipient!.Value, target) != null)
        {
            Diagnostics.Record("ally-cancel", payer, card.Id.Entry);
            return;
        }

        var recipient = state.Players.Single(q => q.NetId == p.Recipient);
        var cast = new Cast(card, payer, recipient);
        using var scope = new Scope(cast);
        var play = new PlayCardAction(recipient, NetCombatCard.FromModel(card), card.Id, p.Target);
        await SharedRules.ExecuteBorrowed(play, async () =>
        {
            // Retain native validation, payment hooks, CardPlay construction,
            // replay, effects, history and final discard/exhaust/power handling.
            if (!card.CanPlay() || !card.IsValidTarget(target))
                return;
            var paid = await card.SpendResources();
            // A mod's spending hook can await a choice, remove/transfer this
            // card, kill its caster or end combat. Never reinsert that card
            // into Play afterwards. Already paid native costs stay paid.
            if (card.HasBeenRemovedFromState || card.Owner != recipient || card.Pile?.Type != PileType.Hand || !recipient.Creature.IsAlive || !ReferenceEquals(state, CombatManager.Instance.DebugOnlyGetState()) || CombatManager.Instance.IsOverOrEnding)
            {
                Diagnostics.Record("ally-interrupted-after-payment", payer, card.Id.Entry);
                return;
            }

            var resources = new ResourceInfo
            {
                EnergySpent = paid.Item1,
                EnergyValue = paid.Item1,
                StarsSpent = paid.Item2,
                StarValue = paid.Item2
            };
            await card.OnPlayWrapper(new Choice(action, recipient), target, false, resources);
        });
        Diagnostics.Record("ally-play", payer, "recipient=" + Diagnostics.Role(recipient) + "; card=" + card.Id.Entry);
    }

    private sealed class Choice : GameActionPlayerChoiceContext
    {
        private readonly Player _recipient;
        internal Choice(GameAction action, Player recipient) : base(action) => _recipient = recipient;
        public override ulong? OwnerId => _recipient.NetId;

        public override async Task SignalPlayerChoiceEnded()
        {
            // A puppet's focus can change while the payer's action is paused.
            // Resume authority belongs to the payer's actual network controller,
            // not the currently displayed role. Real peers retain one sender.
            if (PuppetCompat.ControllerOf(Action.OwnerId) == RunManager.Instance.NetService.NetId || TestFlags.ShouldSendResumeForRemotePlayers)
                RunManager.Instance.ActionQueueSynchronizer.RequestResumeActionAfterPlayerChoice(Action);
            await Action.WaitForActionToResumeExecutingAfterPlayerChoice();
        }
    }

    internal static async Task<(int, int)> Pay(Cast cast)
    {
        var card = cast.Card;
        var price = Price(cast);
        var previous = Paying.Value;
        Diagnostics.Record("ally-payment", cast.Payer, "recipient=" + Diagnostics.Role(cast.Recipient) + "; energy=" + price.Energy + "; stars=" + price.Stars);
        // Redirect only the spending helpers' logical owner reads. Never move
        // or reassign a real card while a resource hook awaits a remote choice.
        // Native payment history/hooks see the payer; play modifiers still
        // belong to the recipient, who is the card's actual caster.
        Paying.Value = new(card, cast.Payer);
        try
        {
            await (Task)SpendEnergy.Invoke(card, new object[] { price.Energy })!;
            await (Task)SpendStars.Invoke(card, new object[] { price.Stars })!;
            return (price.Energy, price.Stars);
        }
        finally
        {
            Paying.Value = previous;
        }
    }

    internal static void Queued(GameAction action)
    {
        var p = Payload(action);
        if (p == null)
            return;
        SharedRules.Queued.Add(p.Card);
        SharedSelection.Prune();
        void Finish(GameAction _)
        {
            SharedRules.Queued.Remove(p.Card);
            if (NetCombatCardDb.Instance.TryGetCard(p.Card, out var card) && card?.Pile?.Type == PileType.Hand && HandEntries.Get(card) == p.Generation)
                HandEntries.Advance(card);
            SharedPanel.MarkDirty();
        }

        action.AfterFinished += Finish;
        action.BeforeCancelled += Finish;
    }

    internal static void QueueVisual(NCardPlayQueue queue, GameAction action)
    {
        var p = Payload(action);
        if (p == null || TestMode.IsOn || !NetCombatCardDb.Instance.TryGetCard(p.Card, out var card) || card?.Pile?.Type != PileType.Hand)
            return;
        var payer = card.CombatState!.Players.First(q => q.NetId == p.Actor);
        var proxy = new PlayCardAction(payer, NetCombatCard.FromModel(card), card.Id, p.Target);
        queue.OnLocalCardPlayed(proxy, NPlayerHand.Instance?.GetCardHolder(card), card);
        var items = (IList)AccessTools.Field(typeof(NCardPlayQueue), "_playQueue").GetValue(queue)!;
        foreach (var item in items)
            if (AccessTools.Field(item!.GetType(), "action").GetValue(item) == proxy)
            {
                AccessTools.Field(item.GetType(), "action").SetValue(item, action);
                break;
            }
    }
}

[HarmonyPatch(typeof(ConsoleCmdGameAction), "ExecuteAction")]
internal static class AllyActionPatch
{
    private static bool Prefix(ConsoleCmdGameAction __instance, ref Task __result)
    {
        if (!__instance.Cmd.StartsWith(AllyPlay.Command, StringComparison.Ordinal))
            return true;
        __result = AllyPlay.Execute(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(PlayerCombatState), nameof(PlayerCombatState.HasEnoughResourcesFor))]
internal static class AllyResourcesPatch
{
    private static bool Prefix(CardModel card, out UnplayableReason reason, ref bool __result)
    {
        reason = UnplayableReason.None;
        var cast = AllyPlay.Context(card);
        if (cast == null)
            return true;
        __result = AllyPlay.Affordable(cast, out reason);
        return false;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.SpendResources))]
internal static class AllySpendPatch
{
    private static bool Prefix(CardModel __instance, ref Task<(int, int)> __result)
    {
        var cast = AllyPlay.Context(__instance);
        if (cast == null)
            return true;
        __result = AllyPlay.Pay(cast);
        return false;
    }
}

[HarmonyPatch]
internal static class AllySpendingOwnerPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => typeof(CardModel).GetNestedTypes(BindingFlags.NonPublic).Where(t => t.Name.StartsWith("<SpendEnergy>", StringComparison.Ordinal) || t.Name.StartsWith("<SpendStars>", StringComparison.Ordinal)).Select(t => (MethodBase)AccessTools.Method(t, "MoveNext"));
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getter = AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Owner));
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(getter))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(AllyPlay), nameof(AllyPlay.SpendingOwner));
            }

            yield return instruction;
        }
    }
}

[HarmonyPatch(typeof(ActionQueueSet), "EnqueueWithoutSynchronizing")]
internal static class AllyQueuedPatch
{
    private static void Prefix(GameAction gameAction) => AllyPlay.Queued(gameAction);
}

[HarmonyPatch(typeof(NCardPlayQueue), "OnActionEnqueued")]
internal static class AllyQueueVisualPatch
{
    private static void Postfix(NCardPlayQueue __instance, GameAction action) => AllyPlay.QueueVisual(__instance, action);
}

