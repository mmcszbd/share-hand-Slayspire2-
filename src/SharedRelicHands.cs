using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Relics;

namespace SharedHandPrototype;

// Only widen these two relics' hand reads. Their native trigger, RNG, amount,
// recipient and timing remain unchanged. In singleplayer Cards returns the
// original physical hand. Do not extend this to Top or personal retention.
[HarmonyPatch]
internal static class SharedRelicHandsPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(MummifiedHand),nameof(MummifiedHand.AfterCardPlayed));
        var method=AccessTools.Method(typeof(CloakClasp),nameof(CloakClasp.BeforeSideTurnEnd));
        yield return AccessTools.Method(method.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType,"MoveNext");
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getter=AccessTools.PropertyGetter(typeof(CardPile),nameof(CardPile.Cards));
        int replaced=0;
        foreach(var i in instructions)
        {if(i.Calls(getter)){i.opcode=OpCodes.Call;i.operand=AccessTools.Method(typeof(SharedHandSemantics),nameof(SharedHandSemantics.Cards));replaced++;}yield return i;}
        if(replaced!=1)throw new System.InvalidOperationException("无法定位遗物的手牌范围读取点。");
    }
}

