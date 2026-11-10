using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

// Sly is a real automatic play. Its actor is the player causing the discard,
// while the physical card still returns to its original holder's result pile.
[HarmonyPatch(typeof(CardCmd),nameof(CardCmd.AutoPlay))]
internal static class SharedSlyActor
{
    [ThreadStatic] private static HashSet<CardModel>? _invoking;
    internal static Player? Actor(PlayerChoiceContext context,CardModel card)
    {
        if(context.LastInvolvedModel is CardModel source)return source.Owner;
        if(context.LastInvolvedModel is RelicModel relic)return relic.Owner;
        if(context.LastInvolvedModel is PotionModel potion)return potion.Owner;
        var state=card.CombatState??card.Owner.Creature.CombatState;
        return context is GameActionPlayerChoiceContext && context.OwnerId is ulong id?state?.Players.FirstOrDefault(p=>p.NetId==id):null;
    }
    private static bool Prefix(PlayerChoiceContext choiceContext,CardModel card,Creature? target,AutoPlayType type,
        bool skipXCapture,bool skipCardPileVisuals,ref Task __result)
    {
        if(type!=AutoPlayType.SlyDiscard||!SharedHandSemantics.Active||_invoking?.Contains(card)==true)return true;
        var actor=Actor(choiceContext,card);
        if(actor==null||actor==card.Owner||card.Pile?.IsCombatPile!=true)return true;
        __result=Execute(card,actor,()=>
        {
            (_invoking??=new()).Add(card);
            try{return CardCmd.AutoPlay(choiceContext,card,target,type,skipXCapture,skipCardPileVisuals);}
            finally{_invoking.Remove(card);}
        });
        return false;
    }
    internal static async Task Execute(CardModel card,Player actor,Func<Task> play)
    {
        var holder=card.Owner;
        var combat=CombatManager.Instance.CurrentCombatId;
        bool hadHolder=SharedRules.PhysicalHolders.TryGetValue(card,out var previous);
        SharedRules.PhysicalHolders[card]=SharedRules.PhysicalHolder(card);
        try
        {
            SharedRules.MoveSameCard(card,actor,card.Pile!.Type);
            await play();
        }
        finally
        {
            if(card.Owner==actor)
            {
                if(card.Pile is {IsCombatPile:true} result&&CombatManager.Instance.CurrentCombatId==combat)
                    SharedRules.MoveSameCard(card,holder,result.Type);
                else card.GiveToAnotherPlayer(holder);
            }
            if(hadHolder)SharedRules.PhysicalHolders[card]=previous!;
            else SharedRules.PhysicalHolders.Remove(card);
            SharedPanel.MarkDirty();
        }
    }
}

