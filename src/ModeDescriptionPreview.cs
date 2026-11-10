using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.HoverTips;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;

namespace SharedHandPrototype;

// These controls have their own transient state and never write gameplay settings.
internal static class ModeDescriptionPreview
{
    private static readonly Dictionary<string,string> PreviewNames=new()
    {
        ["神化"]="Apotheosis",["散射炮"]="FlakCannon",["原始力量"]="PrimalForce",
        ["护驾！！！"]="Guards",["武装＋"]="Armaments+",["压缩"]="Compact",
        ["恶魔之焰"]="FiendFire",["重振精神"]="SecondWind",["添柴"]="Stoke",
        ["钢铁风暴"]="StormOfSteel",["计算下注"]="CalculatedGamble",["子弹时间"]="BulletTime",
        ["痛殴"]="Thrash",["坚毅"]="TrueGrit",["净化"]="Purity",["生存者"]="Survivor",
        ["完美打击"]="PerfectedStrike",["许愿"]="Wish",["秘密技法"]="SecretTechnique",
        ["秘密武器"]="SecretWeapon",["探寻打击"]="SeekerStrike",["冲锋！！"]="Charge",
        ["洁净"]="Cleanse",["降灵"]="Seance",["既定事项"]="ForegoneConclusion",
        ["全息影像"]="Hologram",["坟冢爆射"]="Graveblast",["头槌"]="Headbutt",
        ["宇宙冷漠"]="CosmicIndifference",["清淤"]="Dredge",["涅奥之怒"]="NeowsFury",
        ["刀舞"]="BladeDance",["风箱"]="Bellows",["干瘪之手"]="MummifiedHand",
        ["斗篷扣"]="CloakClasp",["烘焙手套"]="ToastyMittens",["宝石面具"]="JeweledMask",
        ["能量电池"]="PowerCell",["烟雾弥漫"]="SmoggyPower"
    };
    private static readonly Regex PreviewPattern=new(string.Join("|",PreviewNames.Keys.OrderByDescending(s=>s.Length).Select(Regex.Escape)),RegexOptions.Compiled);
    private static string Highlight(string text)=>PreviewPattern.Replace(text,m=>
        "[url="+PreviewNames[m.Value]+"][color=#f2cb67]"+m.Value+"[/color][/url]");
    private sealed class PreviewTick : NSettingsTickbox
    {
        private readonly Action<bool> _change;
        internal PreviewTick(Action<bool> change)
        {
            _change=change; CustomMinimumSize=new Vector2(64,64);
            var template=ResourceLoader.Load<PackedScene>(SceneHelper.GetScenePath("screens/settings_tickbox")).Instantiate<Control>();
            var owned=new List<Node>();
            void Visit(Node node){if(node.Owner==template)owned.Add(node);foreach(var child in node.GetChildren())Visit(child);}
            Visit(template);
            foreach(var child in template.GetChildren()){template.RemoveChild(child);AddChild(child);}
            foreach(var node in owned)node.Owner=this;
            template.Free();
        }
        public override void _Ready(){ConnectSignals();IsTicked=false;}
        protected override void OnTick()=>_change(true);
        protected override void OnUntick()=>_change(false);
    }
    private const string IndependentExamples="\n\n只处理自己的手牌：护驾！！！、武装＋、压缩、恶魔之焰、重振精神、添柴、钢铁风暴、计算下注、子弹时间、痛殴、坚毅、净化、生存者。"+
        "\n遗物的手牌范围：风箱、干瘪之手、斗篷扣、烘焙手套。烟雾弥漫只封锁自己的手牌。";
    private const string CommonExamples="\n\n共享牌组中的牌会参与神化、散射炮和完美打击等效果；散射炮不会重复消耗已在消耗堆中的牌。"+
        "\n检索共同抽牌堆：许愿、秘密技法、秘密武器、探寻打击、冲锋！！、洁净、降灵、既定事项。"+
        "\n检索共同弃牌堆：全息影像、坟冢爆射、头槌、宇宙冷漠、清淤、涅奥之怒。"+
        "\n遗物中的共同抽牌堆效果：宝石面具、能量电池。";
    internal static void AddTo(Control parent)
    {
        var title=NativeConfigStore.CreateRawLabelControl("玩法组合说明（仅供查看，不会修改上方设置）",24);
        title.CustomMinimumSize=new Vector2(0,64);parent.AddChild(title);
        var choices=new VBoxContainer{Name="ModePreviewChoices",SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
        parent.AddChild(choices);
        var first=new HBoxContainer{Name="FirstRow",Alignment=BoxContainer.AlignmentMode.Center};
        var second=new HBoxContainer{Name="SecondRow",Alignment=BoxContainer.AlignmentMode.Center};
        choices.AddChild(first);choices.AddChild(second);
        string[] names={"同步卡组","独立牌堆","共享战斗牌组","仅手牌共享","手牌不共享"};
        var selected=new bool[5];var ticks=new PreviewTick[5];var labels=new Control[5];var crosses=new Label[5];
        var description=NativeConfigStore.CreateRawLabelControl("",22);
        description.Name="SharedHandModeNotes";description.VerticalAlignment=VerticalAlignment.Top;
        description.AutowrapMode=TextServer.AutowrapMode.WordSmart;description.FitContent=true;
        description.MouseFilter=Control.MouseFilterEnum.Pass;
        description.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
        parent.AddChild(description);
        var anchor=new Control{Name="ModeExampleHoverAnchor",TopLevel=true,MouseFilter=Control.MouseFilterEnum.Ignore,Size=new Vector2(1,1)};
        description.AddChild(anchor);
        description.MetaHoverStarted+=meta=>
        {
            var key=meta.AsString();bool upgraded=key.EndsWith("+",StringComparison.Ordinal);
            if(upgraded)key=key.Substring(0,key.Length-1);
            var model=ModelDb.All.FirstOrDefault(m=>m.GetType().Name==key);
            anchor.GlobalPosition=description.GetGlobalMousePosition();
            if(model is CardModel card)NHoverTipSet.CreateAndShow(anchor,HoverTipFactory.FromCard(card,upgraded));
            else if(model is RelicModel relic)
            {
                var tip=new HoverTip(relic.Title,relic.DynamicDescription,relic.Icon);
                NHoverTipSet.CreateAndShow(anchor,new IHoverTip[]{tip}.Concat(relic.HoverTipsExcludingRelic));
            }
            else if(model is PowerModel power)NHoverTipSet.CreateAndShow(anchor,HoverTipFactory.FromPower(power));
        };
        description.MetaHoverEnded+=_=>NHoverTipSet.Remove(anchor);
        description.TreeExiting+=()=>NHoverTipSet.Remove(anchor);
        void Refresh()
        {
            bool any=false;foreach(var x in selected)any|=x;
            bool invalid=selected[3]&&selected[4];
            bool commonOverridden=selected[2]&&(selected[3]||selected[4]);
            bool independentOverriddenByOnly=selected[1]&&selected[3];
            bool independentOverridden=selected[1]&&selected[2]&&!commonOverridden&&!selected[0];
            for(int i=0;i<5;i++)
            {
                bool crossed=selected[i]&&(invalid&&i>=3||i==2&&commonOverridden||i==1&&(independentOverridden||independentOverriddenByOnly));
                labels[i].Modulate=crossed?new Color(1,.35f,.35f):Colors.White;
                crosses[i].Visible=crossed;
                ticks[i].Modulate=crossed?new Color(1,.35f,.35f,.35f):Colors.White;
            }
            if(!any){description.Text="";description.Visible=false;return;}
            description.Visible=true;
            if(invalid)
            {
                description.Text="该组合[color=#ff6666]不存在[/color]。理由："+(selected[2]
                    ? "共享战斗牌组、仅手牌共享与手牌不共享互斥。"
                    : "仅手牌共享与手牌不共享互斥。");
                if(selected[1])description.Text+="独立牌堆与仅手牌共享互斥。";
                return;
            }
            bool sync=selected[0],independent=selected[1]&&!independentOverriddenByOnly,common=selected[2]&&!commonOverridden,only=selected[3],priv=selected[4];
            string text;
            if(priv)
                text="每人只能看到和使用自己的手牌；抽牌堆、弃牌堆和消耗堆共享。和谐友爱仍可把自己的技能牌或能力牌交给队友使用；棋盘决斗也可以启用。";
            else if(common)
                text="战斗时合并全队的手牌、抽牌堆和弃牌堆，消耗堆仍各自独立。可以使用队友的手牌；神化、活雾、散射炮、原始力量等效果按共享战斗牌组的规则处理牌。";
            else if(only)
                text="手牌共享，抽牌堆、弃牌堆和消耗堆各自独立。可以使用队友的手牌；神化、散射炮、武装＋、压缩等影响整个牌库及手牌的效果会处理使用者自己的牌和当前的共享手牌。\n这是与“独立牌堆”最大的不同点。\n打出队友的刀舞时，小刀归刀舞原来的持有者。";
            else if(independent)
                text="每人从自己的牌堆抽牌，但仍可使用队友的手牌。神化、活雾、散射炮、原始力量等影响牌的卡牌和遗物只处理使用者自己的手牌和牌堆，不能强化、变化、消耗或弃置队友牌。";
            else
                text="获得、删除、升级或变化卡牌时，其他人的永久卡组也跟着改变。每个人战斗时各有一套同步后的牌。";
            if(sync&&(independent||common||only||priv))
                text+=common
                    ? independent ? "\n\n[color=#80e878]多人联机时，获得一张牌，其他人也会得到一张；战斗时这些牌合并到一起。[/color]"
                                  : "\n\n[color=#80e878]完全卡组同步，战斗时全队共用一套同步后的抽弃牌堆。获得一张牌，战斗牌堆中相应增加一张；删牌、升级和变化同样生效。[/color]"
                    : independent && !priv ? "\n\n[color=#80e878]每人都有一套同步的卡组，战斗时各自抽牌。多人联机时，获得一张牌，其他人也会得到一张。[/color]"
                    : "\n\n[color=#80e878]你和队友拥有相同的永久卡组；获得、删除、升级或变化卡牌会同步。[/color]";
            if(independentOverridden)text+="\n\n[color=#ff6666]独立牌堆被共享战斗牌组覆盖。[/color]";
            if(independentOverriddenByOnly)text+="\n\n[color=#ff6666]独立牌堆被仅手牌共享覆盖。[/color]";
            if(commonOverridden)text+="\n\n[color=#ff6666]共享战斗牌组被"+(priv?"手牌不共享":"仅手牌共享")+"覆盖。[/color]";
            if(independent&&priv)text+="\n\n[color=#80e878]独立牌堆的个人手牌效果范围仍适用；抽弃消耗堆按手牌不共享规则合并。[/color]";
            if(independent&&!selected[0]&&!selected[2]&&!only&&!priv)text+=IndependentExamples;
            if(common&&!selected[0]&&!independent&&!only&&!priv)text+=CommonExamples;
            description.Text=Highlight(text);
        }
        for(int i=0;i<5;i++)
        {
            int index=i;
            var label=NativeConfigStore.CreateRawLabelControl(names[i],22);labels[i]=label;
            label.CustomMinimumSize=new Vector2(0,64);label.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
            var frame=new Control{CustomMinimumSize=new Vector2(70,64)};
            var tick=new PreviewTick(value=>{selected[index]=value;Refresh();});ticks[i]=tick;
            tick.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);frame.AddChild(tick);
            var cross=new Label{Text="×",Visible=false,MouseFilter=Control.MouseFilterEnum.Ignore,
                HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
            cross.AddThemeFontSizeOverride("font_size",44);cross.AddThemeColorOverride("font_color",new Color(1,.2f,.2f));
            frame.AddChild(cross);cross.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);crosses[i]=cross;
            var row=new NConfigOptionRow("","ModePreview"+i,label,frame){
                CustomMinimumSize=new Vector2(280,64),SizeFlagsHorizontal=Control.SizeFlags.ShrinkBegin};
            (i<3?first:second).AddChild(row);
        }
        Refresh();
    }
}
