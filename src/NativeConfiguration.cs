using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.ModdingScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.addons.mega_text;

namespace SharedHandPrototype;

[AttributeUsage(AttributeTargets.Property|AttributeTargets.Method)]internal sealed class ConfigIgnoreAttribute:Attribute{}
[AttributeUsage(AttributeTargets.Property|AttributeTargets.Method)]internal sealed class ConfigHideInUIAttribute:Attribute{}
[AttributeUsage(AttributeTargets.Property|AttributeTargets.Method)]internal sealed class ConfigHoverTipAttribute:Attribute{}
[AttributeUsage(AttributeTargets.Property|AttributeTargets.Method)]internal sealed class ConfigSectionAttribute(string name):Attribute{}
[AttributeUsage(AttributeTargets.Property|AttributeTargets.Method)]internal sealed class ConfigButtonAttribute(string name):Attribute{}
[AttributeUsage(AttributeTargets.Property)]internal sealed class ConfigSliderAttribute(int min,int max,int step):Attribute{}

// Same string-valued JSON file as previous releases. Optional settings frontends
// edit this instance; they do not own gameplay state or a second config file.
public abstract class NativeConfigStore
{
    public string ModPrefix=>"SHARED_HAND_PROTOTYPE-";
    private readonly string _path;
    private bool _saving;
    private ulong _saveAfter;
    public event Action? OnConfigReloaded;
    public event EventHandler? ConfigChanged;
    protected NativeConfigStore(string name)=>_path=Path.Combine(OS.GetUserDataDir(),"mod_configs",name+".cfg");
    private IEnumerable<PropertyInfo> Properties=>GetType().GetProperties(BindingFlags.Static|BindingFlags.Public)
        .Where(p=>p.CanRead&&p.CanWrite&&p.GetCustomAttribute<ConfigIgnoreAttribute>()==null);
    public void Load()
    {
        if(!File.Exists(_path)){ConfigReloaded();return;}
        Dictionary<string,string?> values;
        try{values=JsonSerializer.Deserialize<Dictionary<string,string?>>(File.ReadAllText(_path))??new();}
        catch(Exception e)when(e is JsonException or IOException)
        {GD.PushWarning("[SharedHand] Cannot read settings; using defaults: "+e.Message);ConfigReloaded();return;}
        foreach(var p in Properties)
        {
            if(!values.TryGetValue(p.Name,out var value))continue;
            try{p.SetValue(null,TypeDescriptor.GetConverter(p.PropertyType).ConvertFromInvariantString(value??""));}
            catch(Exception e)when(e is FormatException or ArgumentException or NotSupportedException)
            {GD.PushWarning("[SharedHand] Ignored invalid setting "+p.Name);}
        }
        ConfigReloaded();
    }
    public void Save()
    {
        if(_saving)return;_saving=true;_saveAfter=0;
        try
        {
            var values=Properties.ToDictionary(p=>p.Name,p=>TypeDescriptor.GetConverter(p.PropertyType).ConvertToInvariantString(p.GetValue(null)));
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path+".new",JsonSerializer.Serialize(values,new JsonSerializerOptions{WriteIndented=true}));
            File.Move(_path+".new",_path,true);
        }
        finally{_saving=false;}
    }
    public void SaveDebounced(int delayMs=1000)=>_saveAfter=Time.GetTicksMsec()+(ulong)Math.Max(1,delayMs);
    internal void Flush(){if(_saveAfter!=0)Save();}
    internal void Tick(){if(_saveAfter!=0&&Time.GetTicksMsec()>=_saveAfter)Save();}
    public void Changed()=>ConfigChanged?.Invoke(this,EventArgs.Empty);
    public void ConfigReloaded()=>OnConfigReloaded?.Invoke();
    protected virtual void RestoreDefaultsNoConfirm(){}
    public virtual bool VisibleInModList()=>true;
    public abstract void SetupConfigUI(Control container);
    public static MegaRichTextLabel CreateRawLabelControl(string text,int fontSize)
    {
        var label=new MegaRichTextLabel{Text=text,BbcodeEnabled=true,AutoSizeEnabled=false,ScrollActive=false,
            MouseFilter=Control.MouseFilterEnum.Ignore,VerticalAlignment=VerticalAlignment.Center,
            Theme=PreloadManager.Cache.GetAsset<Theme>("res://themes/settings_screen_line_header.tres")};
        label.AddThemeFontOverride("normal_font",PreloadManager.Cache.GetAsset<Font>("res://themes/kreon_regular_shared.tres"));
        label.AddThemeFontOverride("bold_font",PreloadManager.Cache.GetAsset<Font>("res://themes/kreon_bold_shared.tres"));
        label.AddThemeFontSizeOverride("normal_font_size",fontSize);label.AddThemeFontSizeOverride("bold_font_size",fontSize);
        return label;
    }
    protected NativeConfigTickbox CreateRawTickboxControl(PropertyInfo property)=>new(this,property);
    protected NativeConfigSlider CreateRawSliderControl(PropertyInfo property)=>new(this,property);
    protected static Button CreateRawButtonControl(string text,Action pressed)
    {var button=new Button{Text=text,CustomMinimumSize=new(280,60),SizeFlagsHorizontal=Control.SizeFlags.ShrinkEnd};button.Pressed+=pressed;return button;}
}

public sealed class NativeConfigTickbox: NSettingsTickbox
{
    private readonly NativeConfigStore _config;
    private readonly PropertyInfo _property;
    public NativeConfigTickbox(NativeConfigStore config,PropertyInfo property)
    {
        _config=config;_property=property;CustomMinimumSize=new(324,64);SizeFlagsHorizontal=Control.SizeFlags.ShrinkEnd;
        var template=ResourceLoader.Load<PackedScene>(SceneHelper.GetScenePath("screens/settings_tickbox")).Instantiate<Control>();
        var owned=new List<Node>();
        void Visit(Node node){if(node.Owner==template)owned.Add(node);foreach(var child in node.GetChildren())Visit(child);}
        Visit(template);
        foreach(var child in template.GetChildren()){template.RemoveChild(child);AddChild(child);}
        foreach(var node in owned)node.Owner=this;
        template.Free();
    }
    private void Refresh(){if(IsNodeReady())IsTicked=(bool)_property.GetValue(null)!;}
    public override void _Ready(){ConnectSignals();IsTicked=(bool)_property.GetValue(null)!;_config.OnConfigReloaded+=Refresh;}
    protected override void OnTick(){_property.SetValue(null,true);_config.Changed();}
    protected override void OnUntick(){_property.SetValue(null,false);_config.Changed();}
    public override void _ExitTree(){_config.OnConfigReloaded-=Refresh;base._ExitTree();}
}
public sealed class NativeConfigSlider:HBoxContainer
{
    private readonly HSlider _slider=new(){SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,CustomMinimumSize=new(230,40)};
    private readonly Label _value=new(){CustomMinimumSize=new(54,40),HorizontalAlignment=HorizontalAlignment.Right};
    public NativeConfigSlider(NativeConfigStore config,PropertyInfo property)
    {
        CustomMinimumSize=new(324,64);SizeFlagsHorizontal=Control.SizeFlags.ShrinkEnd;AddChild(_slider);AddChild(_value);
        void Refresh(){_slider.SetValueNoSignal(Convert.ToDouble(property.GetValue(null)));_value.Text=_slider.Value.ToString("0");}
        config.OnConfigReloaded+=Refresh;TreeExiting+=()=>config.OnConfigReloaded-=Refresh;
        _slider.ValueChanged+=v=>{property.SetValue(null,(int)v);_value.Text=((int)v).ToString();config.Changed();};Refresh();
    }
    public void SetRange(int min,int max,int step){_slider.MinValue=min;_slider.MaxValue=max;_slider.Step=step;}
}
public sealed class NConfigOptionRow:HBoxContainer
{
    private HoverTip? _hoverTip;
    public Control SettingControl{get;}
    public NConfigOptionRow(string prefix,string name,Control label,Control setting)
    {
        Name=string.IsNullOrEmpty(name)?"SettingRow":name;SettingControl=setting;
        CustomMinimumSize=new(0,64);SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
        label.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;label.CustomMinimumSize=new(0,64);
        AddChild(label);AddChild(setting);
        MouseEntered+=()=>{if(_hoverTip is {} tip)NHoverTipSet.CreateAndShow(this,tip);};
        MouseExited+=()=>NHoverTipSet.Remove(this);TreeExiting+=()=>NHoverTipSet.Remove(this);
    }
}

public static class SharedHandSettingsUi
{
    private static Control? _window;
    internal static void Tick()
    {
        if(GodotObject.IsInstanceValid(_window)&&NModalContainer.Instance?.OpenModal==null&&Input.IsActionJustPressed("ui_cancel"))
        {_window!.GetViewport().SetInputAsHandled();_window.QueueFree();}
    }
    public static Control CreateOptions()
    {
        var panel=new VBoxContainer{SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
        panel.Ready+=()=>SharedHandConfig.Instance.SetupConfigUI(panel);
        return panel;
    }
    public static void Open()
    {
        if(GodotObject.IsInstanceValid(_window)){_window!.QueueFree();return;}
        var window=new SettingsWindow();_window=window;
        var game=NGame.Instance!;game.AddChild(window);
        var overlays=new Node?[]{game.HoverTipsContainer,NModalContainer.Instance}.Where(n=>n?.GetParent()==game).ToArray();
        if(overlays.Length>0)game.MoveChild(window,overlays.Min(n=>n!.GetIndex()));
    }
    private sealed class SettingsWindow:Control
    {
        public SettingsWindow(){Ready+=Build;TreeExiting+=()=>SharedHandConfig.Instance.Flush();}
        private void Build()
        {
            Size=GetViewport().GetVisibleRect().Size;MouseFilter=MouseFilterEnum.Stop;
            var screen=this;
            var background=new ColorRect{Color=new(.035f,.04f,.05f,.99f),MouseFilter=Control.MouseFilterEnum.Stop};
            screen.AddChild(background);background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var margin=new MarginContainer();
            margin.AddThemeConstantOverride("margin_left",64);margin.AddThemeConstantOverride("margin_right",64);
            margin.AddThemeConstantOverride("margin_top",30);margin.AddThemeConstantOverride("margin_bottom",30);screen.AddChild(margin);margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            var box=new VBoxContainer();margin.AddChild(box);
            var header=new HBoxContainer();box.AddChild(header);
            var title=NativeConfigStore.CreateRawLabelControl("共享牌组（SharedHandPrototype） · 设置",32);title.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;title.CustomMinimumSize=new(0,60);header.AddChild(title);
            var close=new Button{Text="返回",CustomMinimumSize=new(160,60)};close.Pressed+=()=>QueueFree();header.AddChild(close);
            var scroll=new ScrollContainer{SizeFlagsVertical=Control.SizeFlags.ExpandFill,SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled};box.AddChild(scroll);
            scroll.AddChild(CreateOptions());
        }
    }
}
[HarmonyPatch(typeof(NModInfoContainer),nameof(NModInfoContainer.Fill))]
internal static class SharedNativeSettingsEntry
{
    private static void Postfix(NModInfoContainer __instance,Mod mod)
    {
        var button=__instance.GetNodeOrNull<Button>("SharedHandSettingsButton");
        if(button==null)
        {
            button=new Button{Name="SharedHandSettingsButton",Text="共享牌组设置",CustomMinimumSize=new(300,60)};
            button.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight);button.Position=new(-320,-80);
            button.Pressed+=SharedHandSettingsUi.Open;__instance.AddChild(button);
        }
        button.Visible=mod.manifest?.id==Entry.ModId;
    }
}
[HarmonyPatch(typeof(NModInfoContainer),nameof(NModInfoContainer.Clear))]
internal static class SharedNativeSettingsClear
{
    private static void Postfix(NModInfoContainer __instance)
    {if(__instance.GetNodeOrNull<Button>("SharedHandSettingsButton") is {} button)button.Hide();}
}


