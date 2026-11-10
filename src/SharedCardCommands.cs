using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

// Native DiscardAndDraw assumes every card belongs to the first card's owner.
// This batch operation preserves each physical owner and defers Sly auto-play
// until all discards and all replacement draws complete, just like vanilla.
[HarmonyPatch(typeof(CardCmd), nameof(CardCmd.DiscardAndDraw))]
internal static class SharedDiscardCommandPatch
{
    private static bool Prefix(PlayerChoiceContext choiceContext, IEnumerable<CardModel> cardsToDiscard, int cardsToDraw, ref Task __result)
    {
        if (!SharedHandSemantics.Active) return true;
        __result = Execute(choiceContext, cardsToDiscard, cardsToDraw);
        return false;
    }
    private static async Task Execute(PlayerChoiceContext context, IEnumerable<CardModel> cards, int draws)
    {
        if (CombatManager.Instance.IsOverOrEnding) return;
        var requested = cards.Distinct().ToList();
        bool equalBatch = draws == requested.Count;
        var snapshot = requested.Where(c => c.Pile?.IsCombatPile == true && c.Pile.Type != PileType.Discard)
            .Select(c => (card: c, holder: SharedRules.PhysicalHolder(c),
                drawer: SharedChipRecipient.NeutralDrawer is {} chip && DrawIdentity.Drawer(c)==null ? chip : SharedRules.PhysicalHolder(c),
                pile: c.Pile!, generation: HandEntries.Get(c))).ToList();
        if (snapshot.Count == 0) return;
        var state = snapshot[0].card.CombatState!;
        var counts = new Dictionary<Player, int>();
        var sly = new List<CardModel>();
        foreach (var entry in snapshot)
        {
            var card = entry.card;
            // Discard hooks can pause for a choice and let another player act.
            // A card which has left/reentered its source is no longer part of
            // this batch, even if it still has the same object/network ID.
            if (card.Pile != entry.pile || (entry.pile.Type == PileType.Hand && HandEntries.Get(card) != entry.generation)) continue;
            bool wasSly=card.IsSlyThisTurn;
            var result=await CardPileCmd.Add(card, PileType.Discard.GetPile(entry.holder));
            if (!result.success || card.Pile != entry.holder.PlayerCombatState!.DiscardPile) continue;
            if(wasSly)sly.Add(card);
            counts[entry.drawer] = counts.GetValueOrDefault(entry.drawer) + 1;
            CombatManager.Instance.History.CardDiscarded(state, card);
            await Hook.AfterCardDiscarded(state, context, card);
        }
        foreach (var holder in snapshot.Select(x=>x.holder).Distinct()) holder.PlayerCombatState!.DiscardPile.InvokeContentsChanged();
        int total = counts.Values.Sum();
        if (equalBatch) draws = total;
        if (draws > 0 && total > 0)
        {
            // Equal discard/draw batches give each holder exactly its count.
            // Other batches distribute their requested draw count proportionally.
            var allocation = counts.ToDictionary(x => x.Key, x => draws * x.Value / total);
            int remainder = draws - allocation.Values.Sum();
            foreach (var holder in counts.Keys)
                if (remainder-- > 0) allocation[holder]++;
            foreach (var entry in allocation)
                if (entry.Value > 0) await CardPileCmd.Draw(context, entry.Value, entry.Key);
        }
        foreach (var card in sly) await CardCmd.AutoPlay(context, card, null, AutoPlayType.SlyDiscard);
    }
}

[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.Add), new[] {
    typeof(IEnumerable<CardModel>), typeof(PileType), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool) })]
internal static class SharedBatchMovePatch
{
    private static bool Prefix(IEnumerable<CardModel> cards, PileType newPileType, CardPilePosition position,
        AbstractModel? clonedBy, bool skipVisuals, ref Task<IReadOnlyList<CardPileAddResult>> __result)
    {
        if (!SharedHandSemantics.Active || newPileType == PileType.Deck) return true;
        var snapshot = cards.ToList();
        if (snapshot.Select(c => c.Owner).Distinct().Count() <= 1) return true;
        __result = Move(snapshot, newPileType, position, clonedBy, skipVisuals);
        return false;
    }
    private static async Task<IReadOnlyList<CardPileAddResult>> Move(List<CardModel> cards, PileType type,
        CardPilePosition position, AbstractModel? clonedBy, bool skipVisuals)
    {
        var result = new List<CardPileAddResult>();
        foreach (var group in cards.GroupBy(c => c.Owner))
            result.AddRange(await CardPileCmd.Add(group, CardPile.Get(type, group.Key)!, position, clonedBy, skipVisuals));
        return result;
    }
}




