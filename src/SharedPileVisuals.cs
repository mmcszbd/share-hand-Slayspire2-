using System.Collections.Generic;
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Cards;
using MegaCrit.Sts2.Core.TestSupport;

namespace SharedHandPrototype;

// Vanilla deliberately skips remote hand -> pile animations. Those cards now
// have real visible holders on this client. Animate only those existing views;
// never create another model, move a backend card or change player identity.
[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.GetTweenForCardsChangingPiles),
    new[] { typeof(IEnumerable<CardPileAddResult>), typeof(bool) })]
internal static class SharedPileVisuals
{
    private static readonly Dictionary<CardModel,int> Pending=new();
    internal static bool IsPending(CardModel card)=>Pending.ContainsKey(card);
    internal static CardModel[] Hold(IEnumerable<CardModel> cards,bool skipVisuals)
    {
        if(skipVisuals||TestMode.IsOn||!HandLayouts.Active(NPlayerHand.Instance))return Array.Empty<CardModel>();
        var held=cards.Where(c=>c.Pile?.Type==PileType.Hand&&!LocalContext.IsMe(c.Owner)&&
            NPlayerHand.Instance.GetCardHolder(c)!=null).Distinct().ToArray();
        foreach(var card in held)Pending[card]=Pending.GetValueOrDefault(card)+1;
        return held;
    }
    internal static void Release(CardModel[]? cards)
    {
        if(cards==null)return;
        foreach(var card in cards)
            if(Pending.TryGetValue(card,out var count)){if(count<=1)Pending.Remove(card);else Pending[card]=count-1;}
    }
    internal static async Task<IReadOnlyList<CardPileAddResult>> Finish(Task<IReadOnlyList<CardPileAddResult>> task,CardModel[] cards)
    {
        try{return await task;}
        finally{Release(cards);}
    }
    private static readonly MethodInfo Detach=AccessTools.Method(typeof(CardPileCmd),"MoveCardNodeToNewPileBeforeTween");
    private static void Prefix(IEnumerable<CardPileAddResult> results)
    {
        if(TestMode.IsOn||!HandLayouts.Active(NPlayerHand.Instance)||!HandLayouts.CombatVisible)return;
        foreach(var result in results)
            if(result.success&&result.oldPile?.Type==PileType.Hand&&result.targetPile!=PileType.Hand)
                HandStacks.Departure(result.cardAdded);
    }
    private static void Postfix(IEnumerable<CardPileAddResult> results, ref (Tween?, bool) __result)
    {
        var hand=NPlayerHand.Instance;
        if(TestMode.IsOn||!HandLayouts.Active(hand)||!HandLayouts.CombatVisible)return;
        bool animated=false;
        foreach(var result in results)
        {
            var card=result.cardAdded;
            if(!result.success||result.oldPile?.Type!=PileType.Hand||LocalContext.IsMe(card.Owner)||
                card.Pile?.Type is not (PileType.Discard or PileType.Draw or PileType.Exhaust))continue;
            var node=hand!.GetCardHolder(card)?.CardNode;
            if(!GodotObject.IsInstanceValid(node)||!node!.IsVisibleInTree())continue;
            var transform=node.GetGlobalTransformWithCanvas();
            var pile=card.Pile.Type;
            Detach.Invoke(null,new object[]{node,pile});
            node.Scale=transform.Scale;node.Rotation=transform.Rotation;
            if(pile==PileType.Exhaust)
            {
                card.Pile.InvokeCardAddFinished();
                var exhaust=NCardExhaustQuickVfx.Create(node);
                if(exhaust!=null)TaskHelper.RunSafely(exhaust.PlayAnimation());
                else node.QueueFree();
            }
            else
            {
                var fly=NCardFlyVfx.Create(node,pile,true,card.Owner.Character.TrailPath);
                if(fly!=null)NCombatRoom.Instance.Ui.AddChild(fly);
                else node.QueueFree();
            }
            animated=true;
        }
        if(animated)__result=(__result.Item1,true);
    }
}

// Bulk Add removes backend entries before it awaits hooks/exhaust effects and
// starts pile tweens. Preserve their real holders across that asynchronous gap,
// so periodic shared-hand refresh cannot dispose them before vanilla takes over.
[HarmonyPatch(typeof(CardPileCmd),nameof(CardPileCmd.Add),new[]{typeof(IEnumerable<CardModel>),typeof(CardPile),typeof(CardPilePosition),typeof(AbstractModel),typeof(bool),typeof(bool)})]
internal static class SharedPileViewLifetime
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(IEnumerable<CardModel> cards,bool skipVisuals,out CardModel[] __state)=>__state=SharedPileVisuals.Hold(cards,skipVisuals);
    private static void Postfix(ref Task<IReadOnlyList<CardPileAddResult>> __result,CardModel[] __state)
    {if(__state.Length>0)__result=SharedPileVisuals.Finish(__result,__state);}
    private static void Finalizer(Exception? __exception,CardModel[]? __state)
    {if(__exception!=null)SharedPileVisuals.Release(__state);}
}

