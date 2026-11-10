using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SharedHandPrototype;

internal static class SharedQueueVisuals
{
    private static readonly Dictionary<NCard, ulong> Orphans = new();
    internal static void Refresh()
    {
        var queue = NCardPlayQueue.Instance;
        if (!HandSharingMode.Shared || queue == null || !Entry.Ready) return;
        var list = (IList)AccessTools.Field(typeof(NCardPlayQueue), "_playQueue").GetValue(queue)!;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var item = list[i]!;
            var action = (GameAction)AccessTools.Field(item.GetType(), "action").GetValue(item)!;
            var card = (NCard)AccessTools.Field(item.GetType(), "card").GetValue(item)!;
            bool ended = action.State is GameActionState.Finished or GameActionState.Canceled;
            if (!ended && GodotObject.IsInstanceValid(card) && card.Model?.Pile?.Type is PileType.Discard or PileType.Exhaust &&
                action.State == GameActionState.None && !SharedRules.Queued.Contains(NetCombatCardDb.Instance.GetCardId(card.Model))) ended = true;
            if (!ended) continue;
            AccessTools.Method(typeof(NCardPlayQueue), "RemoveCardFromQueue", new[] { typeof(int) }).Invoke(queue, new object[] { i });
            if (GodotObject.IsInstanceValid(card) && card.GetParent() == queue) Retire(card);
        }
        if (SharedRules.Queued.Count > 0 || RunManager.Instance.ActionExecutor.CurrentlyRunningAction != null) return;
        var container = NCombatRoom.Instance.Ui.PlayContainer;
        foreach (var card in container.GetChildren().OfType<NCard>())
        {
            if (card.Model?.Pile?.Type == PileType.Play) { Orphans.Remove(card); continue; }
            if (!Orphans.TryGetValue(card, out var since)) Orphans[card] = Time.GetTicksMsec();
            else if (Time.GetTicksMsec() - since > 1500) { Retire(card); Orphans.Remove(card); }
        }
        foreach (var card in Orphans.Keys.Where(c => !GodotObject.IsInstanceValid(c) || c.GetParent() != container).ToArray()) Orphans.Remove(card);
    }
    private static void Retire(NCard node)
    {
        var hand = NPlayerHand.Instance;
        var card = node.Model;
        if (card?.Pile?.Type == PileType.Hand && hand != null && SharedHandSemantics.All().Contains(card) &&
            hand.GetCardHolder(card) == null && !SharedSelection.InSelectionView(card))
            hand.Add(node);
        else node.QueueFree();
    }
}




