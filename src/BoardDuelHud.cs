using System;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace SharedHandPrototype;

// Display-only. Turn order, count and countdown come from the synchronized
// combat controller; this widget never changes gameplay state.
internal static class BoardDuelHud
{
    private static CanvasLayer? _layer;
    private static Label? _label;
    private static string _last="";

    internal static void Update(string playerName,int played,int limit,float seconds,bool localTurn)
    {
        if(!GodotObject.IsInstanceValid(_layer)&&!Create())return;
        if(!GodotObject.IsInstanceValid(_label))return;
        string actor=string.IsNullOrWhiteSpace(playerName)?"玩家":playerName;
        string text=$"棋盘决斗 · {(localTurn?"你的行动":actor+"的行动")}   {Math.Max(0,played)}/{Math.Max(1,limit)} 张   {Math.Max(0,(int)Math.Ceiling(seconds))} 秒";
        if(text==_last)return;
        _last=text;_label!.Text=text;
        _label.AddThemeColorOverride("font_color",localTurn?new Color(1f,.85f,.50f):new Color(.91f,.93f,.96f));
    }

    private static bool Create()
    {
        var game=NGame.Instance;if(game==null)return false;
        try
        {
            var layer=new CanvasLayer{Name="SharedHandBoardDuelHud",Layer=80};
            var panel=new PanelContainer{Name="BoardDuelTurnIndicator",MouseFilter=Control.MouseFilterEnum.Ignore};
            panel.SetAnchorsPreset(Control.LayoutPreset.CenterTop);
            panel.OffsetLeft=-260;panel.OffsetRight=260;panel.OffsetTop=82;panel.OffsetBottom=125;
            panel.AddThemeStyleboxOverride("panel",new StyleBoxFlat{BgColor=new Color(.04f,.055f,.075f,.87f),BorderColor=new Color(.68f,.55f,.30f,.9f),BorderWidthLeft=1,BorderWidthRight=1,BorderWidthTop=1,BorderWidthBottom=1,CornerRadiusTopLeft=5,CornerRadiusTopRight=5,CornerRadiusBottomLeft=5,CornerRadiusBottomRight=5});
            _label=new Label{HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,MouseFilter=Control.MouseFilterEnum.Ignore};
            _label.AddThemeFontSizeOverride("font_size",20);
            panel.AddChild(_label);layer.AddChild(panel);game.AddChild(layer);_layer=layer;_last="";return true;
        }
        catch(Exception ex)
        {
            GD.PushWarning("[SharedHand] Board duel HUD unavailable: "+ex.Message);Hide();return false;
        }
    }

    internal static void Hide()
    {
        if(GodotObject.IsInstanceValid(_layer))_layer!.QueueFree();
        _layer=null;_label=null;_last="";
    }
}
