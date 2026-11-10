using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace SharedHandPrototype;

// The retain keyword on the model remains authoritative for the actual turn.
// Only description rendering is viewer-specific; no gameplay state is changed.
internal static class PhantomBladesDisplay
{
    internal sealed class Origin { internal bool Phantom; internal bool Other; }
    internal static readonly ConditionalWeakTable<CardModel,Origin> Origins = new();
    [ThreadStatic] internal static int Applying;
    [ThreadStatic] internal static CardModel? Describing;
}

[HarmonyPatch(typeof(PhantomBladesPower), nameof(PhantomBladesPower.AfterApplied))]
internal static class PhantomAppliedScope
{
    static void Prefix() => PhantomBladesDisplay.Applying++;
    static void Postfix(PhantomBladesPower __instance)
    {
        var state=__instance.Owner?.CombatState;
        var hand=NPlayerHand.Instance;
        if(!HandSharingMode.Shared || !SharedRules.IsSupportedParty(state) || state==null ||
           LocalContext.GetMe(state)!=__instance.Owner.Player || !GodotObject.IsInstanceValid(hand) || !hand!.IsNodeReady()) return;
        foreach(var holder in hand.ActiveHolders)
            if(holder.CardNode?.Model?.Tags.Contains(CardTag.Shiv)==true) holder.UpdateCard();
    }
    static Exception? Finalizer(Exception? __exception) { PhantomBladesDisplay.Applying--; return __exception; }
}

[HarmonyPatch(typeof(PhantomBladesPower), nameof(PhantomBladesPower.AfterCardEnteredCombat))]
internal static class PhantomEnteredScope
{
    static void Prefix() => PhantomBladesDisplay.Applying++;
    static Exception? Finalizer(Exception? __exception) { PhantomBladesDisplay.Applying--; return __exception; }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.AddKeyword))]
internal static class PhantomTrackRetain
{
    static void Prefix(CardModel __instance, CardKeyword keyword)
    {
        if(keyword != CardKeyword.Retain || !__instance.Tags.Contains(CardTag.Shiv)) return;
        var origin=PhantomBladesDisplay.Origins.GetOrCreateValue(__instance);
        if(PhantomBladesDisplay.Applying > 0)
        {
            if(!origin.Phantom && __instance.Keywords.Contains(CardKeyword.Retain)) origin.Other=true;
            origin.Phantom=true;
        }
        else origin.Other=true;
    }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.RemoveKeyword))]
internal static class PhantomForgetRetain
{
    static void Postfix(CardModel __instance, CardKeyword keyword)
    { if(keyword==CardKeyword.Retain) PhantomBladesDisplay.Origins.Remove(__instance); }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.GetDescriptionForPile), new Type[] { typeof(PileType), typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature) })]
internal static class PhantomDescriptionScope
{
    static void Prefix(CardModel __instance, PileType pileType, out CardModel? __state)
    {
        __state=PhantomBladesDisplay.Describing;
        if(pileType==PileType.Hand && HandSharingMode.Shared && SharedRules.IsSupportedParty(__instance) && __instance.Tags.Contains(CardTag.Shiv))
            PhantomBladesDisplay.Describing=__instance;
    }
    static Exception? Finalizer(Exception? __exception, CardModel? __state)
    { PhantomBladesDisplay.Describing=__state; return __exception; }
}

[HarmonyPatch(typeof(CardModel), nameof(CardModel.ShouldRetainThisTurn), MethodType.Getter)]
internal static class PhantomViewerRetain
{
    static void Postfix(CardModel __instance, ref bool __result)
    {
        if(!ReferenceEquals(PhantomBladesDisplay.Describing,__instance)) return;
        var viewer=LocalContext.GetMe(__instance.CombatState!);
        if(viewer==null) return;
        if(viewer.Creature.GetPower<PhantomBladesPower>()!=null) { __result=true; return; }
        if(PhantomBladesDisplay.Origins.TryGetValue(__instance,out var origin) && origin.Phantom && !origin.Other)
            __result=(bool)(AccessTools.Field(typeof(CardModel),"_hasSingleTurnRetain").GetValue(__instance) ?? false);
    }
}
