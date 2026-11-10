using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;

namespace SharedHandPrototype;

// Use BaseLib's public localization convention. RitsuLib's automatic mirror
// reads these same keys and writes the same ModConfig properties/save file.
// Key by table instance so loading another language installs its own strings.
[HarmonyPatch(typeof(LocManager), nameof(LocManager.GetTable))]
internal static class SettingsLocalization
{
    private static readonly ConditionalWeakTable<LocTable, object> Applied = new();
    private static void Postfix(string name, LocTable __result)
    {
        if(name!="settings_ui"||SharedHandConfig.Instance==null||Applied.TryGetValue(__result,out _))return;
        var strings=new Dictionary<string,string>();
        var prefix=SharedHandConfig.Instance.ModPrefix;
        strings[prefix[..^1]+".mod_title"]="共享牌组（SharedHandPrototype）";
        void Add(string property,string title,string? help=null)
        {
            var key=prefix+StringHelper.Slugify(property);
            strings[key+".title"]=title;
            if(help!=null){strings[key+".hover.title"]=title;strings[key+".hover.desc"]=help;}
        }
        void Choice(string property,string value,string title)=>strings[prefix+StringHelper.Slugify(property)+"."+value]=title;
        Add("Gameplay","手牌与卡组规则");
        Add("Display","手牌显示与选牌提示");
        Add("StarterDeck","开局卡组调整");
        Add("Actions","配色与恢复默认");
        Add("Experimental","趣味实验性调整");
        Add(nameof(SharedHandConfig.UnifiedForge),"全队锻造统一","所有人都只会锻造同一把君王之剑，不影响复制。");
        Add(nameof(SharedHandConfig.HandExpansion),"手牌展开","开启后可在布局编辑器中调整合并牌区的展开模式，仅影响本机显示。");
        Add(nameof(SharedHandConfig.PublicHandLimit),"公共手牌数量上限",
            "1～99，默认 15。所有玩家手牌合计的上限。新冒险和重新载入存档时采用房主当前配置；当前战斗不会即时改变。");
        Add(nameof(SharedHandConfig.BaseDraw),"每人每回合基础抽牌",
            "1～99，默认 5。每名玩家分别抽牌，保留遗物、能力等额外抽牌，仍受公共手牌上限限制。新冒险和重新载入存档时采用房主当前配置。");
        Add(nameof(SharedHandConfig.Harmony),"和谐友爱",SharedHandConfig.HarmonyHelp);
        Add(nameof(SharedHandConfig.SharedDeck),"同步卡组",
            "所有玩家卡组合并。任意玩家对卡组的改变都生效。已开始的冒险也可开启；战斗中修改会在离开战斗后生效。关闭后保留已有卡牌。控制台删除会尝试在下次保存前校验同步。");
        Add(nameof(SharedHandConfig.IndependentPiles),"独立牌堆",SharedHandConfig.IndependentPileHelp);
        Add(nameof(SharedHandConfig.SharedPiles),"共享战斗牌组",SharedHandConfig.SharedPileHelp);
        Add(nameof(SharedHandConfig.LimitTeammateCards),"限制每回合可打出队友牌的数量",SharedHandConfig.LimitHelp);
        Add(nameof(SharedHandConfig.TeammateCardsPerTurn),"每回合队友牌数量",SharedHandConfig.LimitHelp);
        Choice(nameof(SharedHandConfig.PileMode),nameof(DrawPileMode.Independent),"独立牌堆");
        Choice(nameof(SharedHandConfig.PileMode),nameof(DrawPileMode.Common),"共享牌堆");
        Add(nameof(SharedHandConfig.SelectionChains),"队友选牌锁链提示",
            "队友正在拖动、瞄准或选择手牌时，显示锁链提示。仅有视觉效果，不会锁定卡牌，你仍可以打出它。默认开启。");
        Add(nameof(SharedHandConfig.LockSelections),"真的锁住了",SharedHandConfig.LockHelp);
        Add(nameof(SharedHandConfig.StackBasics),"打防堆叠","就是打击和防御堆一块而已。");
        Add(nameof(SharedHandConfig.StackSameName),"同名堆叠","将所有手牌中的同名卡堆叠，若附魔、强化不一致则无法堆叠。");
        Add(nameof(SharedHandConfig.TeammateGlow),"队友卡牌发光",
            "按照持牌角色配置的颜色显示发光边框。只改变边框，不改变卡面。关闭后使用原版效果。默认开启。");
        Add(nameof(SharedHandConfig.OwnCardGlow),"自己卡牌也发光");
        Add(nameof(SharedHandConfig.TopHandInverted),"上方卡牌旋转 180°",
            "上方手牌倒置摆放，牌名朝向战场；悬停时转正并放大。默认开启。");
        Add(nameof(SharedHandConfig.TopHandOccluded),"上方手牌受顶部 UI 遮挡",
            "上方手牌被顶部状态栏遮挡。关闭仅取消顶部遮挡；打开地图、卡组或菜单时，手牌始终隐藏。默认开启。");
        Add(nameof(SharedHandConfig.HandLayout),"手牌布局",
            "上下对称扇形：P1/P2 在下方，P3/P4 在上方。\n下方四区：下方 2×2 固定牌区。\n底部扇形与左右竖排：P1/P2 在下方，P3 左侧，P4 右侧。\n自定义布局：使用共享牌组完整配置页中保存的位置、缩放和间距；布局编辑器、角色头像和逐角色调色也在该页。");
        Choice(nameof(SharedHandConfig.HandLayout),nameof(SharedHandLayout.TopAndBottom),"上下对称扇形");
        Choice(nameof(SharedHandConfig.HandLayout),nameof(SharedHandLayout.FourQuadrants),"下方四区");
        Choice(nameof(SharedHandConfig.HandLayout),nameof(SharedHandLayout.BalancedRows),"底部扇形与左右竖排");
        Choice(nameof(SharedHandConfig.HandLayout),nameof(SharedHandLayout.Custom),"自定义布局");
        Add(nameof(SharedHandConfig.SmallDeck),"小卡组模式",
            "打开后安东尼会弄你。减少打防数量。\n游戏开始时将所有玩家打防数量减少至原数量除以2后除以人数。采用四舍五入。");
        Add(nameof(SharedHandConfig.SmallerDeck),"万物一心",
            "必须开启“同步卡组”。\n与小卡组模式冲突。\n开始游戏时，会以队伍中打防数量最高的一名角色为准，构建打防数量。");
        Add("DefaultCharacterColors","默认角色颜色","使用各角色的地图绘画画笔颜色，包含模组角色。");
        Add("ExportDiagnostics","导出联机诊断包","导出本局共享规则、战斗状态和近期操作；点击后先确认，不读取账号凭据或无关文件。");
        Add(nameof(SharedHandConfig.PrivateHands),"手牌不共享",SharedHandConfig.PrivateHandsHelp);
        Add("ResetAll","恢复默认","恢复本模组的全部配置，包括规则、颜色和布局。点击后立即恢复。");
        __result.MergeWith(strings);
        Applied.Add(__result,new object());
    }
}


