using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Runs;

namespace SharedHandPrototype;

// Each client has native holders for its own cards and eligible teammate cards.
// These are views of the original models, never copies or extra backend hands.
internal static class NativeSharedHand
{
    private static readonly HashSet<CardModel> Borrowed = new();
    private static readonly FieldInfo Shortcuts = AccessTools.Field(typeof(NPlayerHand), "_selectCardShortcuts");
    internal static void Clear()
    {
        var hand = NPlayerHand.Instance;
        foreach (var card in Borrowed.ToArray())
            if (GodotObject.IsInstanceValid(hand) && hand!.GetCardHolder(card) != null) hand.Remove(card);
        Borrowed.Clear();
    }
    internal static Player? Borrower(CardModel? card)
    {
        if (!HandSharingMode.Shared || TestMode.IsOn || !Entry.Ready || CardEvaluation.Active || NativePlayChecks.Active || card == null ||
            !StarterCards.Supports(card) || !SharedRules.IsSupportedParty(card) || card.Pile?.Type != PileType.Hand ||
            !Borrowed.Contains(card)) return null;
        var actor = LocalContext.GetMe(card.CombatState!);
        return actor != null && actor != card.Owner ? actor : null;
    }
    internal static IDisposable? BindPreview(CardModel? card)
    {
        var actor = Borrower(card);
        return actor == null ? null : CardEvaluation.Bind(card!, actor);
    }
    internal static void Refresh(CombatState state)
    {
        if (TestMode.IsOn) return;
        if(!HandSharingMode.Shared){Clear();return;}
        var hand = NPlayerHand.Instance;
        var actor = LocalContext.GetMe(state);
        if (hand == null || !hand.IsNodeReady() || actor?.PlayerCombatState == null) return;
        EnsureShortcuts(hand);
        SharedSelection.Prune();
        SharedQueueVisuals.Refresh();
        var desired = Entry.Ready ? state.Players
            .Where(p => p != actor && p.Creature.IsAlive && p.PlayerCombatState != null)
            .SelectMany(p => p.PlayerCombatState!.Hand.Cards).Where(StarterCards.Supports).ToHashSet() : new();
        foreach (var card in Borrowed.Where(c => !desired.Contains(c)).ToArray())
        {
            if(card.Owner==actor&&card.Pile==actor.PlayerCombatState.Hand)
            {
                // A shared-stock redraw can make a former teammate card local
                // before this refresh runs. Adopt its native view instead of
                // removing it as an obsolete teammate view. Instant transfers
                // can also leave an old holder beside the newly created one.
                var views=hand.CardHolderContainer.GetChildren().OfType<NHandCardHolder>()
                    .Where(h=>h.CardNode?.Model==card).ToArray();
                foreach(var duplicate in views.Take(Math.Max(0,views.Length-1)))
                {
                    var node=duplicate.CardNode;hand.RemoveCardHolder(duplicate);
                    if(GodotObject.IsInstanceValid(node))node!.QueueFree();
                }
                Borrowed.Remove(card);continue;
            }
            if (SharedPileVisuals.IsPending(card) || SharedCombatTransforms.KeepOriginal(card) ||
                CommonPiles.Active&&card.Pile?.Type==PileType.Hand&&!SharedRules.PhysicalHolder(card).Creature.IsAlive)
            {
                if(hand.GetCardHolder(card) is NHandCardHolder moving)moving.Hitbox.MouseFilter=Control.MouseFilterEnum.Ignore;
                continue;
            }
            if (hand.GetCardHolder(card) != null) hand.Remove(card);
            Borrowed.Remove(card);
        }
        foreach (var card in desired)
        {
            bool newlyTracked = Borrowed.Add(card);
            if (newlyTracked && hand.GetCardHolder(card) is NHandCardHolder existing) existing.UpdateCard();
            if (hand.GetCardHolder(card) != null || IsPending(card) || SharedSelection.InSelectionView(card)) continue;
            var node = NCard.Create(card);
            // Remote draw commands can complete their whole batch before the
            // next frame. Start the first visible card at the real draw pile,
            // rather than animating a newly allocated holder from screen 0,0.
            if(StackDrawAnimation.Recent(card)&&NCombatRoom.Instance is {} room)
            {room.Ui.AddChild(node);node.GlobalPosition=PileType.Draw.GetTargetPosition(node);}
            var holder = hand.Add(node);
            // Ready initializes OverlayContainer and other native children.
            // Mod card decorations may read them from UpdateVisuals postfixes.
            node.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            holder.Name = "Shared_" + NetCombatCard.FromModel(card).CombatCardIndex;
            Mark(holder);
        }
        foreach (var holder in hand.ActiveHolders) Mark(holder);
        SortByHolder(hand, actor, state);
    }
    private static bool IsPending(CardModel card) => SharedCombatTransforms.WaitingReplacement(card) ||
        (NetCombatCardDb.Instance.TryGetCardId(card, out var id) && (Entry.IsAwaiting(id) || SharedRules.Queued.Contains(id)));
    private static void EnsureShortcuts(NPlayerHand hand)
    {
        var shortcuts = (StringName[])Shortcuts.GetValue(hand)!;
        int length = Math.Max(15, RuleSession.Current.HandLimit);
        if (shortcuts.Length >= length) return;
        int oldLength = shortcuts.Length;
        Array.Resize(ref shortcuts, length);
        for (int i = oldLength; i < shortcuts.Length; i++)
        {
            shortcuts[i] = "shared_hand_unbound_" + i;
            if (!InputMap.HasAction(shortcuts[i])) InputMap.AddAction(shortcuts[i]);
        }
        Shortcuts.SetValue(hand, shortcuts);
    }
    internal static readonly Color TeamColor = SharedHandConfig.DefaultGreen;
    private sealed class GlowState { internal Color Applied; internal bool Changed; }
    private static readonly ConditionalWeakTable<NCardHighlight, GlowState> Glows = new();
    internal static void Mark(NHandCardHolder holder)
    {
        var card = holder.CardNode?.Model;
        if (!HandSharingMode.Shared || card == null || !holder.IsNodeReady() || !Entry.Ready) return;
        var mine = LocalContext.GetMe(card.CombatState!) == SharedRules.PhysicalHolder(card);
        var glow = holder.CardNode!.CardHighlight;
        if(!SharedRules.PhysicalHolder(card).Creature.IsAlive){glow.AnimHideInstantly();return;}
        var saved = Glows.GetOrCreateValue(glow);
        if(UnassignedCardGlow.Quiet(card))
        {
            saved.Changed=false;
            UnassignedCardGlow.Hide(holder);
            return;
        }
        if (mine ? SharedHandConfig.OwnCardGlow : SharedHandConfig.TeammateGlow)
        {
            saved.Applied = SharedHandConfig.ColorFor(SharedRules.PhysicalHolder(card).Character);
            saved.Changed = true; glow.Modulate = saved.Applied;
            glow.AnimShow();
        }
        else if (saved.Changed)
        {
            saved.Changed = false;
            // Recompute native playable/red/gold/hidden effects now. The
            // postfix sees Changed=false and does not recurse into restoration.
            holder.UpdateCard();
        }
    }
    private static void SortByHolder(NPlayerHand hand, Player actor, CombatState state)
    {
        if (!Entry.Ready || hand.IsInCardSelection) return;
        var container = hand.CardHolderContainer;
        var holders = container.GetChildren().OfType<NHandCardHolder>().ToArray();
        var order = actor.PlayerCombatState!.Hand.Cards.Concat(state.Players.Where(p => p != actor)
            .SelectMany(p => p.PlayerCombatState!.Hand.Cards)).ToArray();
        var indices = order.Select((card, index) => (card, index)).ToDictionary(x => x.card, x => x.index);
        var modelOrder = holders.OrderBy(h => indices.GetValueOrDefault(h.CardModel, int.MaxValue)).ToArray();
        // Stack seats can interleave cards from different players. Do not put
        // the raw owner order back on top of the visible layout every frame.
        var sorted = HandStacks.SceneOrdered(hand,modelOrder).ToArray();
        if (holders.SequenceEqual(sorted)) return;
        HandStacks.SortHolderNodes(container,sorted);
        AccessTools.Method(typeof(NPlayerHand), "RefreshLayout").Invoke(hand, null);
    }
    internal static int CombinedInsertIndex(NPlayerHand hand, CardModel card)
    {
        var state = card.CombatState;
        var actor = state == null ? null : LocalContext.GetMe(state);
        if (actor?.PlayerCombatState == null) return -1;
        var order = actor.PlayerCombatState.Hand.Cards.Concat(state!.Players.Where(p => p != actor)
            .SelectMany(p => p.PlayerCombatState!.Hand.Cards).Where(StarterCards.Supports)).ToList();
        int position = order.IndexOf(card);
        if (position < 0) return -1;
        return hand.CardHolderContainer.GetChildren().OfType<NHandCardHolder>()
            .Count(h => h.CardModel != card && order.IndexOf(h.CardModel) < position);
    }
}

[HarmonyPatch(typeof(NPlayerHand), nameof(NPlayerHand.SelectCards))]
internal static class SharedSelectionPatch
{
    private static void Prefix(NPlayerHand __instance,CardSelectorPrefs prefs, ref Func<CardModel, bool>? filter)
    {HandStacks.Selection(__instance);SharedSelection.Begin(prefs,ref filter);}
}

[HarmonyPatch(typeof(NPlayerHand), "GetHandInsertIndex")]
internal static class SharedInsertPatch
{
    private static bool Prefix(NPlayerHand __instance, CardModel card, ref int __result)
    {
        if (!HandSharingMode.Shared || !Entry.Ready || !SharedRules.IsSupportedParty(card)) return true;
        __result = NativeSharedHand.CombinedInsertIndex(__instance, card);
        return false;
    }
}

[HarmonyPatch(typeof(NHandCardHolder), nameof(NHandCardHolder.UpdateCard))]
internal static class SharedHolderPreviewPatch
{
    private static void Prefix(NHandCardHolder __instance, out IDisposable? __state) =>
        __state = NativeSharedHand.BindPreview(__instance.CardNode?.Model);
    private static void Finalizer(IDisposable? __state) => __state?.Dispose();
    private static void Postfix(NHandCardHolder __instance) => NativeSharedHand.Mark(__instance);
}

[HarmonyPatch(typeof(NCard), nameof(NCard.UpdateVisuals))]
internal static class SharedCardPreviewPatch
{
    private static void Prefix(NCard __instance, out IDisposable? __state) =>
        __state = NativeSharedHand.BindPreview(__instance.Model);
    private static void Finalizer(IDisposable? __state) => __state?.Dispose();
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.IsValidTarget))]
internal static class SharedTargetActorPatch
{
    // Native drag/shortcut validation checks the target before CanPlay. Use
    // the same local borrower throughout all target postfixes (including PvP
    // extensions), then restore the physical holder even if validation throws.
    // Ordered effects and auto-play retain their explicit execution actor.
    private static void Prefix(CardModel __instance, out IDisposable? __state)
    {
        __state = null;
        var actor = NativeSharedHand.Borrower(__instance);
        if (actor == null) return;
        bool queued = NetCombatCardDb.Instance.TryGetCardId(__instance, out var id) && SharedRules.Queued.Contains(id);
        if (!queued && !RunManager.Instance.ActionExecutor.IsRunning)
            __state = CardEvaluation.Bind(__instance, actor);
    }
    private static void Finalizer(IDisposable? __state) => __state?.Dispose();
}

[HarmonyPatch(typeof(NTargetManager), "AllowedToTargetCreature")]
internal static class SharedVersusDummyHoverPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Creature creature, ref bool __result)
    {
        // YouJin's PvP placeholder is alive but cannot be hit. Crossing its
        // hitbox from a side hand otherwise selects it, and vanilla cancels
        // the whole arrow on the next frame. Do not change combat eligibility
        // or touch other encounters, private hands, or single-player targeting.
        if (__result && HandLayouts.Active(NPlayerHand.Instance) &&
            creature.Monster?.GetType().FullName == "YouJin.Scripts.BetrayalEggMonster" && !creature.IsHittable)
            __result = false;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.CanPlay), new[] { typeof(UnplayableReason), typeof(AbstractModel) },
    new[] { ArgumentType.Out, ArgumentType.Out })]
internal static class SharedCanPlayPatch
{
    private readonly record struct CheckScope(IDisposable? Binding, Player? Actor, bool CheckLock);
    private static void Prefix(CardModel __instance, out CheckScope __state)
    {
        // Evaluate hover, shortcut and drag checks for the same local actor.
        // Queued/executing actions and explicit validation scopes retain their actor.
        bool queued = NetCombatCardDb.Instance.TryGetCardId(__instance, out var id) && SharedRules.Queued.Contains(id);
        // Presence is asynchronous. Never let it change auto-play/effect
        // decisions inside ordered actions differently on different peers.
        bool checkLock = !CardEvaluation.Active && !queued && !TestMode.IsOn && !NativePlayChecks.Active &&
            __instance.Owner.PlayerCombatState?.Phase == PlayerTurnPhase.Play && !RunManager.Instance.ActionExecutor.IsRunning;
        var actor = checkLock && (RuleSession.Current.LockSelections || RuleSession.Current.TeammateCardLimit > 0) && __instance.CombatState is { } state ? LocalContext.GetMe(state) : null;
        __state = new(checkLock ? NativeSharedHand.BindPreview(__instance) : null, actor, checkLock);
    }
    private static void Postfix(CardModel __instance, CheckScope __state, ref bool __result,
        ref UnplayableReason reason, ref AbstractModel? preventer)
    {
        if (!__result || !__state.CheckLock || __state.Actor == null || !(SelectionHints.IsLocked(__instance, __state.Actor) || TeammatePlayLimit.Blocked(__instance, __state.Actor))) return;
        __result = false; reason = UnplayableReason.BlockedByCardLogic; preventer = __instance;
    }
    private static void Finalizer(CheckScope __state) => __state.Binding?.Dispose();
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.TryManualPlay))]
internal static class SharedManualPlayPatch
{
    private static bool Prefix(CardModel __instance, Creature? target, ref bool __result)
    {
        var actor = NativeSharedHand.Borrower(__instance);
        if (actor == null) return true;
        __result = false;
        var id = NetCombatCard.FromModel(__instance).CombatCardIndex;
        if (Entry.IsAwaiting(id) || SharedRules.Queued.Contains(id) ||
            actor.PlayerCombatState?.Phase != PlayerTurnPhase.Play ||
            CombatManager.Instance.PlayerActionsDisabled || NPlayerHand.Instance?.IsInCardSelection == true ||
            !CardEvaluation.As(__instance, actor, () => __instance.CanPlayTargeting(target))) return false;
        Entry.Request(id, target?.CombatId, new PlayCardAction(actor, NetCombatCard.FromModel(__instance), __instance.Id, target?.CombatId));
        __result = Entry.IsAwaiting(id);
        return false;
    }
}

[HarmonyPatch]
internal static class SharedCreatePlayPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NMouseCardPlay), nameof(NMouseCardPlay.Create));
        yield return AccessTools.Method(typeof(NControllerCardPlay), nameof(NControllerCardPlay.Create));
    }
    private static void Postfix(NCardPlay __result)
    {
        var actor = NativeSharedHand.Borrower(__result.Holder.CardModel);
        if (actor != null) AccessTools.PropertySetter(typeof(NCardPlay), nameof(NCardPlay.Player)).Invoke(__result, new object[] { actor });
    }
}

[HarmonyPatch(typeof(NCardPlay), "get_CardOwnerNode")]
internal static class SharedOrbPreviewPatch
{
    private static bool Prefix(NCardPlay __instance, ref MegaCrit.Sts2.Core.Nodes.Combat.NCreature? __result)
    {
        var actor = NativeSharedHand.Borrower(__instance.Holder.CardModel);
        if (actor == null) return true;
        __result = NCombatRoom.Instance.GetCreatureNode(actor.Creature);
        return false;
    }
}

[HarmonyPatch(typeof(NCardPlay), "ShowMultiCreatureTargetingVisuals")]
internal static class SharedTargetPreviewPatch
{
    private static void Prefix(NCardPlay __instance, out IDisposable? __state) =>
        __state = NativeSharedHand.BindPreview(__instance.Holder.CardModel);
    private static void Finalizer(IDisposable? __state) => __state?.Dispose();
}





