using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace SharedHandPrototype;

internal static class SelectAllHand
{
    private static Button? _button;
    private static NPlayerHand? _hand;
    private static readonly System.Reflection.FieldInfo Preferences=AccessTools.Field(typeof(NPlayerHand),"_prefs");
    private static readonly System.Reflection.FieldInfo Selected=AccessTools.Field(typeof(NPlayerHand),"_selectedCards");
    // Vanilla uses this sentinel for "any number", including Guards. A fixed
    // discard quota (even one greater than the current hand) is not select-all.
    internal static bool IsAnyNumber(CardSelectorPrefs prefs)=>prefs.MinSelect==0&&prefs.MaxSelect>=999999999;
    internal static bool Available(NPlayerHand hand)=>HandLayouts.Active(hand)&&HandLayouts.CombatVisible&&
        hand.IsInCardSelection&&hand.CurrentMode==NPlayerHand.Mode.SimpleSelect&&IsAnyNumber((CardSelectorPrefs)Preferences.GetValue(hand)!);
    internal static void Update(NPlayerHand hand,Vector2 size,Func<Vector2,Vector2> atRest)
    {
        if(_hand!=hand){_hand=hand;_button=null;}
        if(!GodotObject.IsInstanceValid(_button))
        {
            _button=new Button{Name="SharedHandSelectAll",Text="全选",CustomMinimumSize=new Vector2(100,54),FocusMode=Control.FocusModeEnum.All};
            _button.AddThemeFontSizeOverride("font_size",24);hand.AddChild(_button);_button.Pressed+=Choose;
        }
        _button!.Visible=Available(hand);
        float unit=Math.Min(size.X/1920f,size.Y/1080f);
        _button.Scale=Vector2.One*unit;_button.Position=atRest(new Vector2(18*unit,size.Y-269*unit));
    }
    internal static void Hide(){if(GodotObject.IsInstanceValid(_button))_button!.Hide();}
    internal static void Choose()
    {
        var hand=_hand;if(hand==null||!Available(hand))return;
        var selected=(List<CardModel>)Selected.GetValue(hand)!;
        var prefs=(CardSelectorPrefs)Preferences.GetValue(hand)!;
        var snapshot=SharedHandSemantics.All().Where(c=>!selected.Contains(c)&&SharedSelection.Allowed(c)).ToArray();
        foreach(var card in snapshot)
        {
            if(!Available(hand)||selected.Count>=prefs.MaxSelect)break;
            if(!SharedSelection.Allowed(card)||selected.Contains(card)||hand.GetCardHolder(card) is not NHandCardHolder holder)continue;
            HandStacks.Reveal(holder);
            AccessTools.Method(typeof(NPlayerHand),"SelectCardInSimpleMode").Invoke(hand,new object[]{holder});
        }
        HandLayouts.Apply(hand);
    }
}

