using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.TestSupport;

namespace SharedHandPrototype;

internal static class PartyLimits
{
    // Layout editor and this experimental build support up to twelve seats.
    internal const int MaxPlayers=12;
}

public static class StarterCards
{
    public static readonly string[] Names = {
        "StrikeIronclad", "DefendIronclad", "Bash",
        "StrikeSilent", "DefendSilent", "Neutralize", "Survivor",
        "StrikeDefect", "DefendDefect", "Zap", "Dualcast",
        "StrikeNecrobinder", "DefendNecrobinder", "Bodyguard", "Unleash",
        "StrikeRegent", "DefendRegent", "FallingStar", "Venerate"
    };
    private static readonly HashSet<string> Types = Names.Select(x => "MegaCrit.Sts2.Core.Models.Cards." + x).ToHashSet();
    public static bool Supports(CardModel card) => true;
}

internal static class HandEntries
{
    private sealed class Version { internal ulong Value; }
    private static readonly ConditionalWeakTable<CardModel, Version> Versions = new();
    internal static ulong Get(CardModel card)
    {
        var version = Versions.GetOrCreateValue(card);
        if (version.Value == 0 && card.Pile?.Type == PileType.Hand) version.Value = 1;
        return version.Value;
    }
    internal static void Advance(CardModel card) => Versions.GetOrCreateValue(card).Value++;
}

// Preview reads bind the backing owner synchronously, without moving any pile,
// sending events or spending resources. Pile queries retain the source pile.
// Patching just get_Owner is insufficient: the game's JIT can inline that getter.
// This scope must never be held across an await.
internal static class CardEvaluation
{
    [ThreadStatic] private static (CardModel card, CardPile? pile)? _scope;
    internal static bool Active => _scope.HasValue;
    internal static Player? OriginalHolder(CardModel card) => _scope is { } scope && ReferenceEquals(scope.card, card)
        ? card.CombatState?.Players.FirstOrDefault(p => ReferenceEquals(p.PlayerCombatState?.Hand, scope.pile)) : null;
    private sealed class Binding : IDisposable
    {
        private readonly CardModel _card;
        private readonly Player _holder;
        private readonly (CardModel card, CardPile? pile)? _previous;
        internal Binding(CardModel card, Player actor)
        {
            _card = card; _holder = card.Owner; _previous = _scope;
            _scope = (card, card.Pile);
            card.GiveToAnotherPlayer(actor);
        }
        public void Dispose() { _card.GiveToAnotherPlayer(_holder); _scope = _previous; }
    }
    internal static IDisposable Bind(CardModel card, Player actor) => new Binding(card, actor);
    internal static bool TryPile(CardModel card, out CardPile? pile)
    {
        if (_scope is { } scope && ReferenceEquals(scope.card, card))
        { pile = scope.pile; return true; }
        pile = null; return false;
    }
    internal static T As<T>(CardModel card, Player actor, Func<T> query)
    {
        using var binding = Bind(card, actor);
        return query();
    }
}

public static class SharedRules
{
    private static bool _installed;
    internal static readonly HashSet<PlayCardAction> Reentry = new();
    internal static readonly HashSet<uint> Queued = new();
    internal static readonly Dictionary<CardModel, Player> PhysicalHolders = new();
    internal static Player PhysicalHolder(CardModel card) => PhysicalHolders.TryGetValue(card, out var holder) ? holder :
        CardEvaluation.OriginalHolder(card) ?? card.Owner;
    internal static bool IsSupportedParty(CardModel card) => card.CombatState?.Players.Count is >= 2 and <= PartyLimits.MaxPlayers;
    internal static bool IsSupportedParty(ICombatState? state) => state?.Players.Count is >= 2 and <= PartyLimits.MaxPlayers;
    internal static bool IsHandSharingParty(CardModel card)=>HandSharingMode.Shared&&IsSupportedParty(card);
    internal static bool IsHandSharingParty(ICombatState? state)=>HandSharingMode.Shared&&IsSupportedParty(state);

    public static void Install()
    {
        if (_installed) return;
        new Harmony(Entry.ModId + ".rules.v2").PatchAll(typeof(SharedRules).Assembly);
        SharedHandSemantics.Scan();
        _installed = true;
    }

    internal static void RemoveHandVisual(CardModel card)
    {
        if (TestMode.IsOn) return;
        var hand = NPlayerHand.Instance;
        if (hand?.GetCardHolder(card) != null) hand.Remove(card);
    }

    internal static void RestoreHandVisual(CardModel card)
    {
        if (TestMode.IsOn || card.Pile?.Type != PileType.Hand || !LocalContext.IsMine(card)) return;
        var hand = NPlayerHand.Instance;
        if (hand != null && hand.GetCardHolder(card) == null)
        {
            var node = NCard.Create(card);
            hand.Add(node);
            node.UpdateVisuals(PileType.Hand, MegaCrit.Sts2.Core.Entities.Cards.CardPreviewMode.Normal);
        }
    }

    // Deliberately does not use GiveToAnotherPlayer's animation command: that
    // command silently removes the old pile. Here both old and new piles must
    // publish their real mutations to native and teammate-card displays.
    public static void MoveSameCard(CardModel card, Player receiver, PileType type, int index = -1)
    {
        var old = card.Pile ?? throw new InvalidOperationException("Card has no source pile.");
        var destination = CardPile.Get(type, receiver)
            ?? throw new InvalidOperationException("Missing destination pile.");
        if (ReferenceEquals(old, destination))
        {
            if (card.Owner != receiver) card.GiveToAnotherPlayer(receiver);
            SharedPanel.MarkDirty();
            return;
        }
        if (old.Type == PileType.Hand) RemoveHandVisual(card);
        old.RemoveInternal(card, silent: false);
        card.GiveToAnotherPlayer(receiver);
        destination.AddInternal(card, index < 0 ? -1 : Math.Min(index, destination.Cards.Count), silent: false);
        destination.InvokeCardAddFinished();
        SharedPanel.MarkDirty();
    }

    public static async Task ExecuteBorrowed(PlayCardAction action, Func<Task> executeVanilla)
    {
        var card = action.NetCombatCard.ToCardModelOrNull();
        var payer = card == null ? action.Player : AllyPlay.Context(card)?.Payer ?? action.Player;
        if (card != null && TeammatePlayLimit.Blocked(card, payer))
        { TeammatePlayLimit.Speak(payer); HandEntries.Advance(card); Diagnostics.Record("teammate-quota-rejected",payer,card.Id.Entry); return; }
        using var quota = card == null ? null : TeammatePlayLimit.Track(card, payer);
        if (card == null || card.Owner == action.Player || !StarterCards.Supports(card))
        { await executeVanilla(); return; }
        var holder = card.Owner;
        using var generated = OnlyHandsGenerated.Track(action.Player,holder);
        PhysicalHolders[card] = holder;
        var sourceHand = holder.PlayerCombatState?.Hand;
        if (sourceHand == null || card.Pile != sourceHand || !holder.Creature.IsAlive || !action.Player.Creature.IsAlive)
        { PhysicalHolders.Remove(card); action.Cancel(); return; }
        var sourceIndex = sourceHand.Cards.ToList().IndexOf(card);
        var combatId = CombatManager.Instance.CurrentCombatId;
        var moved = false;
        try
        {
            // Same combat card/ID, different acting owner. Vanilla will spend
            // this actor's resources and construct CardPlay.Player from them.
            MoveSameCard(card, action.Player, PileType.Hand);
            moved = true;
            await executeVanilla();
        }
        finally
        {
            if (moved && card.Owner == action.Player)
            {
                var actualResult = card.Pile;
                if (actualResult != null && actualResult.IsCombatPile &&
                    CombatManager.Instance.CurrentCombatId == combatId)
                {
                    // Preserve the result chosen by vanilla/hooks. Exhaust is
                    // already processed once; no extra exhaust/discard effect.
                    MoveSameCard(card, holder, actualResult.Type,
                        actualResult.Type == PileType.Hand ? sourceIndex : -1);
                    RestoreHandVisual(card);
                }
                else
                {
                    // Power/removed card or ended combat: never resurrect it.
                    card.GiveToAnotherPlayer(holder);
                }
            }
            SharedPanel.MarkDirty();
            PhysicalHolders.Remove(card);
        }
    }
}

[HarmonyPatch(typeof(CardModel), "get_Pile")]
internal static class EvaluationPilePatch
{
    private static bool Prefix(CardModel __instance, ref CardPile? __result)
    {
        if (!CardEvaluation.TryPile(__instance, out var pile)) pile = CommonPiles.Find(__instance);
        if (pile == null) return true;
        __result = pile;
        return false;
    }
}

[HarmonyPatch(typeof(CardPile), nameof(CardPile.AddInternal))]
internal static class HandEntryPatch
{
    private static void Postfix(CardPile __instance, CardModel card)
    {
        if (__instance.Type == PileType.Hand) HandEntries.Advance(card);
        SharedPanel.MarkDirty();
    }
}

[HarmonyPatch(typeof(CardPile), nameof(CardPile.RemoveInternal))]
internal static class HandRemovePatch
{
    private static void Postfix() => SharedPanel.MarkDirty();
}

[HarmonyPatch(typeof(Hook), nameof(Hook.ModifyHandDraw))]
internal static class FiveCardDrawPatch
{
    private static void Prefix(ICombatState __0, ref decimal __2)
    {
        if (SharedRules.IsSupportedParty(__0)) __2 = RuleSession.Current.DrawPerTurn;
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSynchronizer), "RequestEnqueue")]
internal static class NativePlayRequestPatch
{
    private static bool Prefix(GameAction action)
    {
        if (action is not PlayCardAction play) return true;
        var card = play.NetCombatCard.ToCardModelOrNull();
        if (card == null || !StarterCards.Supports(card) || !SharedRules.IsSupportedParty(card)) return true;
        if (TestMode.IsOn) return true;
        Diagnostics.Record("manual-queue-request",play.Player,card.Id.Entry+";index="+play.NetCombatCard.CombatCardIndex+
            ";owner="+Diagnostics.Role(card.Owner)+";pile="+card.Pile?.Type+";private="+HandSharingMode.Private+
            ";route="+(HandSharingMode.Private?"native":Entry.RouteNative?"shared":"native")+";ready="+Entry.Ready);
        // Private hands have no contested cards. Keep native action submission,
        // including the game's own multiplayer authorization and queueing.
        if (HandSharingMode.Private && !BoardDuelCoordinator.InCombat) return true;
        if (!Entry.RouteNative && !BoardDuelCoordinator.InCombat) return true;
        // TryManualPlay is small enough to be JIT-inlined into the native
        // mouse flow. Resolve the UI actor again at the non-inlined queue
        // boundary so a borrowed card never authenticates as its holder.
        var borrower = NativeSharedHand.Borrower(card);
        if (borrower != null) play = new PlayCardAction(borrower, play.NetCombatCard, play.CardModelId, play.TargetId);
        Entry.Request(play.NetCombatCard.CombatCardIndex, play.TargetId, play);
        return false;
    }
}

[HarmonyPatch(typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSet), "EnqueueWithoutSynchronizing")]
internal static class QueuedCardPatch
{
    private static void Prefix(GameAction gameAction)
    {
        if (gameAction is not PlayCardAction action) return;
        var card = action.NetCombatCard.ToCardModelOrNull();
        if (card == null || !StarterCards.Supports(card) || !SharedRules.IsSupportedParty(card)) return;
        var id = action.NetCombatCard.CombatCardIndex;
        SharedRules.Queued.Add(id);
        SharedSelection.Prune();
        action.AfterFinished += _ => { SharedRules.Queued.Remove(id); SharedPanel.MarkDirty(); };
        action.BeforeCancelled += _ =>
        {
            SharedRules.Queued.Remove(id);
            if (card.Pile?.Type == PileType.Hand) HandEntries.Advance(card);
            SharedPanel.MarkDirty();
        };
        SharedPanel.MarkDirty();
    }
}

[HarmonyPatch(typeof(PlayCardAction), "ExecuteAction")]
internal static class BorrowedPlayPatch
{
    private static readonly MethodInfo Original = AccessTools.Method(typeof(PlayCardAction), "ExecuteAction");
    private static bool Prefix(PlayCardAction __instance, ref Task __result)
    {
        if (!HandSharingMode.Shared || SharedRules.Reentry.Contains(__instance)) return true;
        var card = __instance.NetCombatCard.ToCardModelOrNull();
        if (card == null || !StarterCards.Supports(card) || card.Owner == __instance.Player ||
            !SharedRules.IsSupportedParty(card)) return true;
        __result = SharedRules.ExecuteBorrowed(__instance, () =>
        {
            SharedRules.Reentry.Add(__instance);
            try { return (Task)Original.Invoke(__instance, null)!; }
            finally { SharedRules.Reentry.Remove(__instance); }
        });
        return false;
    }
}





