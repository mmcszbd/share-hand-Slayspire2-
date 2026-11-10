using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.TestSupport;

namespace SharedHandPrototype;

internal static class SharedSelection
{
    private static readonly FieldInfo Selected = AccessTools.Field(typeof(NPlayerHand), "_selectedCards");
    private static readonly FieldInfo Container = AccessTools.Field(typeof(NPlayerHand), "_selectedHandCardContainer");
    private static readonly FieldInfo Confirm = AccessTools.Field(typeof(NPlayerHand), "_selectModeConfirmButton");
    private static readonly FieldInfo Filter = AccessTools.Field(typeof(NPlayerHand), "_currentSelectionFilter");
    private static readonly FieldInfo Prefs = AccessTools.Field(typeof(NPlayerHand), "_prefs");
    private static readonly Dictionary<CardModel, ulong> Versions = new();
    private static bool _pruning;
    private static int _minimum;
    internal static bool Candidate(CardModel card)
    {
        if (card.Pile?.Type != PileType.Hand || !SharedHandSemantics.All().Contains(card)) return false;
        return !NetCombatCardDb.Instance.TryGetCardId(card, out var id) ||
            !SharedRules.Queued.Contains(id);
    }
    internal static bool Allowed(CardModel card)=>Candidate(card)&&
        (NPlayerHand.Instance is not {} hand||!hand.IsInCardSelection||((Func<CardModel,bool>?)Filter.GetValue(hand))?.Invoke(card)!=false);
    internal static bool AllowedFor(Player player,CardModel card) => Candidate(card) &&
        (!SharedHandSemantics.PersonalEffectScope || ReferenceEquals(card.Pile,player.PlayerCombatState?.Hand));
    internal static void Begin(CardSelectorPrefs prefs, ref Func<CardModel, bool>? filter)
    {
        if (!SharedHandSemantics.Active) return;
        if (NPlayerHand.Instance is { } hand) CleanupSource(hand);
        Versions.Clear(); _minimum = prefs.MinSelect;
        var original = filter;
        filter = c => Candidate(c) && (original?.Invoke(c) ?? true);
    }
    internal static void Remember()
    {
        var hand = NPlayerHand.Instance;
        if (hand == null || !hand.IsInCardSelection) return;
        foreach (var card in (List<CardModel>)Selected.GetValue(hand)!)
            Versions.TryAdd(card, HandEntries.Get(card));
    }
    internal static bool InSelectionView(CardModel card)
    {
        var hand = NPlayerHand.Instance;
        if (hand == null) return false;
        if (((List<CardModel>)Selected.GetValue(hand)!).Contains(card)) return true;
        var preview = AccessTools.Field(typeof(NPlayerHand), "_upgradePreview").GetValue(hand);
        return preview != null && ReferenceEquals(AccessTools.Property(preview.GetType(), "Card").GetValue(preview), card);
    }
    internal static void Prune()
    {
        var hand = NPlayerHand.Instance;
        if (_pruning || TestMode.IsOn || !SharedHandSemantics.Active || hand?.IsInCardSelection != true) return;
        _pruning = true;
        try
        {
            var selected = (List<CardModel>)Selected.GetValue(hand)!;
            var filter = (Func<CardModel, bool>?)Filter.GetValue(hand);
            foreach (var card in selected.Where(c => !Candidate(c) ||
                filter?.Invoke(c) == false ||
                (Versions.TryGetValue(c, out var version) && version != HandEntries.Get(c))).ToArray())
            {
                selected.Remove(card); Versions.Remove(card);
                var holder = ((Control)Container.GetValue(hand)!).GetChildren().OfType<NCardHolder>()
                    .FirstOrDefault(h => ReferenceEquals(h.CardNode?.Model, card));
                if (holder != null) hand.RemoveCardHolder(holder);
                var preview = AccessTools.Field(typeof(NPlayerHand), "_upgradePreview").GetValue(hand)!;
                var previewCard = AccessTools.Property(preview.GetType(), "Card");
                if (ReferenceEquals(previewCard.GetValue(preview), card))
                {
                    previewCard.SetValue(preview, null);
                    ((Control)AccessTools.Field(typeof(NPlayerHand), "_upgradePreviewContainer").GetValue(hand)!).Visible = false;
                }
            }
            int count = SharedHandSemantics.All().Count(c => Candidate(c) && (filter?.Invoke(c) ?? true));
            var prefs = (CardSelectorPrefs)Prefs.GetValue(hand)!;
            var adjusted = new CardSelectorPrefs(prefs.Prompt, Math.Min(_minimum, count), prefs.MaxSelect)
            {
                RequireManualConfirmation = prefs.RequireManualConfirmation, Cancelable = prefs.Cancelable,
                Comparison = prefs.Comparison, UnpoweredPreviews = prefs.UnpoweredPreviews,
                PretendCardsCanBePlayed = prefs.PretendCardsCanBePlayed, ShouldGlowGold = prefs.ShouldGlowGold
            };
            Prefs.SetValue(hand, adjusted);
            AccessTools.Method(typeof(NPlayerHand), "UpdateSelectModeCardVisibility").Invoke(hand, null);
            AccessTools.Method(typeof(NPlayerHand), "RefreshSelectModeConfirmButton").Invoke(hand, null);
            if (selected.Count == 0 && count == 0)
                ((TaskCompletionSource<IEnumerable<CardModel>>?)AccessTools.Field(typeof(NPlayerHand), "_selectionCompletionSource").GetValue(hand))?.TrySetResult(Array.Empty<CardModel>());
        }
        finally { _pruning = false; }
    }
    internal static void ConfirmVisibility(NPlayerHand hand)
    {
        if (!SharedHandSemantics.Active || !hand.IsInCardSelection) return;
        var selected = (List<CardModel>)Selected.GetValue(hand)!;
        var prefs = (CardSelectorPrefs)Prefs.GetValue(hand)!;
        ((Control)Confirm.GetValue(hand)!).Visible = selected.Count >= prefs.MinSelect &&
            selected.Count <= prefs.MaxSelect && selected.All(Candidate) && (selected.Count > 0 || _minimum == 0);
    }
    internal static void CleanupSource(NPlayerHand hand)
    {
        if (!SharedHandSemantics.Active) return;
        var container = (Control)Container.GetValue(hand)!;
        foreach (var holder in container.GetChildren().OfType<NCardHolder>().ToArray())
            if(holder.CardNode?.Model is CardModel transforming && SharedCombatTransforms.KeepOriginal(transforming))continue;
            else if (holder.IsQueuedForDeletion()) container.RemoveChild(holder);
            else if (holder.CardNode?.Model is not CardModel card || !Candidate(card))
                hand.RemoveCardHolder(holder);
        var preview = AccessTools.Field(typeof(NPlayerHand), "_upgradePreview").GetValue(hand)!;
        var property = AccessTools.Property(preview.GetType(), "Card");
        if (property.GetValue(preview) is CardModel selected && !Candidate(selected))
        {
            property.SetValue(preview, null);
            ((Control)AccessTools.Field(typeof(NPlayerHand), "_upgradePreviewContainer").GetValue(hand)!).Visible = false;
        }
    }
}

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHand))]
internal static class SharedChoiceRetryPatch
{
    [ThreadStatic] private static bool _inside;
    private static readonly MethodInfo Original = AccessTools.Method(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHand));
    private static bool Prefix(PlayerChoiceContext context, Player player, CardSelectorPrefs prefs,
        Func<CardModel, bool>? filter, AbstractModel source, ref Task<IEnumerable<CardModel>> __result)
    {
        if (_inside || !SharedHandSemantics.Active) return true;
        __result = Select(context, player, prefs, filter, source);
        return false;
    }
    private static async Task<IEnumerable<CardModel>> Select(PlayerChoiceContext context, Player player,
        CardSelectorPrefs prefs, Func<CardModel, bool>? filter, AbstractModel source)
    {
        var checkedFilter = new Func<CardModel, bool>(c => SharedSelection.AllowedFor(player,c) && (filter?.Invoke(c) ?? true));
        while (!CombatManager.Instance.IsOverOrEnding)
        {
            Task<IEnumerable<CardModel>> task;
            _inside = true;
            try { task = (Task<IEnumerable<CardModel>>)Original.Invoke(null, new object?[] { context, player, prefs, checkedFilter, source })!; }
            finally { _inside = false; }
            var result = (await task).ToList();
            // Native SignalPlayerChoiceEnded resumes in the ordered action
            // queue before this check: both peers retry the same stale choice.
            if (result.All(c=>SharedSelection.AllowedFor(player,c))) return result;
        }
        return Array.Empty<CardModel>();
    }
}

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForUpgrade))]
internal static class SharedUpgradeChoiceRetryPatch
{
    [ThreadStatic] private static bool _inside;
    private static readonly MethodInfo Original = AccessTools.Method(typeof(CardSelectCmd), nameof(CardSelectCmd.FromHandForUpgrade));
    private static bool Prefix(PlayerChoiceContext context, Player player, AbstractModel source, ref Task<CardModel?> __result)
    {
        if (_inside || !SharedHandSemantics.Active) return true;
        __result = Select(context, player, source); return false;
    }
    private static async Task<CardModel?> Select(PlayerChoiceContext context, Player player, AbstractModel source)
    {
        while (!CombatManager.Instance.IsOverOrEnding)
        {
            Task<CardModel?> task;
            _inside = true;
            try { task = (Task<CardModel?>)Original.Invoke(null, new object[] { context, player, source })!; }
            finally { _inside = false; }
            var result = await task;
            if (result == null || (SharedSelection.AllowedFor(player,result) && result.IsUpgradable)) return result;
        }
        return null;
    }
}

[HarmonyPatch]
internal static class SharedRememberSelectionPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NPlayerHand), "SelectCardInSimpleMode");
        yield return AccessTools.Method(typeof(NPlayerHand), "SelectCardInUpgradeMode");
    }
    private static void Postfix() => SharedSelection.Remember();
}
[HarmonyPatch(typeof(NPlayerHand), "RevalidateSelectionAfterStateChange")]
internal static class SharedRevalidationPatch
{
    private static bool Prefix()
    {
        if (!SharedHandSemantics.Active) return true;
        SharedSelection.Prune(); return false;
    }
}
[HarmonyPatch(typeof(NPlayerHand), "RefreshSelectModeConfirmButton")]
internal static class SharedConfirmVisibilityPatch
{
    private static void Postfix(NPlayerHand __instance) => SharedSelection.ConfirmVisibility(__instance);
}
[HarmonyPatch(typeof(NPlayerHand), "OnSelectModeConfirmButtonPressed")]
internal static class SharedConfirmGuardPatch
{
    private static bool Prefix(NPlayerHand __instance)
    {
        if (!SharedHandSemantics.Active) return true;
        SharedSelection.Prune();
        return ((Control)AccessTools.Field(typeof(NPlayerHand), "_selectModeConfirmButton").GetValue(__instance)!).Visible;
    }
}
[HarmonyPatch(typeof(NPlayerHand), "OnSelectModeSourceFinished")]
internal static class SharedSelectedCleanupPatch
{
    private static void Prefix(NPlayerHand __instance) => SharedSelection.CleanupSource(__instance);
}




