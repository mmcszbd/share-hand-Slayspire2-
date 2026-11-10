using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.TestSupport;

namespace SharedHandPrototype;

// A personal prohibition cannot be stored on a shared card model: that would
// prohibit another player too. Record completed Skills by actor/turn instead.
// Completion (not starting/payment) preserves nested/repeated native plays.
// Independent-only mode limits that actor's own hand area. Use the physical
// holder, including during preview/borrowed-play bindings, never draw identity.
// Common combat piles take precedence if both pile switches are enabled.
internal static class SharedSmog
{
    internal const string OverlayName = "SharedHandSmog";
    private sealed class Usage { internal readonly Dictionary<Player,int> Turns = new(); }
    private static readonly ConditionalWeakTable<ICombatState,Usage> Uses = new();
    private static ulong _refresh;
    private static Player Actor(Player fallback) => AllyPlay.Initiator ?? fallback;
    private static bool PersonalHandOnly => RuleSession.Ready &&
        (HandSharingMode.Private || SharedHandSemantics.PersonalEffectScope);
    internal static bool Used(Player actor) => actor.Creature.CombatState is {} state &&
        Uses.TryGetValue(state,out var usage) && usage.Turns.TryGetValue(actor,out var turn) &&
        actor.PlayerCombatState?.TurnNumber == turn;
    internal static bool Blocked(CardModel card, Player actor) => SharedRules.IsSupportedParty(card) &&
        card.Type == CardType.Skill && actor.Creature.GetPower<SmoggyPower>() != null && Used(actor) &&
        (!PersonalHandOnly || SharedRules.PhysicalHolder(card) == actor);
    internal static bool Allows(SmoggyPower power, CardModel card)
    {
        var actor = Actor(card.Owner);
        return power.Owner.Player != actor || !Blocked(card,actor);
    }
    internal static void Completed(SmoggyPower power, CardPlay play)
    {
        var actor = Actor(play.Player);
        if(play.Card.Type != CardType.Skill || power.Owner.Player != actor || actor.PlayerCombatState == null ||
            actor.Creature.CombatState is not {} state) return;
        Uses.GetOrCreateValue(state).Turns[actor] = actor.PlayerCombatState.TurnNumber;
        AccessTools.Method(typeof(PowerModel),"Flash",Type.EmptyTypes).Invoke(power,null);
        SharedPanel.MarkDirty();
    }
    internal static void End(SmoggyPower power, IEnumerable<Creature> participants)
    {
        if(participants.Contains(power.Owner) && power.Owner.Player is {} player &&
            player.Creature.CombatState is {} state && Uses.TryGetValue(state,out var usage))
        { usage.Turns.Remove(player); SharedPanel.MarkDirty(); }
    }
    internal static void Refresh(NCard node)
    {
        // UpdateVisuals can be called before _Ready initializes this member.
        if(node.OverlayContainer is not {} container) return;
        var card = node.Model;
        bool show = !TestMode.IsOn && node.Name!="SharedStackBackCard" && card?.CombatState is {} state && SharedRules.IsSupportedParty(state) &&
            card.Pile?.Type == PileType.Hand && LocalContext.GetMe(state) is {} local && Blocked(card,local);
        var overlay = container.GetNodeOrNull<Control>(OverlayName);
        if(!show)
        {
            if(overlay != null) { container.RemoveChild(overlay); overlay.QueueFree(); }
            return;
        }
        if(overlay != null) return;
        // Reuse the native Smog scene without attaching a gameplay affliction.
        // Each peer can therefore see its own lock on the same physical card.
        overlay = ModelDb.Affliction<Smog>().CreateOverlay();
        overlay.Name = OverlayName;
        overlay.MouseFilter = Control.MouseFilterEnum.Ignore;
        container.AddChild(overlay);
    }
    internal static void Tick()
    {
        if(TestMode.IsOn || Time.GetTicksMsec()-_refresh<100 || NPlayerHand.Instance is not {} hand) return;
        _refresh=Time.GetTicksMsec();
        foreach(var holder in hand.ActiveHolders) if(holder.CardNode is {} node) Refresh(node);
    }
}

[HarmonyPatch(typeof(SmoggyPower),nameof(SmoggyPower.ShouldPlay))]
internal static class SharedSmogPlayPatch
{
    private static bool Prefix(SmoggyPower __instance,CardModel card,ref bool __result)
    {
        if(!SharedRules.IsSupportedParty(__instance.CombatState)) return true;
        __result=SharedSmog.Allows(__instance,card); return false;
    }
}
[HarmonyPatch(typeof(SmoggyPower),nameof(SmoggyPower.AfterCardPlayed))]
internal static class SharedSmogCompletedPatch
{
    private static bool Prefix(SmoggyPower __instance,CardPlay cardPlay,ref Task __result)
    {
        if(!SharedRules.IsSupportedParty(__instance.CombatState)) return true;
        SharedSmog.Completed(__instance,cardPlay); __result=Task.CompletedTask; return false;
    }
}
[HarmonyPatch(typeof(SmoggyPower),nameof(SmoggyPower.AfterCardEnteredCombat))]
internal static class SharedSmogGeneratedPatch
{
    private static bool Prefix(SmoggyPower __instance,ref Task __result)
    {
        if(!SharedRules.IsSupportedParty(__instance.CombatState)) return true;
        __result=Task.CompletedTask; return false;
    }
}
[HarmonyPatch(typeof(SmoggyPower),nameof(SmoggyPower.AfterSideTurnEnd))]
internal static class SharedSmogTurnEndPatch
{
    private static bool Prefix(SmoggyPower __instance,IEnumerable<Creature> participants,ref Task __result)
    {
        if(!SharedRules.IsSupportedParty(__instance.CombatState)) return true;
        SharedSmog.End(__instance,participants); __result=Task.CompletedTask; return false;
    }
}
[HarmonyPatch(typeof(NCard),nameof(NCard.UpdateVisuals))]
internal static class SharedSmogVisualPatch
{
    private static void Postfix(NCard __instance)=>SharedSmog.Refresh(__instance);
}

