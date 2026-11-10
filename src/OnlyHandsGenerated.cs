using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

// Redirect newly generated combat cards, not cards moved from existing piles.
// Keep the actual creator unchanged so normal creator hooks still identify the
// payer. Nested scopes restore their predecessor even across async awaits.
internal static class OnlyHandsGenerated
{
    private sealed class Scope : IDisposable
    {
        private readonly (Player payer, Player holder)? _old;
        internal Scope(Player payer,Player holder) { _old=Current.Value;Current.Value=(payer,holder); }
        public void Dispose()=>Current.Value=_old;
    }
    private static readonly AsyncLocal<(Player payer,Player holder)?> Current=new();
    internal static IDisposable? Track(Player payer,Player holder) =>
        RuleSession.Ready && (RuleSession.Current.Advanced is {Enabled:true} custom
            ? custom.GeneratedForOriginalHolder : RuleSession.Current.OnlyHands) && payer!=holder ? new Scope(payer,holder) : null;
    internal static void Redirect(ref IEnumerable<CardModel> cards,Player? creator)
    {
        // Only card generation explicitly credited to the acting player is
        // part of the borrowed play. Enemy powers can generate cards with no
        // player creator during the same play; keep their original recipient.
        if(Current.Value is not { } scope || creator!=scope.payer)return;
        var list=cards.ToList();
        foreach(var card in list)
            if(card.Pile==null && card.Owner==scope.payer && scope.holder.Creature.IsAlive)
                card.GiveToAnotherPlayer(scope.holder);
        cards=list;
    }
}

[HarmonyPatch(typeof(CardPileCmd),nameof(CardPileCmd.AddGeneratedCardsToCombat))]
internal static class OnlyHandsGeneratedPatch
{
    private static void Prefix(ref IEnumerable<CardModel> cards,Player? creator) => OnlyHandsGenerated.Redirect(ref cards,creator);
}
