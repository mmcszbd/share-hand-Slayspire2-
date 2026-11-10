using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

namespace SharedHandPrototype;

// View-only state: neither the card models nor the network order are changed.
internal static class HandExpansion
{
    internal sealed class ZoneState
    {
        internal bool Open;
        internal ulong LastInside,LastFrame;
        // Position is measured in row spacings, not a fraction of the row's
        // original offset. Several rows can share a position after merging.
        internal readonly Dictionary<int,float> Positions=new();
        internal int[] Moving=Array.Empty<int>();
        internal float From,To,Progress,Pause;
        internal int Stage;
    }
    private sealed class AlphaState { internal float Original; internal bool? Visible; internal Tween? Animation; }
    private static readonly Dictionary<int,ZoneState> Zones=new();
    private static readonly Dictionary<NHandCardHolder,AlphaState> Alphas=new();
    private static CombatState? _combat;
    private static int _turn=int.MinValue;
    private static ulong _turnStarted;
    internal static bool IsOpen(int group)=>Zones.TryGetValue(group,out var state)&&state.Open;
    internal static float CollapseDuration(int order)=>.367f/(float)Math.Pow(2,Math.Max(0,order));
    internal static bool IsAnimating=>Zones.Values.Any(s=>s.Positions.Any(p=>s.Open?p.Value<p.Key-.001f:p.Value>.001f));
    internal static bool LayerVisible(int group,int layer)=>Zones.TryGetValue(group,out var state) &&
        (state.Open || state.Positions.GetValueOrDefault(layer)>.001f);
    internal static void Restore()
    {
        foreach(var pair in Alphas)
        {
            if(GodotObject.IsInstanceValid(pair.Value.Animation))pair.Value.Animation!.Kill();
            if(GodotObject.IsInstanceValid(pair.Key)){var c=pair.Key.Modulate;c.A=pair.Value.Original;pair.Key.Modulate=c;}
        }
        Alphas.Clear();Zones.Clear();_combat=null;_turn=int.MinValue;_turnStarted=0;
    }
    internal static void ApplyVisibility(NHandCardHolder holder,bool isBack,bool open)
    {
        if(!isBack)
        {
            if(Alphas.TryGetValue(holder,out var old))
            {
                if(GodotObject.IsInstanceValid(old.Animation))old.Animation!.Kill();var c=holder.Modulate;c.A=old.Original;holder.Modulate=c;Alphas.Remove(holder);
            }
            return;
        }
        if(!Alphas.TryGetValue(holder,out var state))Alphas[holder]=state=new AlphaState{Original=holder.Modulate.A};
        if(state.Visible==open)return;
        state.Visible=open;if(GodotObject.IsInstanceValid(state.Animation))state.Animation!.Kill();
        if(holder.IsInsideTree())
        {
            state.Animation=holder.CreateTween();
            state.Animation.TweenProperty(holder,"modulate:a",open?state.Original:0f,open?.12:.32).SetTrans(Tween.TransitionType.Sine);
        }
        else {var c=holder.Modulate;c.A=open?state.Original:0;holder.Modulate=c;}
    }
    internal static void Apply(NPlayerHand hand,CombatState combat,Dictionary<CardModel,HandLayouts.Slot> slots,HandDesign design,Vector2 size)
    {
        if(_combat!=combat){Restore();_combat=combat;}
        if(!SharedHandConfig.HandExpansion){Zones.Clear();return;}
        var groups=slots.Values.Where(s=>s.Layer>0).Select(s=>s.Group).Distinct().ToArray();
        foreach(var stale in Zones.Keys.Where(g=>!groups.Contains(g)).ToArray())Zones.Remove(stale);
        if(groups.Length==0)return;
        var viewport=hand.GetViewport();var mouse=viewport.GetMousePosition();
        float unit=Math.Min(size.X/1920f,size.Y/1080f);ulong now=Time.GetTicksMsec();
        var player=LocalContext.GetMe(combat);int turn=player?.PlayerCombatState?.TurnNumber??0;
        if(turn!=_turn){_turn=turn;_turnStarted=now;}
        bool opening=now-_turnStarted<5000;
        foreach(int group in groups)
        {
            if(!Zones.TryGetValue(group,out var state))Zones[group]=state=new();
            var zone=design.Zones[group];
            Vector2 direction=zone.Shape=="Column"?new Vector2(zone.X<.5f?1:-1,0):new Vector2(0,zone.Mirror?1:-1);
            Vector2 offset=direction*zone.ExpandDistance*unit;
            var entries=slots.Where(p=>p.Value.Group==group).ToArray();
            // With no front cards there is nothing to hover: keep the other
            // player's row reachable without changing its actual layer.
            bool noFront=!entries.Any(p=>p.Value.Layer==0);
            bool inside=entries.Where(p=>state.Open||p.Value.Layer==0||state.Positions.GetValueOrDefault(p.Value.Layer)>.001f).Any(p=>
            {
                var s=p.Value;float position=state.Positions.GetValueOrDefault(s.Layer);
                var center=s.Center+(s.Layer>0?offset*position:Vector2.Zero);
                float angle=Mathf.DegToRad(s.Angle);
                var radius=new Vector2((150*Math.Abs(Mathf.Cos(angle))+211*Math.Abs(Mathf.Sin(angle)))*s.Scale,
                    (211*Math.Abs(Mathf.Cos(angle))+150*Math.Abs(Mathf.Sin(angle)))*s.Scale);
                return new Rect2(center-radius,radius*2).Grow(16*unit).HasPoint(mouse);
            });
            var focus=hand.FocusedHolder;var hovered=viewport.GuiGetHoveredControl();
            if(GodotObject.IsInstanceValid(focus)&&focus!.CardNode?.Model is {} card&&slots.TryGetValue(card,out var focusedSlot)&&focusedSlot.Group==group&&
               hovered!=null&&(hovered==focus.Hitbox||focus.IsAncestorOf(hovered)))inside=true;
            if(inside||noFront||opening){state.Open=true;state.LastInside=now;}
            else if(hand.InCardPlay&&state.Open)state.LastInside=now;
            else if(state.Open&&now-state.LastInside>=2000)state.Open=false;
            float elapsed=state.LastFrame==0?0:Math.Min(.1f,(now-state.LastFrame)/1000f);state.LastFrame=now;
            var layers=entries.Select(p=>p.Value.Layer).Where(l=>l>0).Distinct().OrderByDescending(l=>l).ToArray();
            foreach(int stale in state.Positions.Keys.Where(l=>!layers.Contains(l)).ToArray())state.Positions.Remove(stale);
            foreach(int layer in layers)if(!state.Positions.ContainsKey(layer))state.Positions[layer]=0;
            if(state.Open)
            {
                // Opening at any point cancels the pending merge and expands
                // smoothly from the actual current positions.
                state.Moving=Array.Empty<int>();state.Pause=0;state.Progress=0;state.Stage=0;
                foreach(int layer in layers)state.Positions[layer]=Mathf.MoveToward(state.Positions[layer],layer,elapsed*layer/.12f);
            }
            else
            {
                AdvanceCollapse(state,elapsed);
            }
            // During a native drag the whole opened area settles towards its
            // screen edge, while remaining open until the drag finishes.
            Vector2 settle=hand.InCardPlay&&state.Open?(zone.Shape=="Column"?Vector2.Zero:new Vector2(0,(zone.Mirror?-1:1)*28*unit)):Vector2.Zero;
            foreach(var pair in entries)
            {
                var s=pair.Value;float position=state.Positions.GetValueOrDefault(s.Layer);
                slots[pair.Key]=s with{Center=s.Center+settle+(s.Layer>0?offset*position:Vector2.Zero)};
            }
        }
    }

    internal static void AdvanceCollapse(ZoneState state,float elapsed)
    {
        if(state.Pause>0){state.Pause=Math.Max(0,state.Pause-elapsed);return;}
        if(state.Moving.Length>0)
            state.Moving=state.Moving.Where(state.Positions.ContainsKey).ToArray();
        if(state.Moving.Length==0)
        {
            if(state.Positions.Count==0)return;
            float top=state.Positions.Values.Max();
            if(top<=.001f)return;
            state.Moving=state.Positions.Where(p=>Math.Abs(p.Value-top)<.001f).Select(p=>p.Key).ToArray();
            state.From=top;
            state.To=state.Positions.Values.Where(v=>v<top-.001f).DefaultIfEmpty(0).Max();
            state.Progress=0;
        }
        state.Progress=Math.Min(1,state.Progress+elapsed/CollapseDuration(state.Stage));
        float eased=state.Progress*state.Progress*(3-2*state.Progress);
        float position=Mathf.Lerp(state.From,state.To,eased);
        foreach(int layer in state.Moving)state.Positions[layer]=position;
        if(state.Progress>=1)
        {
            state.Moving=Array.Empty<int>();state.Progress=0;state.Stage++;
            if(state.To>.001f)state.Pause=.2f;
        }
    }
}
