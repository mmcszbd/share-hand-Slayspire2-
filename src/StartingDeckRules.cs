using System;
using System.Collections.Generic;
using System.Linq;

using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace SharedHandPrototype;

internal static class StartingDeckRules
{
    internal static readonly PersistentValue<IRunState, bool> Adjusted = new(() => false, "SharedHand_v6_StartingDeckAdjusted");
    internal static void Apply(IRunState run)
    {
        if (run.Players.Count is not (>= 2 and <= PartyLimits.MaxPlayers) || !RuleSession.Ready || Adjusted.Get(run)) return;
        var rules = RuleSession.Current;
        // Mark once even with both options disabled, so reloading or later
        // menu changes can never shrink rewards already added to a deck.
        Adjusted.Set(run, true);
        if (!rules.SmallDeck && !rules.SmallerDeck) return;
        var reference = run.Players.OrderByDescending(p => p.Deck.Cards.Count(c => c.IsBasicStrikeOrDefend &&
            (c.Tags.Contains(CardTag.Strike) || c.Tags.Contains(CardTag.Defend)))).First();
        var referenceCounts = new[] { CardTag.Strike, CardTag.Defend }.ToDictionary(tag => tag,
            tag => reference.Deck.Cards.Count(c => c.IsBasicStrikeOrDefend && c.Tags.Contains(tag)));
        foreach (var tag in new[] { CardTag.Strike, CardTag.Defend })
        {
            var groups = run.Players.Select(p => p.Deck.Cards.Where(c => c.IsBasicStrikeOrDefend && c.Tags.Contains(tag)).ToArray()).ToArray();
            var keep = new HashSet<CardModel>();
            if (rules.SmallerDeck)
            {
                int count = referenceCounts[tag];
                int each = count / groups.Length;
                foreach (var group in groups) foreach (var card in group.Take(each)) keep.Add(card);
                var remainder = groups.SelectMany(g => g.Skip(each).Take(1)).ToList();
                while (keep.Count < count && remainder.Count > 0)
                {
                    int index = run.Rng.UpFront.NextInt(remainder.Count);
                    keep.Add(remainder[index]); remainder.RemoveAt(index);
                }
                // A mod character may start with zero of a basic type. Fill
                // the remainder from available cards without inventing copies.
                foreach (var card in groups.SelectMany(g => g))
                    if (keep.Count < count) keep.Add(card);
            }
            else
            {
                // Reduce the team's combined basics by half, then distribute
                // the result equally. Rounding each original deck separately
                // biases unequal starts (3+5 must produce 2+2, not 2+3).
                int count = (int)Math.Round(groups.Sum(g => g.Length) / 2m / run.Players.Count,
                    MidpointRounding.AwayFromZero);
                foreach (var group in groups)
                {
                    foreach (var card in group.Take(count)) keep.Add(card);
                }
            }
            foreach (var card in groups.SelectMany(g => g).Where(c => !keep.Contains(c)))
            {
                card.Owner.Deck.RemoveInternal(card);
                card.RemoveFromState();
            }
        }
    }
}





