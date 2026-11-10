using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace SharedHandPrototype;

// Presentation only: callers own the authoritative timer, networking and
// timeout choice. Move values match the native relic fight (rock=0, paper=1,
// scissors=2). A displayed choice is never sent twice.
internal static class BoardDuelRpsUi
{
    private static CanvasLayer? _layer;
    private static Label? _message;
    private static Label? _timer;
    private static Button[] _buttons=Array.Empty<Button>();
    private static bool _chosen;

    internal static bool Open(int seconds, Action<int> choose)
    {
        Close();
        var game=NGame.Instance;
        if(game==null||choose==null)return false;
        try
        {
            _chosen=false;
            var layer=new CanvasLayer{Name="SharedHandBoardDuelRps",Layer=320};
            var panel=new PanelContainer{Name="BoardDuelRpsPanel",MouseFilter=Control.MouseFilterEnum.Stop,ProcessMode=Node.ProcessModeEnum.Always};
            panel.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
            panel.OffsetLeft=-340;panel.OffsetRight=340;panel.OffsetTop=-220;panel.OffsetBottom=-30;
            var style=new StyleBoxFlat{BgColor=new Color(.075f,.09f,.12f,.97f),BorderColor=new Color(.75f,.61f,.34f),BorderWidthLeft=2,BorderWidthRight=2,BorderWidthTop=2,BorderWidthBottom=2,CornerRadiusTopLeft=8,CornerRadiusTopRight=8,CornerRadiusBottomLeft=8,CornerRadiusBottomRight=8};
            panel.AddThemeStyleboxOverride("panel",style);
            var margin=new MarginContainer();
            margin.AddThemeConstantOverride("margin_left",22);margin.AddThemeConstantOverride("margin_right",22);
            margin.AddThemeConstantOverride("margin_top",12);margin.AddThemeConstantOverride("margin_bottom",12);
            var column=new VBoxContainer{Alignment=BoxContainer.AlignmentMode.Center};column.AddThemeConstantOverride("separation",7);
            _message=new Label{Text="棋盘决斗：请选择石头、剪刀或布",HorizontalAlignment=HorizontalAlignment.Center};
            _message.AddThemeFontSizeOverride("font_size",24);
            _timer=new Label{HorizontalAlignment=HorizontalAlignment.Center};_timer.AddThemeFontSizeOverride("font_size",18);
            var row=new HBoxContainer{Alignment=BoxContainer.AlignmentMode.Center};row.AddThemeConstantOverride("separation",14);
            _buttons=new Button[3];
            void Add(string text,int move,int index)
            {
                var button=new Button{Text=text,CustomMinimumSize=new Vector2(150,48),FocusMode=Control.FocusModeEnum.All};
                button.AddThemeFontSizeOverride("font_size",22);_buttons[index]=button;row.AddChild(button);
                button.Pressed+=()=>
                {
                    if(_chosen)return;_chosen=true;
                    foreach(var item in _buttons)item.Disabled=true;
                    if(GodotObject.IsInstanceValid(_message))_message!.Text="已选择"+text+"，等待其他玩家…";
                    choose(move);
                };
            }
            Add("石头",0,0);Add("剪刀",2,1);Add("布",1,2);
            column.AddChild(_message);column.AddChild(_timer);column.AddChild(row);margin.AddChild(column);panel.AddChild(margin);layer.AddChild(panel);
            game.AddChild(layer);_layer=layer;
            NHotkeyManager.Instance?.AddBlockingScreen(layer);
            game.CursorManager.SetCursorShown(true);
            UpdateRemaining(seconds);
            return true;
        }
        catch(Exception ex)
        {
            GD.PushWarning("[SharedHand] Board duel choice UI unavailable: "+ex.Message);
            Close();return false;
        }
    }

    internal static void UpdateRemaining(float seconds)
    {
        if(GodotObject.IsInstanceValid(_timer))_timer!.Text=$"剩余 {Math.Max(0,(int)Math.Ceiling(seconds))} 秒；超时随机出拳";
    }

    // Close without sending a move; the caller decides how cancellation or
    // timeout affects the round.
    internal static void Cancel()=>Close();
    internal static void Close()
    {
        if(GodotObject.IsInstanceValid(_layer))
        {
            NHotkeyManager.Instance?.RemoveBlockingScreen(_layer!);
            _layer!.QueueFree();
        }
        _layer=null;_message=null;_timer=null;_buttons=Array.Empty<Button>();_chosen=false;
    }
}
