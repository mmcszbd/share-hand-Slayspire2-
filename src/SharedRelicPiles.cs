using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace SharedHandPrototype;

// Upgrade each opening hand when that player's native draw/choices are ready.
// Widening Bellows' one owner's read would miss players who draw afterwards.
internal static class SharedBellows
{
    private static readonly ConditionalWeakTable<ICombatState,HashSet<Player>> Applied=new();
    internal static void Ready(ICombatState state,Player player)
    {
        if(!HandSharingMode.Shared||SharedHandSemantics.PersonalEffectScope||!SharedRules.IsSupportedParty(state)||player.PlayerCombatState?.TurnNumber>1||!player.Creature.IsAlive) return;
        var source=state.Players.Where(p=>p.Creature.IsAlive).SelectMany(p=>p.Relics).OfType<Bellows>().FirstOrDefault();
        if(source==null||!Applied.GetOrCreateValue(state).Add(player))return;
        source.Flash(new[]{player.Creature});
        CardCmd.Upgrade(player.PlayerCombatState!.Hand.Cards.ToArray(),CardPreviewStyle.HorizontalLayout);
    }
}
[HarmonyPatch(typeof(Bellows),nameof(Bellows.AfterPlayerTurnStart))]
internal static class SharedBellowsNativePatch
{
    private static bool Prefix(Bellows __instance,ref Task __result)
    {if(!HandSharingMode.Shared||SharedHandSemantics.PersonalEffectScope||!SharedRules.IsSupportedParty(__instance.Owner.Creature.CombatState))return true;__result=Task.CompletedTask;return false;}
}
[HarmonyPatch(typeof(Hook),nameof(Hook.AfterPlayerTurnStart))]
internal static class SharedBellowsReadyPatch
{
    private static void Prefix(ICombatState combatState,Player player)=>SharedBellows.Ready(combatState,player);
}
[HarmonyPatch(typeof(TheAbacus),nameof(TheAbacus.AfterShuffle))]
internal static class SharedAbacusPatch
{
    private static void Prefix(TheAbacus __instance,ref Player shuffler)
    {if(CommonPiles.Active&&__instance.Owner.Creature.IsAlive)shuffler=__instance.Owner;}
}

[HarmonyPatch(typeof(GamblingChip),nameof(GamblingChip.AfterPlayerTurnStart))]
internal static class SharedChipRecipient
{
    private static readonly AsyncLocal<Player?> Current=new();
    internal static Player? NeutralDrawer=>Current.Value;
    private static void Prefix(GamblingChip __instance,out Player? __state)
    {__state=Current.Value;Current.Value=HandSharingMode.Shared&&SharedRules.IsSupportedParty(__instance.Owner.Creature.CombatState)?__instance.Owner:null;}
    private static void Postfix(Player? __state)=>Current.Value=__state;
    private static void Finalizer(Player? __state)=>Current.Value=__state;
}

