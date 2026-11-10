using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.TestSupport;

namespace SharedHandPrototype;

internal static class ManualFeedback
{
    private static readonly Dictionary<Player,ulong> LastLockSpeech=new();
    internal static void Locked(CardModel card)
    {
        if(TestMode.IsOn || card.CombatState is not {} state || LocalContext.GetMe(state) is not {} actor || NCombatRoom.Instance is not {} room) return;
        ulong now=Time.GetTicksMsec();
        if(LastLockSpeech.TryGetValue(actor,out var last)&&now-last<1200) return;
        LastLockSpeech[actor]=now;
        var bubble=NThoughtBubbleVfx.Create("[jitter shared_hand_speed=0.35]没抢过！[/jitter]",actor.Creature,1.5);
        if(bubble!=null)room.CombatVfxContainer.AddChild(bubble);
    }
    // Used only at the native failed-drag dialogue call site. A lock heartbeat
    // arriving after StartCardPlay must still use the local actor and exact text.
    internal static NThoughtBubbleVfx? FailureBubble(string text,Creature speaker,double? seconds)
    {
        if(NPlayerHand.Instance is {} hand && AccessTools.Field(typeof(NPlayerHand),"_currentCardPlay").GetValue(hand) is NCardPlay play)
        {
            var card=play.Holder.CardModel;
            if(SelectionHints.LockedForLocal(card)){Locked(card);return null;}
            if(TeammatePlayLimit.BlockedForLocal(card)){TeammatePlayLimit.SpeakLocal(card);return null;}
        }
        return NThoughtBubbleVfx.Create(text,speaker,seconds);
    }
}

