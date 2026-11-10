using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Screens.InspectScreens;

namespace SharedHandPrototype;

internal static class SharedRelicNotes
{
    internal static IEnumerable<IHoverTip> Append(RelicModel relic,IEnumerable<IHoverTip> original)
    {
        var run=RunManager.Instance.DebugOnlyGetState();
        if(!HandSharingMode.Shared||run?.Players.Count is not (>=2 and <=PartyLimits.MaxPlayers))return original;
        string? text=relic switch
        {
            GhostSeed=>"抽到[gold]打击[/gold]和[gold]防御[/gold]时，为其添加[gold]虚无[/gold]词条。",
            MummifiedHand=>"以“和谐友爱”的方式打出同样生效。",
            Tingsha or ToughBandages=>"弃牌效果由你执行时，弃掉队友的牌也能触发本遗物。",
            ScreamingFlagon=>"持有者回合结束自己无手牌即可。",
            UnceasingTop=>"持有者手牌为空即可触发。",
            _=>null
        };
        if(text==null)return original;
        string id=Entry.ModId+".relic-note."+relic.Id.Entry;
        var tips=original.ToList();
        if(tips.OfType<HoverTip>().Any(t=>t.Id==id))return tips;
        const string key="SharedHandPrototype.relic-adjustments";
        LocManager.Instance.GetTable("settings_ui").MergeWith(new(){[key]="共享牌组改动"});
        tips.Add(new HoverTip(new LocString("settings_ui",key),text){Id=id});
        return tips;
    }
    internal static IEnumerable<IHoverTip> ForInspect(RelicModel relic)=>Append(relic,relic.HoverTipsExcludingRelic);
}

// The native inspect page may inline its tiny excluding-relic getter. Bind
// that actual UI call site as well, while retaining native keyword tip nodes.
[HarmonyPatch(typeof(NInspectRelicScreen),"UpdateRelicDisplay")]
internal static class SharedRelicInspectNotesPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getter=AccessTools.PropertyGetter(typeof(RelicModel),nameof(RelicModel.HoverTipsExcludingRelic));
        int replaced=0;
        foreach(var i in instructions)
        {if(i.Calls(getter)){i.opcode=OpCodes.Call;i.operand=AccessTools.Method(typeof(SharedRelicNotes),nameof(SharedRelicNotes.ForInspect));replaced++;}yield return i;}
        if(replaced!=1)throw new System.InvalidOperationException("无法定位遗物详情页的额外词条读取点。");
    }
}

// Both native relic hover and inspect/event side panels request these lists.
// Stable IDs make the note safe if another UI adapter reads both lists.
[HarmonyPatch]
internal static class SharedRelicNotesPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.PropertyGetter(typeof(RelicModel),nameof(RelicModel.HoverTips));
        yield return AccessTools.PropertyGetter(typeof(RelicModel),nameof(RelicModel.HoverTipsExcludingRelic));
    }
    private static void Postfix(RelicModel __instance,ref IEnumerable<IHoverTip> __result)=>__result=SharedRelicNotes.Append(__instance,__result);
}

