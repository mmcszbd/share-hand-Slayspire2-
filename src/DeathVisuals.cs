using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Audio.Debug;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.Vfx.Cards;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Settings;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.TestSupport;

namespace SharedHandPrototype;
internal static class DeathVisuals
{
    internal static NCard? Take(CardModel card)
    {
        if(TestMode.IsOn||!HandLayouts.CombatVisible||NPlayerHand.Instance is not {} hand||hand.GetCardHolder(card)?.CardNode is not NCard view)return null;
        HandStacks.Departure(card);
        var transform=view.GetGlobalTransformWithCanvas();
        view.Reparent(NCombatRoom.Instance.Ui);
        view.GlobalPosition=transform.Origin;view.Rotation=transform.Rotation;view.Scale=transform.Scale;
        view.Show();view.Modulate=Colors.White;view.Body.Modulate=Colors.White;view.Body.Scale=Vector2.One;
        view.CardHighlight.AnimHideInstantly();
        hand.Remove(card);
        return view;
    }
    internal static float Duration=>SaveManager.Instance.PrefsSave.FastMode switch
    {FastModeType.Normal=>2f,FastModeType.Fast=>1.4f,_=>1f};
    internal static void Dissolve(NCard view)
    {
        if(!GodotObject.IsInstanceValid(view)||NCombatRoom.Instance==null)return;
        var effect=NCardExhaustVfx.Create(view);
        if(effect==null){view.QueueFree();return;}
        effect.Name="SharedDeathDissolve";
        AccessTools.Field(typeof(NCardExhaustVfx),"_exhaustDuration").SetValue(effect,Duration);
        // The authored erosion curve burns most of the card early. Stretch
        // the visible erosion itself, not just the particle cleanup lifetime.
        var curve=new Curve();curve.AddPoint(new Vector2(0,0));curve.AddPoint(new Vector2(.15f,0));curve.AddPoint(new Vector2(.95f,1));curve.AddPoint(new Vector2(1,1));
        AccessTools.Field(typeof(NCardExhaustVfx),"_exhaustCurve").SetValue(effect,curve);
        NCombatRoom.Instance.Ui.AddChild(effect);
        NDebugAudioManager.Instance?.Play("card_exhaust.mp3");
        TaskHelper.RunSafely(effect.PlayAnimation());
    }
}

// Cross-fade two original character trails, keeping both native particle
// styles. The switch starts halfway through the flight, not on departure.
internal sealed partial class DeathTrailBlend : Node
{
    private readonly NCard _card;
    private readonly NCardFlyVfx _flight;
    private readonly string _from,_to;
    private NCardTrailVfx? _old,_next;
    private float _time;private SceneTree? _tree;
    internal DeathTrailBlend(NCard card,NCardFlyVfx flight,string from,string to)
    {
        _card=card;_flight=flight;_from=from;_to=to;Name="SharedDeathTrailBlend";
        Ready+=Start;TreeEntered+=()=>{_tree=GetTree();_tree.ProcessFrame+=Tick;};
        TreeExiting+=()=>{if(_tree!=null)_tree.ProcessFrame-=Tick;_tree=null;if(GodotObject.IsInstanceValid(_next))_next!.QueueFree();};
    }
    private void Start()
    {
        if(_from==_to)return;
        _old=AccessTools.Field(typeof(NCardFlyVfx),"_vfx").GetValue(_flight) as NCardTrailVfx;
        _next=NCardTrailVfx.Create(_card,_to);
        if(_next!=null){_flight.GetParent().AddChild(_next);_next.Modulate=new Color(1,1,1,0);}
    }
    private void Tick()
    {
        if(!GodotObject.IsInstanceValid(_card)||!GodotObject.IsInstanceValid(_flight))return;
        float duration=(float)AccessTools.Field(typeof(NCardFlyVfx),"_duration").GetValue(_flight)!;
        float speed=(float)AccessTools.Field(typeof(NCardFlyVfx),"_speed").GetValue(_flight)!;
        _time+=(float)GetProcessDeltaTime()*speed;
        float blend=Mathf.SmoothStep(0f,1f,Math.Clamp((_time/Math.Max(.01f,duration)-.45f)/.4f,0f,1f));
        if(GodotObject.IsInstanceValid(_old))_old!.Modulate=new Color(1,1,1,1-blend);
        if(GodotObject.IsInstanceValid(_next))_next!.Modulate=new Color(1,1,1,blend);
    }

}

