using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace SharedHandPrototype;

public enum SharedHandLayout { TopAndBottom, FourQuadrants, BalancedRows, Custom }

internal static class HandLayouts
{
    private static readonly FieldInfo DragIndex = AccessTools.Field(typeof(NPlayerHand), "_draggedHolderIndex");
    private static readonly FieldInfo TargetPositionField = AccessTools.Field(typeof(NHandCardHolder), "_targetPosition");
    private static readonly FieldInfo TargetScaleField = AccessTools.Field(typeof(NHandCardHolder), "_targetScale");
    private static readonly FieldInfo TargetAngleField = AccessTools.Field(typeof(NHandCardHolder), "_targetAngle");
    private static NPlayerHand? _hand;
    private static Control? _clip;
    private static NCard? _preview;
    private static CardModel? _previewed;
    private static ulong _lastTick;
    private static bool _screenSubscribed;
    private static readonly List<Control> Labels = new();
    internal static bool Active(NPlayerHand? hand) => HandSharingMode.Shared && Entry.Ready && hand != null && hand.IsNodeReady() &&
        SharedRules.IsSupportedParty(CombatManager.Instance.DebugOnlyGetState());
    internal static bool CombatVisible
    {
        get
        {
            if(NMapScreen.Instance?.Visible==true)return false;
            var screen=ActiveScreenContext.Instance.GetCurrentScreen();
            return screen==NCombatRoom.Instance||screen is NCardGridSelectionScreen selection&&
                selection.GetNodeOrNull<NPeekButton>("%PeekButton")?.IsPeeking==true&&CombatManager.Instance.IsInProgress;
        }
    }
    internal readonly record struct Slot(Vector2 Center, float Scale, int Group, float Angle,int Layer=0);
    internal static Dictionary<CardModel, Slot> Plan(CombatState state, Vector2 size)
    {
        var local = LocalContext.GetMe(state);
        var groups = state.Players.OrderBy(p=>p==local?0:1).Select(p=>p.PlayerCombatState!.Hand.Cards.ToArray()).ToArray();
        return HandStacks.Build(groups,size,LayoutDesign.ForPlayers(LayoutDesign.Current,state.Players.Count)).Slots;
    }
    // Layout coordinates are relative to the hand at REST. Do not cancel its
    // native AnimIn/AnimOut/AnimDisable translation on every frame.
    private static Vector2 AtRest(Control target, NPlayerHand hand, Vector2 point)
    {
        var motion=hand.GetParent<CanvasItem>().GetGlobalTransformWithCanvas().BasisXform(hand.Position);
        return target.GetGlobalTransformWithCanvas().AffineInverse() * (point+motion);
    }
    private static void Prepare(NPlayerHand hand)
    {
        if(_hand==hand)return;
        _hand=hand;_preview=null;_previewed=null;Labels.Clear();
        // Keep the native container in place in the tree: reparenting it would
        // invoke holders' ExitTree handlers and drop their model subscriptions.
        _clip=hand.CardHolderContainer;
        _clip.ClipContents=true;
        // React to menu opening before the next rendered frame as well as in Tick.
        if(!_screenSubscribed){ActiveScreenContext.Instance.Updated+=ScreenChanged;_screenSubscribed=true;}
    }
    private static void ScreenChanged()=>Visibility();
    private static void Visibility()
    {
        if(!GodotObject.IsInstanceValid(_hand)||!GodotObject.IsInstanceValid(_clip))return;
        _clip!.Visible=CombatVisible;
        if(!CombatVisible)
        {
            SelectAllHand.Hide();
            if(GodotObject.IsInstanceValid(_preview))_preview!.Visible=false;
            foreach(var label in Labels)if(GodotObject.IsInstanceValid(label))label.Visible=false;
        }
    }
    internal static bool Apply(NPlayerHand hand)
    {
        if (!Active(hand)) { HandStacks.Restore(); SelectAllHand.Hide(); return false; }
        Prepare(hand);Visibility();
        var state = CombatManager.Instance.DebugOnlyGetState()!;
        var size = hand.GetViewport().GetVisibleRect().Size;
        float unit=Math.Min(size.X/1920f,size.Y/1080f);
        float top=SharedHandConfig.TopHandOccluded?80*unit:0;
        _clip!.Position=AtRest(hand,hand,new Vector2(0,top));
        _clip.Size=new Vector2(size.X,size.Y-top);
        // No global elevation above the run's map, deck, pause or top bar.
        hand.CardHolderContainer.ZIndex=SharedHandConfig.TopHandOccluded?0:2;
        if(!CombatVisible)return true;
        var stackFrame=HandStacks.Plan(hand,state,size);
        var plan = stackFrame.Slots;
        var design=LayoutDesign.ForPlayers(LayoutDesign.Current,state.Players.Count);
        AvoidPlayerList(plan,design,size);
        HandExpansion.Apply(hand,state,plan,design,size);
        foreach(var hidden in hand.CardHolderContainer.GetChildren().OfType<NHandCardHolder>().Where(h=>!h.Visible))
            if(plan.TryGetValue(hidden.CardModel,out var hiddenSlot))
            {
                hidden.Position=AtRest(hand.CardHolderContainer,hand,hiddenSlot.Center);
                hidden.SetTargetPosition(hidden.Position);hidden.SetScaleInstantly(Vector2.One*hiddenSlot.Scale);hidden.SetAngleInstantly(hiddenSlot.Angle);
            }
        var holders = hand.ActiveHolders.ToArray();
        var shortcuts=HandStacks.Shortcuts(hand).ToList();
        bool dragging = (int)DragIndex.GetValue(hand)! >= 0;
        var expandedGroups=plan.Values.Where(p=>p.Layer>0).Select(p=>p.Group).ToHashSet();
        var focused=dragging?null:hand.FocusedHolder;
        var focusedCard=GodotObject.IsInstanceValid(focused)?focused!.CardNode?.Model:null;
        bool hasFocus=focusedCard!=null&&plan.TryGetValue(focusedCard,out _);
        for (int i = 0; i < holders.Length; i++)
        {
            var holder = holders[i];
            if (!plan.TryGetValue(holder.CardModel, out var slot)) continue;
            var zone=design.Zones[slot.Group];
            var center=slot.Center;float scale=slot.Scale,angle=slot.Angle;
            bool fan=zone.Shape=="Fan";
            bool inFocusedFan=fan&&hasFocus&&plan[focusedCard!].Group==slot.Group;
            if(inFocusedFan&&!SharedHandConfig.StackBasics&&!expandedGroups.Contains(slot.Group))
            {
                var group=holders.Where(h=>plan.TryGetValue(h.CardModel,out var p)&&p.Group==slot.Group).ToArray();
                int delta=Array.IndexOf(group,focused!)-Array.IndexOf(group,holder);
                center.X-=Math.Sign(delta)*Mathf.Lerp(100,0,Math.Min(1,Math.Abs(delta)/4f))*unit;
            }
            bool twoLayers=expandedGroups.Contains(slot.Group);
            bool enlarged=holder==focused&&hasFocus&&(inFocusedFan||twoLayers);
            if(enlarged)
            {
                bool reduced=twoLayers;
                float factor=slot.Layer>0?.5f:.25f;
                scale=reduced?Mathf.Lerp(scale,unit,factor):unit;angle=0;
                // A hovered top card is upright and fully below the HUD; a
                // bottom card rises to the same reading position as vanilla.
                float raised=zone.Mirror?Math.Max(top,zone.Y*size.Y)+211*unit-2*unit:
                    Math.Min(size.Y,zone.Y*size.Y)-211*unit+2*unit;
                center.Y=reduced?Mathf.Lerp(center.Y,raised,factor):raised;
                if(reduced){if(!((Vector2)TargetScaleField.GetValue(holder)!).IsEqualApprox(Vector2.One*scale))holder.SetTargetScale(Vector2.One*scale);if(Math.Abs((float)TargetAngleField.GetValue(holder)!)>.001f)holder.SetTargetAngle(0);}
                else {holder.SetScaleInstantly(Vector2.One*scale);holder.SetAngleInstantly(0);TargetScaleField.SetValue(holder,Vector2.One*scale);TargetAngleField.SetValue(holder,0f);}
            }
            else
            {
                if(!((Vector2)TargetScaleField.GetValue(holder)!).IsEqualApprox(Vector2.One*scale))holder.SetTargetScale(Vector2.One*scale);
                if(Math.Abs((float)TargetAngleField.GetValue(holder)!-angle)>.001f)holder.SetTargetAngle(angle);
            }
            var position=AtRest(hand.CardHolderContainer,hand,center);
            if(!holder.TargetPosition.IsEqualApprox(position))
            {
                // The native position coroutine reads this field every frame.
                // Keep it running instead of cancelling and restarting it for
                // every intermediate step in the expansion animation.
                if(HandExpansion.IsAnimating&&holder.Position.DistanceSquaredTo(holder.TargetPosition)>=1f)
                    TargetPositionField.SetValue(holder,position);
                else holder.SetTargetPosition(position);
            }
            // Hover elevation stays within the combat hand. Menu visibility is
            // unconditional, even when the player disables top HUD occlusion.
            bool back=twoLayers&&slot.Layer>0;
            HandExpansion.ApplyVisibility(holder,back,HandExpansion.LayerVisible(slot.Group,slot.Layer));
            holder.ZIndex=enlarged?100:twoLayers?50-slot.Layer:0;
            holder.Hitbox.MouseFilter=dragging||back&&!HandExpansion.LayerVisible(slot.Group,slot.Layer)||!SharedRules.PhysicalHolder(holder.CardModel).Creature.IsAlive?Control.MouseFilterEnum.Ignore:Control.MouseFilterEnum.Stop;
            holder.FocusNeighborLeft=holders[(i+holders.Length-1)%holders.Length].GetPath();
            holder.FocusNeighborRight=holders[(i+1)%holders.Length].GetPath();
            var vertical=holders.Where(h=>plan.ContainsKey(h.CardModel)&&Math.Abs(plan[h.CardModel].Center.Y-slot.Center.Y)>size.Y*.1f)
                .OrderBy(h=>Math.Abs(plan[h.CardModel].Center.X-slot.Center.X)).FirstOrDefault();
            holder.FocusNeighborTop=holder.FocusNeighborBottom=(vertical??holder).GetPath();
            holder.SetIndexLabel(shortcuts.IndexOf(holder)+1);
        }
        if(SharedHandConfig.StackBasics)
            foreach(var hidden in hand.CardHolderContainer.GetChildren().OfType<NHandCardHolder>().Where(h=>!h.Visible))hidden.SetIndexLabel(0);
        HandStacks.Decorate(hand,state,stackFrame,size,p=>AtRest(hand.CardHolderContainer,hand,p));
        SelectAllHand.Update(hand,size,p=>AtRest(hand,hand,p));
        Decorations(hand,state,plan,size);
        return true;
    }
    // Keep upper fans out of the real player-list hitboxes. The list must
    // stay interactive for native ally targeting, so changing its mouse
    // filter or simply drawing cards above it would steal input from players.
    private static void AvoidPlayerList(Dictionary<CardModel,Slot> plan,HandDesign design,Vector2 viewport)
    {
        var list=NRun.Instance?.GlobalUi?.MultiplayerPlayerContainer;
        if(!GodotObject.IsInstanceValid(list)||!list!.IsVisibleInTree())return;
        var blocks=list.GetChildren().OfType<NMultiplayerPlayerState>()
            .Where(p=>GodotObject.IsInstanceValid(p.Hitbox)&&p.Hitbox.IsVisibleInTree())
            .Select(p=>p.Hitbox.GetGlobalRect()).Where(r=>r.HasArea()).ToArray();
        if(blocks.Length==0)return;
        float unit=Math.Min(viewport.X/1920f,viewport.Y/1080f);
        for(int g=0;g<design.Zones.Length;g++)
        {
            if(!design.Zones[g].Mirror||design.Zones[g].Shape!="Fan")continue;
            var entries=plan.Where(p=>p.Value.Group==g).ToArray();
            if(entries.Length==0)continue;
            float RadiusX(Slot s)=>(150*Math.Abs(Mathf.Cos(Mathf.DegToRad(s.Angle)))+211*Math.Abs(Mathf.Sin(Mathf.DegToRad(s.Angle))))*s.Scale;
            float RadiusY(Slot s)=>(211*Math.Abs(Mathf.Cos(Mathf.DegToRad(s.Angle)))+150*Math.Abs(Mathf.Sin(Mathf.DegToRad(s.Angle))))*s.Scale;
            float left=entries.Min(p=>p.Value.Center.X-RadiusX(p.Value));
            float right=entries.Max(p=>p.Value.Center.X+RadiusX(p.Value));
            float top=entries.Min(p=>p.Value.Center.Y-RadiusY(p.Value));
            float bottom=entries.Max(p=>p.Value.Center.Y+RadiusY(p.Value));
            var overlap=blocks.Where(r=>r.End.X>left&&r.Position.X<right&&r.End.Y>top&&r.Position.Y<bottom).ToArray();
            if(overlap.Length==0)continue;
            float minLeft=overlap.Max(r=>r.End.X)+12*unit;
            float shift=minLeft-left;
            if(shift<=0)continue;
            float room=viewport.X-right-8*unit;
            if(shift<=room)
            {
                foreach(var p in entries)plan[p.Key]=p.Value with{Center=p.Value.Center+new Vector2(shift,0)};
                continue;
            }
            // Wide fans fit the remaining region by narrowing their pitch,
            // leaving card size, orientation and individual seat order intact.
            float radius=entries.Max(p=>RadiusX(p.Value));
            float first=minLeft+radius,last=viewport.X-8*unit-radius;
            if(last<first)continue;
            float oldFirst=entries.Min(p=>p.Value.Center.X),oldLast=entries.Max(p=>p.Value.Center.X);
            foreach(var p in entries)
            {
                float t=oldLast-oldFirst<.01f?0:(p.Value.Center.X-oldFirst)/(oldLast-oldFirst);
                plan[p.Key]=p.Value with{Center=new Vector2(Mathf.Lerp(first,last,t),p.Value.Center.Y)};
            }
        }
    }
    private static void Decorations(NPlayerHand hand,CombatState state,Dictionary<CardModel,Slot> plan,Vector2 size)
    {
        var local=LocalContext.GetMe(state);
        if(Labels.Count==0)
            foreach(var player in state.Players.OrderBy(p=>p==local?0:1))
            {
                var group=new HBoxContainer{Name="SharedHandOwner_"+player.NetId,MouseFilter=Control.MouseFilterEnum.Ignore};
                group.AddChild(new TextureRect{Texture=player.Character.IconTexture,CustomMinimumSize=new Vector2(28,28),
                    ExpandMode=TextureRect.ExpandModeEnum.IgnoreSize,StretchMode=TextureRect.StretchModeEnum.KeepAspectCentered,MouseFilter=Control.MouseFilterEnum.Ignore});
                var label=new Label{MouseFilter=Control.MouseFilterEnum.Ignore};label.AddThemeFontSizeOverride("font_size",18);
                group.AddChild(label);hand.AddChild(group);Labels.Add(group);
            }
        var design=LayoutDesign.ForPlayers(LayoutDesign.Current,state.Players.Count);
        var members=state.Players.OrderBy(p=>p==local?0:1).ToArray();
        for(int g=0;g<Labels.Count;g++)
        {
            Labels[g].Visible=CombatVisible&&LayoutDesign.Root(design,g)==g;
            ((Label)Labels[g].GetChild(1)).Text=string.Join(" / ",Enumerable.Range(0,members.Length).Where(i=>LayoutDesign.Root(design,i)==g)
                .Select(i=>(i==0?"你 · ":"P"+(i+1)+" · ")+members[i].Character.Title.GetFormattedText()));
            var zone=design.Zones[g];float unit=Math.Min(size.X/1920f,size.Y/1080f);
            var slots=plan.Values.Where(v=>v.Group==g).ToArray();
            float y=slots.Length>0?slots.Min(v=>v.Center.Y-211*v.Scale)-32*unit:zone.Y*size.Y-34*unit;
            if(zone.Mirror)y=slots.Length>0?slots.Max(v=>v.Center.Y+211*v.Scale)+4*unit:zone.Y*size.Y;
            float labelX=slots.Length>0?slots.Average(v=>v.Center.X):zone.X*size.X;
            Labels[g].Position=AtRest(hand,hand,new Vector2(labelX-100*unit,Math.Clamp(y,82*unit,size.Y-30*unit)));
        }
        var focused=hand.FocusedHolder;
        var card=!hand.InCardPlay&&GodotObject.IsInstanceValid(focused)&&focused!.CardNode?.Model is CardModel candidate&&
            plan.TryGetValue(candidate,out var focusSlot)&&design.Zones[focusSlot.Group].Shape!="Fan"?candidate:null;
        if(card==null){if(GodotObject.IsInstanceValid(_preview))_preview!.Visible=false;_previewed=null;return;}
        if(!GodotObject.IsInstanceValid(_preview))
        {
            _preview=NCard.Create(card);_preview!.Name="SharedHandHoverPreview";hand.AddChild(_preview);
            IgnoreMouse(_preview);_preview.ZIndex=0;
        }
        _preview!.Visible=CombatVisible;
        if(_previewed!=card){_preview.Model=card;_preview.UpdateVisuals(PileType.Hand,CardPreviewMode.Normal);IgnoreMouse(_preview);_previewed=card;}
        _preview.CardHighlight.Modulate=focused!.CardNode!.CardHighlight.Modulate;
        _preview.Scale=Vector2.One*Math.Min(.85f,size.Y/1080f*.85f);
        _preview.Position=AtRest(hand,hand,new Vector2(size.X*.5f,size.Y*.42f));
    }
    private static void IgnoreMouse(Node node)
    {
        if(node is Control control)control.MouseFilter=Control.MouseFilterEnum.Ignore;
        foreach(var child in node.GetChildren())IgnoreMouse(child);
    }
    internal static void Tick()
    {
        Visibility(); // Never throttle menu hiding.
        var now=Time.GetTicksMsec();if(now-_lastTick<(HandExpansion.IsAnimating?16UL:90UL))return;_lastTick=now;
        var hand=NPlayerHand.Instance;if(Active(hand))Apply(hand!);
        else {HandExpansion.Restore();if(GodotObject.IsInstanceValid(_preview))_preview!.Visible=false;}
    }
}

[HarmonyPatch(typeof(NPlayerHand), "RefreshLayout")]
internal static class SharedHandLayoutPatch
{
    private static bool Prefix(NPlayerHand __instance) => !HandLayouts.Apply(__instance);
}

[HarmonyPatch(typeof(NHandCardHolder), nameof(NHandCardHolder.SetDefaultTargets))]
internal static class SharedHandReturnLayoutPatch
{
    private static bool Prefix() => !HandLayouts.Apply(NPlayerHand.Instance!);
}

// Top hands drag DOWN toward combat. Fixed lower panels also need to enter
// targeting relative to the card's origin rather than the old bottom screen band.
[HarmonyPatch(typeof(NMouseCardPlay), "IsCardInPlayZone")]
internal static class SharedHandDragZonePatch
{
    private static readonly FieldInfo StartY = AccessTools.Field(typeof(NMouseCardPlay), "_dragStartYPosition");
    private static readonly FieldInfo Shortcut = AccessTools.Field(typeof(NMouseCardPlay), "_skipStartCardDrag");
    private static bool Prefix(NMouseCardPlay __instance, ref bool __result)
    {
        if (!HandLayouts.Active(NPlayerHand.Instance)) return true;
        if ((bool)Shortcut.GetValue(__instance)!) { __result = true; return false; }
        __result = HandDragGeometry.Distance(__instance) > __instance.GetViewport().GetVisibleRect().Size.Y*.035f;
        return false;
    }
}

[HarmonyPatch(typeof(NMouseCardPlay), "IsCardInCancelZone")]
internal static class SharedHandCancelZonePatch
{
    private static readonly FieldInfo StartY = AccessTools.Field(typeof(NMouseCardPlay), "_dragStartYPosition");
    private static readonly FieldInfo Shortcut = AccessTools.Field(typeof(NMouseCardPlay), "_skipStartCardDrag");
    private sealed class DragState { internal bool Entered; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NMouseCardPlay, DragState> States = new();
    private static bool Prefix(NMouseCardPlay __instance, ref bool __result)
    {
        if (!HandLayouts.Active(NPlayerHand.Instance)) return true;
        if ((bool)Shortcut.GetValue(__instance)!) { __result = false; return false; }
        float height=__instance.GetViewport().GetVisibleRect().Size.Y;
        float away=HandDragGeometry.Distance(__instance);
        var state = States.GetOrCreateValue(__instance);
        state.Entered |= away > height * .035f;
        __result = state.Entered && away < height * .014f;
        return false;
    }
}

internal static class HandDragGeometry
{
    private sealed class Start { internal Vector2 Point; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NMouseCardPlay,Start> Origins=new();
    internal static void Begin(NMouseCardPlay play)=>Origins.GetOrCreateValue(play).Point=play.GetViewport().GetMousePosition();
    internal static float Distance(NMouseCardPlay play)=>Origins.TryGetValue(play,out var origin)?play.GetViewport().GetMousePosition().DistanceTo(origin.Point):0;
}
[HarmonyPatch(typeof(NMouseCardPlay),nameof(NMouseCardPlay.Start))]
internal static class SharedDragOriginPatch
{
    private static void Prefix(NMouseCardPlay __instance)=>HandDragGeometry.Begin(__instance);
}

