using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using Godot;

namespace SharedHandPrototype;

// Observe the entire native draw batch, including nested draw hooks. Gameplay
// and network execution are untouched: only newly created decorative backs
// wait for the first card to finish arriving before unfolding behind it.
[HarmonyPatch(typeof(CardPileCmd),"DrawInternal")]
internal static class StackDrawAnimation
{
    private sealed class Batch {internal int Count;}
    private static NPlayerHand? _hand;
    private static Batch _batch=new();
    private static readonly Dictionary<CardModel,ulong> Arrivals=new();
    internal static bool Pending=>_hand==NPlayerHand.Instance&&_batch.Count>0;
    internal static void Arrival(CardModel card){if(Pending)Arrivals[card]=Time.GetTicksMsec();}
    internal static bool ConsumeArrival(CardModel card)=>Arrivals.Remove(card,out var time)&&Time.GetTicksMsec()-time<2000;
    internal static bool Recent(CardModel card)=>Arrivals.TryGetValue(card,out var time)&&Time.GetTicksMsec()-time<2000;
    private static void Prefix(out object? __state)
    {
        __state=null;
        var hand=NPlayerHand.Instance;
        if(!SharedHandConfig.StackBasics||!HandLayouts.Active(hand))return;
        if(_hand!=hand){_hand=hand;_batch=new();Arrivals.Clear();}
        _batch.Count++;__state=_batch;
    }
    private static void Postfix(ref Task<IEnumerable<CardModel>> __result,object? __state)
    {if(__state is Batch batch)__result=Complete(__result,batch);}
    private static async Task<IEnumerable<CardModel>> Complete(Task<IEnumerable<CardModel>> task,Batch batch)
    {try{return await task;}finally{batch.Count--;}}
    private static void Finalizer(Exception? __exception,object? __state)
    {if(__exception!=null&&__state is Batch batch)batch.Count--;}
}
[HarmonyPatch(typeof(CardPile),nameof(CardPile.AddInternal))]
internal static class StackDrawArrivalPatch
{
    private static void Prefix(CardPile __instance,CardModel card)
    {if(__instance.Type==PileType.Hand)StackDrawAnimation.Arrival(card);}
}

// AddInternal is small enough for the game's JIT to inline at native call
// sites. Observe its non-inlined asynchronous command entry as well.
[HarmonyPatch(typeof(CardPileCmd),nameof(CardPileCmd.Add),new[]{typeof(CardModel),typeof(CardPile),typeof(CardPilePosition),typeof(AbstractModel),typeof(bool)})]
internal static class StackDrawCommandArrivalPatch
{
    private static void Prefix(CardModel card,CardPile newPile)
    {if(newPile.Type==PileType.Hand)StackDrawAnimation.Arrival(card);}
}

