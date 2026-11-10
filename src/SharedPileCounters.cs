using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace SharedHandPrototype;

// Native counters advance at animation-finished events. Silent pile movement
// intentionally emits none. Repair only the UI cache, without replaying pile
// events/hooks or inventing another exhaust. Ordinary fly/bump animations stay.
internal static class SharedPileCounters
{
    private static readonly FieldInfo Pile = AccessTools.Field(typeof(NCombatCardPile), "_pile");
    private static readonly FieldInfo Local = AccessTools.Field(typeof(NCombatCardPile), "_localPlayer");
    private static readonly FieldInfo Count = AccessTools.Field(typeof(NCombatCardPile), "_currentCount");
    private static readonly FieldInfo Label = AccessTools.Field(typeof(NCombatCardPile), "_countLabel");
    private static ulong _lastCheck, _busyUntil;

    internal static void Tick()
    {
        if (!Entry.Ready || NCombatRoom.Instance == null) return;
        var now = Time.GetTicksMsec();
        if (RunManager.Instance.ActionExecutor.IsRunning) { _busyUntil = now + 700; return; }
        if (now < _busyUntil || now - _lastCheck < 150) return;
        _lastCheck = now; Refresh();
    }

    internal static void Refresh()
    {
        if (!Entry.Ready || NCombatRoom.Instance is not { } room ||
            MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState() is not { } state ||
            LocalContext.GetMe(state) is not { PlayerCombatState: not null } actor) return;
        Update(room.Ui.DrawPile, actor, PileType.Draw);
        Update(room.Ui.DiscardPile, actor, PileType.Discard);
        Update(room.Ui.ExhaustPile, actor, PileType.Exhaust);
    }

    private static void Update(NCombatCardPile button, Player actor, PileType kind)
    {
        if (!GodotObject.IsInstanceValid(button) || !button.IsNodeReady()) return;
        var pile = CardPile.Get(kind, actor)!;
        if (!ReferenceEquals(Pile.GetValue(button), pile) || !ReferenceEquals(Local.GetValue(button), actor))
        {
            // Perspective switches must display/open the same actual pile.
            // Detach the old callbacks before using native initialization.
            if (Pile.GetValue(button) is CardPile old)
            {
                old.CardAddFinished -= (Action)Delegate.CreateDelegate(typeof(Action), button, AccessTools.Method(button.GetType(), "AddCard"));
                old.CardRemoveFinished -= (Action)Delegate.CreateDelegate(typeof(Action), button, AccessTools.Method(typeof(NCombatCardPile), "RemoveCard"));
            }
            button.Initialize(actor);
        }
        int actual = pile.Cards.Count;
        if ((int)Count.GetValue(button)! != actual)
        {
            Count.SetValue(button, actual);
            var label = (Control)Label.GetValue(button)!;
            AccessTools.Method(label.GetType(), "SetTextAutoSize", new[] { typeof(string) }).Invoke(label, new object[] { actual.ToString() });
            label.PivotOffset = label.Size * .5f;
        }
        if (button is NExhaustPileButton && actual > 0)
        {
            if (!button.Visible) button.AnimIn();
            button.Enable();
        }
    }

    internal static async Task<IReadOnlyList<CardPileAddResult>> AfterSilent(Task<IReadOnlyList<CardPileAddResult>> task)
    {
        var result = await task;
        Refresh();
        return result;
    }
}

[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.Add), new[] { typeof(IEnumerable<CardModel>), typeof(CardPile), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool), typeof(bool) })]
internal static class SilentPileCounterPatch
{
    private static void Postfix(bool skipVisuals, ref Task<IReadOnlyList<CardPileAddResult>> __result)
    {
        if (skipVisuals && Entry.Ready) __result = SharedPileCounters.AfterSilent(__result);
    }
}

[HarmonyPatch(typeof(NCombatCardPile), "OnRelease")]
internal static class SharedPileOpenCounterPatch
{
    private static void Prefix() => SharedPileCounters.Refresh();
}

