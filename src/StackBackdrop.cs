using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using System.Threading.Tasks;

namespace SharedHandPrototype;

// One layer behind ALL native cards: decorative views cannot catch mouse
// input, become a playable holder, or put their callout over a nearby card.
internal sealed partial class StackBackdrop : Control
{
    private sealed record Item(NCardHolder Head,HandStacks.Stack Stack,Label Counter,Transform2D? Rest);
    private readonly List<Item> _items=new();
    private sealed class View
    {
        internal NCard node;
        internal string key;
        internal ulong? Growth;
        internal View(NCard n,string k){node=n;key=k;}
    }
    private readonly Dictionary<CardModel,View> _views=new();
    private HashSet<CardModel> _live=new();
    private ulong _settle;
    private readonly HashSet<CardModel> _shown=new();
    private readonly Control _callouts;
    private SceneTree? _tree;
    internal StackBackdrop()
    {
        _callouts=new Control{Name="SharedStackCallouts",MouseFilter=MouseFilterEnum.Ignore};
        _callouts.Draw+=Paint;AddChild(_callouts);
        TreeEntered+=()=>{_tree=GetTree();_tree.ProcessFrame+=Animate;};
        TreeExiting+=()=>{if(_tree!=null)_tree.ProcessFrame-=Animate;_tree=null;};
    }
    internal void Begin(HashSet<CardModel> live)
    {
        _items.Clear();_shown.Clear();
        if(live.Except(_live).Any())_settle=Time.GetTicksMsec()+180;
        _live=live;
        foreach(var pair in _views.ToArray())
        {
            if(!GodotObject.IsInstanceValid(pair.Value.node)){_views.Remove(pair.Key);continue;}
            pair.Value.node.Hide();
            if(!live.Contains(pair.Key)){pair.Value.node.QueueFree();_views.Remove(pair.Key);}
        }
    }
    internal void Add(NCardHolder head,HandStacks.Stack stack,Label counter,Transform2D? rest)
    {
        _items.RemoveAll(i=>i.Head==head);
        _items.Add(new(head,stack,counter,rest));
        // Every incoming duplicate needs a trail, including the sixth or
        // later card even though only four backing faces are drawn.
        foreach(var card in stack.Cards.Skip(1))
            if(StackDrawAnimation.ConsumeArrival(card))FlyIntoStack(head,card);
        // The nearest layer is the actual NEXT physical card, not a sample of
        // owners or a generic outline. At most four layers keep it compact.
        for(int index=Math.Min(4,stack.Cards.Length-1);index>=1;index--)
        {
            var card=stack.Cards[index];
            if(!_views.TryGetValue(card,out var view)||!GodotObject.IsInstanceValid(view.node))
            {
                var node=NCard.Create(card);if(node==null)continue;
                node.Name="SharedStackBackCard";AddChild(node);IgnoreMouse(node);
                node.UpdateVisuals(PileType.Hand,CardPreviewMode.Normal);
                view=new(node,stack.Key);_views[card]=view;
            }
            else if(view.key!=stack.Key)
            {view.node.UpdateVisuals(PileType.Hand,CardPreviewMode.Normal);view.key=stack.Key;}
            // Back layers are plain card faces, never playable/highlighted
            // cards. Killing the native tween also prevents a late glow flash.
            view.node.CardHighlight.AnimHideInstantly();
            MoveChild(view.node,GetChildCount()-1);view.node.Show();_shown.Add(card);
        }
        // The callout belongs between the decorative back cards and every
        // real front card, rather than underneath the whole stepped stack.
        MoveChild(_callouts,GetChildCount()-1);
    }
    internal void Finish(){Visible=_items.Count>0;Animate();}
    private void FlyIntoStack(NCardHolder head,CardModel card)
    {
        var anchor=new Control{Name="SharedStackDrawFlight_"+MegaCrit.Sts2.Core.Entities.Multiplayer.NetCombatCard.FromModel(card).CombatCardIndex,MouseFilter=MouseFilterEnum.Ignore};AddChild(anchor);
        var start=PileType.Draw.GetTargetPosition(head.CardNode!);
        anchor.GlobalPosition=start;anchor.Scale=Vector2.One*.6f;
        var trail=NCardTrailVfx.Create(anchor,card.Owner.Character.TrailPath);
        if(trail==null){anchor.QueueFree();return;}
        AddChild(trail);
        var tween=CreateTween();
        tween.TweenMethod(Callable.From<float>(t=>
        {
            if(!GodotObject.IsInstanceValid(head.CardNode))return;
            var end=head.CardNode!.GlobalPosition;
            anchor.GlobalPosition=start.Lerp(end,t)+new Vector2(0,-100*MathF.Sin(Mathf.Pi*t));
        }),0f,1f,.42).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.TweenCallback(Callable.From(()=>{_ = FinishFlight(trail,anchor);}));
    }
    private static async Task FinishFlight(NCardTrailVfx trail,Control anchor)
    {
        try{if(GodotObject.IsInstanceValid(trail)&&trail.IsInsideTree())await trail.FadeOut();}
        finally{if(GodotObject.IsInstanceValid(anchor))anchor.QueueFree();}
    }
    private static void IgnoreMouse(Node node)
    {
        if(node is Control control){control.MouseFilter=MouseFilterEnum.Ignore;control.FocusMode=FocusModeEnum.None;}
        foreach(var child in node.GetChildren())IgnoreMouse(child);
    }
    private void Animate()
    {
        if(!IsVisibleInTree())return;
        var inverse=GetGlobalTransformWithCanvas().AffineInverse();
        foreach(var item in _items)
        {
            if(!GodotObject.IsInstanceValid(item.Head)||!GodotObject.IsInstanceValid(item.Head.CardNode))continue;
            var origin=item.Rest??inverse*item.Head.CardNode!.GetGlobalTransformWithCanvas();
            for(int index=1;index<=Math.Min(4,item.Stack.Cards.Length-1);index++)
                if(_views.TryGetValue(item.Stack.Cards[index],out var view)&&GodotObject.IsInstanceValid(view.node))
                {
                    ulong now=Time.GetTicksMsec();
                    if(view.Growth==null&&!StackDrawAnimation.Pending&&now>=_settle)view.Growth=now;
                    float progress=view.Growth==null?0:Math.Clamp((now-view.Growth.Value)/260f,0,1);
                    float eased=1-MathF.Pow(1-progress,3);
                    view.node.Position=origin*(new Vector2(index*2,-index*14)*eased);
                    view.node.Rotation=origin.Rotation;view.node.Scale=origin.Scale;
                }
        }
        _callouts.QueueRedraw();
    }
    private void Paint()
    {
        var inverse=_callouts.GetGlobalTransformWithCanvas().AffineInverse();
        foreach(var item in _items)
        {
            var label=item.Counter;
            if(!GodotObject.IsInstanceValid(label)||!label.IsVisibleInTree()||!GodotObject.IsInstanceValid(item.Head.CardNode))continue;
            var transform=inverse*label.GetGlobalTransformWithCanvas();
            var center=label.Size*.5f;
            var radius=new Vector2(Math.Max(28,label.Size.X*.5f+8),Math.Max(28,label.Size.Y*.5f+3));
            var points=new Vector2[65];
            for(int i=0;i<points.Length;i++)
            {float angle=Mathf.Tau*i/(points.Length-1);points[i]=transform*(center+new Vector2(MathF.Cos(angle)*radius.X,MathF.Sin(angle)*radius.Y));}
            float unit=Math.Max(.25f,transform.Scale.Abs().X);
            var tip=inverse*item.Head.CardNode!.GetGlobalTransformWithCanvas()*new Vector2(-140,-184);
            var start=transform*(center+new Vector2(radius.X*.72f,radius.Y*.70f));
            var tail=new[]{start,start.Lerp(tip,.55f)+new Vector2(-3,3)*unit,tip};
            _callouts.DrawPolyline(tail,Colors.Black,8*unit,true);_callouts.DrawPolyline(tail,Colors.White,3*unit,true);
            _callouts.DrawPolyline(points,Colors.Black,8*unit,true);_callouts.DrawPolyline(points,Colors.White,3*unit,true);
        }
    }
}

