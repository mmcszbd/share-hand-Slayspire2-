using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using Label = Godot.Label;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;

namespace SharedHandPrototype;
// The hand and its drag animation remain native. Only a deliberate drop on a
// living teammate changes the caster. Enemy-targeted skills ask for their
// original target afterwards, using the same native targeting arrow.
internal static class AllyTargeting
{
    private sealed class Gesture
    {
        internal Player? Recipient;
        internal bool Secondary;
    }

    private static readonly ConditionalWeakTable<NCardPlay, Gesture> Gestures = new();
    private static readonly FieldInfo CurrentPlay = AccessTools.Field(typeof(NPlayerHand), "_currentCardPlay");
    private static readonly MethodInfo Cleanup = AccessTools.Method(typeof(NCardPlay), "Cleanup");
    private static readonly FieldInfo SkipDrag = AccessTools.Field(typeof(NMouseCardPlay), "_skipStartCardDrag");
    private static readonly MethodInfo Center = AccessTools.Method(typeof(NCardPlay), "CenterCard");
    private static readonly MethodInfo Lerp = AccessTools.Method(typeof(NMouseCardPlay), "LerpToMouse");
    private static NMouseCardPlay? _hintPlay;
    private static Label? _hint;
    private static NTargetingArrow? _allyArrow;
    private static NCreature? _hover;
    private static NMultiplayerPlayerState? _hoverPortrait;
    private static NMouseCardPlay? Active => NPlayerHand.Instance is { } h ? CurrentPlay.GetValue(h) as NMouseCardPlay : null;

    private static bool Valid(NCardPlay? play) => AllyPlay.Enabled && play != null && GodotObject.IsInstanceValid(play) && play.IsInsideTree() && !play.IsQueuedForDeletion() && play.Holder?.CardModel is { } card && AllyPlay.Eligible(card);
    internal static bool ActiveGesture=>Valid(Active);
    private static Player? Other(NCardPlay play, Creature? creature) => creature?.Player is { } p && p != LocalContext.GetMe(play.Holder.CardModel.CombatState!) && p.Creature.IsAlive ? p : null;
    private static Player? UnderMouse(NCardPlay play)
    {
        var room = NCombatRoom.Instance;
        if (room == null)
            return null;
        var mouse = play.GetViewport().GetMousePosition();
        if(AllyPortraits.UnderMouse(mouse) is {} row)
            return Other(play,row.Player.Creature);
        foreach (var node in room.CreatureNodes)
            if (Other(play, node.Entity)is { } p && node.Hitbox.GetGlobalRect().HasPoint(mouse))
                return p;
        return null;
    }

    internal static void Input(NMouseCardPlay play, InputEvent input)
    {
        if (!Valid(play) || input is not InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
            return;
        var gesture = Gestures.GetOrCreateValue(play);
        if (gesture.Secondary)
            return;
        // Shortcut/mouse-click play can finish on press; normal drag finishes
        // on release. Never remember a teammate merely passed while dragging.
        if (button.IsReleased() || (button.IsPressed() && (bool)SkipDrag.GetValue(play)!))
        {
            gesture.Recipient = UnderMouse(play);
            if(AllyPortraits.UnderMouse(play.GetViewport().GetMousePosition()) is {} row)
                AllyPortraits.RememberDrop(row);
        }
    }

    internal static bool Allow(Creature creature)
    {
        var play = Active;
        return Valid(play) && play!.Holder.CardModel.TargetType == TargetType.AnyEnemy && !Gestures.GetOrCreateValue(play).Secondary && Other(play, creature) != null;
    }

    // Native history persists across reloads and starts afresh for each run.
    // TurnsTaken is written on combat completion, so fight three still teaches
    // the gesture; the hint disappears after it, including in elite/boss rooms.
    internal static bool ShowTutorial => RunManager.Instance.DebugOnlyGetState() is not { } run ||
        run.MapPointHistory.SelectMany(act => act).SelectMany(point => point.Rooms)
            .Count(room => room.RoomType == RoomType.Monster && room.TurnsTaken > 0) < 3;

    internal static async Task DragOrAim(NMouseCardPlay play, NHandCardHolder holder)
    {
        if (Valid(play) && UnderMouse(play) != null)
        {
            // Match the native ally-targeted card pose while deliberately
            // aiming at a teammate, without changing the center-drop gesture.
            Center.Invoke(play, null);
            await play.ToSignal(play.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        else
            await (Task)Lerp.Invoke(play, new object[] { holder })!;
    }

    internal static void Tick()
    {
        var play = AllyPlay.Enabled ? Active : null;
        if (!Valid(play) || Gestures.GetOrCreateValue(play!).Secondary)
        {
            ClearHint();
            return;
        }

        var recipient = UnderMouse(play!);
        var node = recipient == null ? null : NCombatRoom.Instance.GetCreatureNode(recipient.Creature);
        var portrait=recipient==null?null:AllyPortraits.UnderMouse(play!.GetViewport().GetMousePosition());
        if (play == _hintPlay && node == _hover && portrait==_hoverPortrait)
        {
            if(portrait!=null)AllyPortraits.Highlight(portrait);
            if (node != null && play!.Holder.CardModel.TargetType == TargetType.Self)
                NCombatRoom.Instance.GetCreatureNode(play.Player.Creature)?.HideMultiselectReticle();
            if (_allyArrow != null && GodotObject.IsInstanceValid(_allyArrow) && node != null)
                _allyArrow.UpdateDrawingTo(portrait?.Hitbox.GetGlobalRect().GetCenter()??node.VfxSpawnPosition);
            return;
        }

        ClearHint();
        if (node == null)
            return;
        _hintPlay = play;
        _hover = node;
        _hoverPortrait=portrait;
        if(portrait!=null)AllyPortraits.Highlight(portrait);
        if (play!.Holder.CardModel.TargetType == TargetType.Self)
            NCombatRoom.Instance.GetCreatureNode(play.Player.Creature)?.HideMultiselectReticle();
        node.ShowMultiselectReticle();
        if (play.Holder.CardModel.TargetType != TargetType.AnyEnemy)
        {
            // Reuse native arrow art, curve, ally highlighting and smoothing.
            // This is visual only: no second manager or input owner is created.
            _allyArrow = new NTargetingArrow { Name = "SharedHandAllyArrow", ZIndex = play.Holder.CardNode!.ZIndex + 1 };
            play.AddChild(_allyArrow);
            _allyArrow.StartDrawingFrom(play.Holder.CardNode, true);
            _allyArrow.UpdateDrawingTo(portrait?.Hitbox.GetGlobalRect().GetCenter()??node.VfxSpawnPosition);
            _allyArrow.SetHighlightingOn(false);
        }
        if (!ShowTutorial) return;
        _hint = new Label
        {
            Name = "SharedHandAllyTutorial",
            Text = play.Holder.CardModel.TargetType == TargetType.AnyEnemy ? "交给队友使用，随后选择敌人" : "交给队友使用，由你支付费用",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            Position = new Vector2(0, 180),
            Size = new Vector2(play.GetViewport().GetVisibleRect().Size.X, 45)
        };
        _hint.AddThemeFontSizeOverride("font_size", 26);
        play.AddChild(_hint);
    }

    private static void ClearHint()
    {
        if (_allyArrow != null && GodotObject.IsInstanceValid(_allyArrow))
        {
            _allyArrow.StopDrawing();
            _allyArrow.QueueFree();
        }
        _allyArrow = null;
        if (_hint != null && GodotObject.IsInstanceValid(_hint))
            _hint.QueueFree();
        if (_hover != null && GodotObject.IsInstanceValid(_hover))
            _hover.HideMultiselectReticle();
        if(_hoverPortrait!=null&&GodotObject.IsInstanceValid(_hoverPortrait))_hoverPortrait.OnCreatureUnhovered();
        if (_hintPlay != null && GodotObject.IsInstanceValid(_hintPlay) && _hintPlay.IsInsideTree() && !_hintPlay.IsQueuedForDeletion() && AllyPlay.Enabled && Active == _hintPlay && !Gestures.GetOrCreateValue(_hintPlay).Secondary && _hintPlay.Holder.CardModel.TargetType == TargetType.Self)
            NCombatRoom.Instance?.GetCreatureNode(_hintPlay.Player.Creature)?.ShowMultiselectReticle();
        _hint = null;
        _hover = null;
        _hoverPortrait=null;
        _hintPlay = null;
    }

    internal static bool TryPlay(NCardPlay play, Creature? target)
    {
        if (play is not NMouseCardPlay || !Valid(play))
            return true;
        var gesture = Gestures.GetOrCreateValue(play);
        if (gesture.Secondary)
            return false;
        var recipient = Other(play, target) ?? gesture.Recipient;
        if (recipient == null)
            return true;
        if (play.Holder.CardModel.TargetType == TargetType.AnyEnemy)
        {
            gesture.Secondary = true;
            TaskHelper.RunSafely(ChooseEnemy(play, recipient));
        }
        else
            Send(play, recipient, null);
        return false;
    }

    private static async Task ChooseEnemy(NCardPlay play, Player recipient)
    {
        Label? hint = null;
        try
        {
            // Vanilla completes its Task before clearing the previous arrow.
            // Start the second arrow on the next frame, after that cleanup.
            await play.ToSignal(play.GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!Valid(play))
                return;
            hint = new Label
            {
                Name = "SharedHandAllySecondaryHint",
                Text = "已选择队友：请继续选择敌人",
                MouseFilter = Control.MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Center,
                Position = new Vector2(0, 180),
                Size = new Vector2(play.GetViewport().GetVisibleRect().Size.X, 50)
            };
            hint.AddThemeFontSizeOverride("font_size", 26);
            play.AddChild(hint);
            var manager = NTargetManager.Instance;
            manager.StartTargeting(TargetType.AnyEnemy, play.Holder.CardNode!, TargetMode.ClickMouseToTarget, () => !Valid(play) || !recipient.Creature.IsAlive, node => node is NCreature { Entity.Side: CombatSide.Enemy });
            var node = await manager.SelectionFinished();
            if (!GodotObject.IsInstanceValid(play))
                return;
            if (node is NCreature enemy && recipient.Creature.IsAlive)
                Send(play, recipient, enemy.Entity);
            else
                play.CancelPlayCard();
        }
        finally
        {
            if (hint != null && GodotObject.IsInstanceValid(hint))
                hint.QueueFree();
        }
    }

    private static void Send(NCardPlay play, Player recipient, Creature? target)
    {
        var card = play.Holder.CardModel;
        var payer = LocalContext.GetMe(card.CombatState!);
        if (payer == null || AllyPlay.Validate(card, payer, recipient.NetId, target) != null)
        {
            play.CancelPlayCard();
            return;
        }

        uint id = NetCombatCard.FromModel(card).CombatCardIndex;
        // Retain vanilla cancellation bookkeeping until the host replies.
        // If resources/targets change in transit, rejection must return the
        // dragged holder to the hand rather than leave it awaiting a queue.
        var cancellation = new PlayCardAction(payer, NetCombatCard.FromModel(card), card.Id, target?.CombatId);
        Entry.RequestWithRecipient(id, target?.CombatId, cancellation, recipient.NetId);
        if (Entry.IsAwaiting(id) || SharedRules.Queued.Contains(id))
            Cleanup.Invoke(play, new object[] { true });
        else if (GodotObject.IsInstanceValid(play) && !play.IsQueuedForDeletion())
            play.CancelPlayCard();
    }

    internal static bool CanBegin(CardModel card, out UnplayableReason reason, out AbstractModel? preventer)
    {
        if (TeammatePlayLimit.BlockedForLocal(card))
        { reason = UnplayableReason.BlockedByCardLogic; preventer = card; TeammatePlayLimit.SpeakLocal(card); return false; }
        if (SelectionHints.LockedForLocal(card))
        { reason = UnplayableReason.BlockedByCardLogic; preventer = card; return false; }
        if (card.CanPlay(out reason, out preventer))
            return true;
        if (!AllyPlay.Enabled || !AllyPlay.Eligible(card) || card.CombatState is not { } state)
            return false;
        var payer = LocalContext.GetMe(state);
        if (payer == null)
            return false;
        foreach (var recipient in state.Players)
            if (recipient != payer && recipient.Creature.IsAlive && AllyPlay.Validate(card, payer, recipient.NetId, null, false) == null)
            {
                reason = UnplayableReason.None;
                preventer = null;
                return true;
            }

        return false;
    }
}

[HarmonyPatch(typeof(NMouseCardPlay), nameof(NMouseCardPlay._Input))]
internal static class AllyDragInputPatch
{
    private static void Prefix(NMouseCardPlay __instance, InputEvent inputEvent) => AllyTargeting.Input(__instance, inputEvent);
}

[HarmonyPatch(typeof(NTargetManager), "AllowedToTargetCreature")]
internal static class AllyFirstTargetPatch
{
    private static void Postfix(Creature creature, ref bool __result)
    {
        if (!__result && AllyTargeting.Allow(creature))
            __result = true;
    }
}

[HarmonyPatch(typeof(NCardPlay), "TryPlayCard")]
internal static class AllyDropPatch
{
    private static bool Prefix(NCardPlay __instance, Creature? target) => AllyTargeting.TryPlay(__instance, target);
}

[HarmonyPatch]
internal static class AllyBeginPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(NMouseCardPlay).GetNestedTypes(BindingFlags.NonPublic).Single(t => t.Name.StartsWith("<StartAsync>")), "MoveNext");
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original = AccessTools.Method(typeof(CardModel), nameof(CardModel.CanPlay), new[] { typeof(UnplayableReason).MakeByRefType(), typeof(AbstractModel).MakeByRefType() });
        var replacement = AccessTools.Method(typeof(AllyTargeting), nameof(AllyTargeting.CanBegin));
        var bubble = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Vfx.NThoughtBubbleVfx), "Create", new[]{typeof(string),typeof(Creature),typeof(double?)});
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = replacement;
            }
            else if(instruction.Calls(bubble))
            { instruction.opcode=OpCodes.Call; instruction.operand=AccessTools.Method(typeof(ManualFeedback),nameof(ManualFeedback.FailureBubble)); }

            yield return instruction;
        }
    }
}

[HarmonyPatch]
internal static class AllyMultiTargetPosePatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(typeof(NMouseCardPlay).GetNestedTypes(BindingFlags.NonPublic).Single(t => t.Name.StartsWith("<MultiCreatureTargeting>")), "MoveNext");
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original = AccessTools.Method(typeof(NMouseCardPlay), "LerpToMouse");
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(AllyTargeting), nameof(AllyTargeting.DragOrAim));
            }
            yield return instruction;
        }
    }
}

