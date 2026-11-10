using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace SharedHandPrototype;

internal static class AllyPortraits
{
    private sealed class Rows { internal NMultiplayerPlayerState[] Items=Array.Empty<NMultiplayerPlayerState>(); }
    private static readonly ConditionalWeakTable<NCombatRoom,Rows> Cache=new();
    private static readonly FieldInfo Highlighted=AccessTools.Field(typeof(NMultiplayerPlayerState),"_isHighlighted");
    private static readonly FieldInfo Reticle=AccessTools.Field(typeof(NMultiplayerPlayerState),"_selectionReticle");
    private static readonly MethodInfo Resize=AccessTools.Method(typeof(NMultiplayerPlayerState),"UpdateSelectionReticleWidth");
    private static readonly MethodInfo Select=AccessTools.Method(Reticle.FieldType,"OnSelect");
    private static NMultiplayerPlayerState? _drop;
    private static long _dropFrame;
    private static IEnumerable<NMultiplayerPlayerState> Find(Node root)
    {
        foreach(var child in root.GetChildren())
        {
            if(child is NMultiplayerPlayerState row)yield return row;
            foreach(var nested in Find(child))yield return nested;
        }
    }
    internal static NMultiplayerPlayerState? UnderMouse(Vector2 mouse)
    {
        if(NCombatRoom.Instance is not {} room)return null;
        var rows=Cache.GetOrCreateValue(room);
        if(rows.Items.Length==0||rows.Items.Any(r=>!GodotObject.IsInstanceValid(r)))
            rows.Items=Find(room.GetTree().Root).ToArray();
        return rows.Items.FirstOrDefault(r=>r.IsVisibleInTree()&&r.Hitbox.IsVisibleInTree()&&r.Hitbox.GetGlobalRect().HasPoint(mouse));
    }
    internal static void Highlight(NMultiplayerPlayerState row)
    {
        if((bool)Highlighted.GetValue(row)!)return;
        Highlighted.SetValue(row,true);Resize.Invoke(row,null);Select.Invoke(Reticle.GetValue(row),null);
    }
    internal static void RememberDrop(NMultiplayerPlayerState row)
    { _drop=row;_dropFrame=row.GetTree().GetFrame(); }
    internal static bool BlockDetails(NMultiplayerPlayerState row)=>AllyTargeting.ActiveGesture ||
        (ReferenceEquals(row,_drop)&&row.GetTree().GetFrame()==_dropFrame);
}

// A portrait drop must not also open the expanded player information panel.
[HarmonyPatch(typeof(NMultiplayerPlayerState),"OnRelease")]
internal static class AllyPortraitDropDetailsPatch
{
    private static bool Prefix(NMultiplayerPlayerState __instance)=>!AllyPortraits.BlockDetails(__instance);
}

