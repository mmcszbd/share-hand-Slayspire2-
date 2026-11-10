using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace SharedHandPrototype;

internal static class SharedDeckSync
{
    // Model identities, not names or array indices. Both starter copies of a
    // Strike remain distinct; the mirror of one Strike shares its saved ID.
    internal static readonly PersistentValue<CardModel, string> Identity = new(() => "", "SharedHand_v6_Card");
    private static readonly PersistentValue<Player, int> Counter = NativeSavedState.Counter;
    private static readonly ConditionalWeakTable<IRunState, object> Initialized = new();
    private sealed class Tracker
    {
        internal readonly Dictionary<string, Dictionary<ulong, CardModel>> Cards = new();
        internal readonly Dictionary<string, ulong> Removed = new();
    }
    private static readonly ConditionalWeakTable<IRunState, Tracker> Tracked = new();
    private static int _busy;
    internal static void Suspend() => _busy++;
    internal static void Resume() => _busy--;
    internal static bool Enabled(IRunState? run) => ReceivesAcquisitions(run) &&
        RuleSession.Current.Advanced is not {Enabled:true,DeckSync:AdvancedDeckSync.AcquisitionsOnly};
    private static bool ReceivesAcquisitions(IRunState? run) => run?.Players.Count is >= 2 and <= PartyLimits.MaxPlayers && RuleSession.Ready &&
        RuleSession.Current.ShareDeck && Initialized.TryGetValue(run, out _);
    internal static void Initialize(IRunState run, bool reconcile = false)
    {
        StartingDeckRules.Apply(run);
        bool existing = Initialized.TryGetValue(run, out _);
        if (run.Players.Count is not (>= 2 and <= PartyLimits.MaxPlayers) || !RuleSession.Current.ShareDeck || (existing && !reconcile)) return;
        _busy++;
        try
        {
            // Saved synchronized decks already carry the same identities. An
            // older unsynchronized save contributes each physical starter once.
            foreach (var player in run.Players)
                foreach (var card in player.Deck.Cards)
                    if (string.IsNullOrEmpty(Identity.Get(card))) Identity.Set(card, Next(player));
            if(RuleSession.Current.Advanced is {Enabled:true,DeckSync:AdvancedDeckSync.AcquisitionsOnly})
            {
                if(!existing)Initialized.Add(run,new object());
                return;
            }
            var logical = run.Players.SelectMany(p => p.Deck.Cards).GroupBy(Identity.Get).Select(g => g.First()).ToArray();
            foreach (var player in run.Players)
            {
                foreach (var card in logical)
                    if (!player.Deck.Cards.Any(c => Identity.Get(c) == Identity.Get(card))) Copy(card, player);
                Sort(player.Deck);
            }
            if (!existing) Initialized.Add(run, new object());
            foreach (var card in run.Players.SelectMany(p => p.Deck.Cards)) Remember(card);
        }
        finally { _busy--; }
    }
    private static string Next(Player player)
    {
        int value = checked(Counter.Get(player) + 1);
        Counter.Set(player, value);
        return player.NetId.ToString("D20") + ":" + value.ToString("D10");
    }
    private static CardModel Copy(CardModel source, Player owner)
    {
        var copy = owner.RunState.LoadCard(source.ToSerializable(), owner);
        Identity.Set(copy, Identity.Get(source));
        owner.Deck.AddInternal(copy);
        owner.Deck.InvokeCardAddFinished();
        return copy;
    }
    private static void Sort(CardPile deck)
    {
        // Stable across peers even when two different reward messages arrive
        // during the same frame. Preserve native objects referenced by UI.
        var cards = (List<CardModel>)AccessTools.Field(typeof(CardPile), "_cards").GetValue(deck)!;
        cards.Sort((a, b) => StringComparer.Ordinal.Compare(Identity.Get(a), Identity.Get(b)));
        deck.InvokeContentsChanged();
    }
    internal static void Added(CardPile pile, CardModel card)
    {
        // New acquisitions made while mirroring is disabled are distinct when
        // synchronization is enabled again; cloned extended IDs must not merge.
        if (_busy == 0 && pile.Type == PileType.Deck && card.Owner != null && RuleSession.Ready &&
            !RuleSession.Current.ShareDeck && Initialized.TryGetValue(card.Owner.RunState, out _)) Identity.Set(card, "");
        if (_busy != 0 || pile.Type != PileType.Deck || card.Owner == null || !ReceivesAcquisitions(card.Owner.RunState)) return;
        if(AdvancedDeckTransform.SuppressAcquisition)
        {Identity.Set(card,Next(card.Owner));return;}
        _busy++;
        try
        {
            // Native duplicate effects must get a fresh logical card, even if
            // a clone copied the source's extended saved properties.
            Identity.Set(card, Next(card.Owner));
            foreach (var other in card.Owner.RunState.Players.Where(p => p != card.Owner)) Copy(card, other);
            foreach (var player in card.Owner.RunState.Players) Sort(player.Deck);
            foreach (var player in card.Owner.RunState.Players)
                Remember(player.Deck.Cards.Single(c => Identity.Get(c) == Identity.Get(card)));
        }
        finally { _busy--; }
    }
    internal static void Removing(CardPile pile, CardModel card)
    {
        if (_busy != 0 || pile.Type != PileType.Deck || card.Owner == null || !Enabled(card.Owner.RunState)) return;
        _busy++;
        try
        {
            var identity = Identity.Get(card);
            if (string.IsNullOrEmpty(identity)) return;
            var tracking = Tracked.GetOrCreateValue(card.Owner.RunState);
            tracking.Removed[identity] = card.Owner.NetId;
            tracking.Cards.Remove(identity);
            foreach (var other in card.Owner.RunState.Players.Where(p => p != card.Owner))
                foreach (var mirror in other.Deck.Cards.Where(c => Identity.Get(c) == identity).ToArray())
                {
                    other.Deck.RemoveInternal(mirror);
                    mirror.RemoveFromState();
                }
        }
        finally { _busy--; }
    }
    private static void Remember(CardModel card)
    {
        if (card.Owner == null || string.IsNullOrEmpty(Identity.Get(card))) return;
        var tracking = Tracked.GetOrCreateValue(card.Owner.RunState);
        var id = Identity.Get(card);
        if (!tracking.Cards.TryGetValue(id, out var mirrors)) tracking.Cards[id] = mirrors = new();
        mirrors[card.Owner.NetId] = card;
    }
    internal static void Rebind(Player player)
    {
        if (!Enabled(player.RunState)) return;
        foreach (var card in player.Deck.Cards) Remember(card);
    }
    internal static string[] LocalDeletions(IRunState run, ulong controller)
    {
        if (!Enabled(run)) return Array.Empty<string>();
        var tracking = Tracked.GetOrCreateValue(run);
        var deleted = tracking.Removed.Where(p => PuppetCompat.MayAct(controller, p.Value)).Select(p => p.Key).ToHashSet();
        foreach (var player in run.Players.Where(p => PuppetCompat.MayAct(controller, p.NetId)))
            foreach (var pair in tracking.Cards)
                if (pair.Value.ContainsKey(player.NetId) && !player.Deck.Cards.Any(c => Identity.Get(c) == pair.Key)) deleted.Add(pair.Key);
        return deleted.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    }
    internal static void ApplyDeletions(IRunState run, IEnumerable<string> deleted)
    {
        if (!Enabled(run)) return;
        var tracking = Tracked.GetOrCreateValue(run);
        _busy++;
        try
        {
            bool notified = false;
            foreach (var id in deleted.Distinct())
            {
                notified = true;
                var stale = tracking.Cards.TryGetValue(id, out var mirrors) ? mirrors.Values.ToArray() : Array.Empty<CardModel>();
                foreach (var player in run.Players)
                    foreach (var card in player.Deck.Cards.Where(c => Identity.Get(c) == id).ToArray())
                    { player.Deck.RemoveInternal(card); card.RemoveFromState(); }
                foreach (var card in stale)
                    if (card.Owner != null && card.Pile == null && run.ContainsCard(card)) card.RemoveFromState();
                tracking.Cards.Remove(id); tracking.Removed.Remove(id);
            }
            if (notified) foreach (var player in run.Players) player.Deck.InvokeContentsChanged();
        }
        finally { _busy--; }
    }
    internal static void Changed(CardModel card)
    {
        if (_busy != 0 || card.Owner == null || card.Pile?.Type != PileType.Deck || !Enabled(card.Owner.RunState)) return;
        _busy++;
        try
        {
            foreach (var other in card.Owner.RunState.Players.Where(p => p != card.Owner))
                foreach (var mirror in other.Deck.Cards.Where(c => Identity.Get(c) == Identity.Get(card)).ToArray())
                {
                    // Keep the mirror object alive: a native selector might
                    // still reference it. Copy upgrades and enchantments in place.
                    if (mirror.CurrentUpgradeLevel > card.CurrentUpgradeLevel) mirror.DowngradeInternal();
                    while (mirror.CurrentUpgradeLevel < card.CurrentUpgradeLevel) mirror.UpgradeInternal();
                    mirror.FinalizeUpgradeInternal();
                    var enchantment = card.Enchantment?.ToSerializable();
                    mirror.ClearEnchantmentInternal();
                    if (enchantment != null)
                    {
                        mirror.EnchantInternal(EnchantmentModel.FromSerializable(enchantment), enchantment.Amount);
                        mirror.Enchantment!.ModifyCard();
                        mirror.FinalizeUpgradeInternal();
                    }
                    other.Deck.InvokeContentsChanged();
                }
        }
        finally { _busy--; }
    }
}

// Native combat resynchronization reloads a complete serialized player. It is
// replacement, not hundreds of new acquisitions/deletions. Preserve saved IDs
// and do not recursively mirror the temporary clear/repopulate operations.
[HarmonyPatch(typeof(Player), nameof(Player.SyncWithSerializedPlayer))]
internal static class SharedDeckSnapshotPatch
{
    private static void Prefix() => SharedDeckSync.Suspend();
    private static void Postfix(Player __instance) => SharedDeckSync.Rebind(__instance);
    private static void Finalizer() => SharedDeckSync.Resume();
}

[HarmonyPatch(typeof(CardPile), nameof(CardPile.AddInternal))]
internal static class SharedDeckAddedPatch
{
    private static void Postfix(CardPile __instance, CardModel card) => SharedDeckSync.Added(__instance, card);
}
[HarmonyPatch(typeof(CardPile), nameof(CardPile.RemoveInternal))]
internal static class SharedDeckRemovedPatch
{
    private static void Prefix(CardPile __instance, CardModel card)
    { if (__instance.Cards.Contains(card)) SharedDeckSync.Removing(__instance, card); }
}
[HarmonyPatch]
internal static class SharedDeckChangedPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        foreach (var name in new[] { "UpgradeInternal", "DowngradeInternal", "EnchantInternal", "ClearEnchantmentInternal" })
            yield return AccessTools.Method(typeof(CardModel), name);
    }
    private static void Postfix(CardModel __instance) => SharedDeckSync.Changed(__instance);
}





