using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

internal static class SharedHandLimits
{
    internal static int Maximum() => HandSharingMode.Shared && SharedRules.IsSupportedParty(CombatManager.Instance.DebugOnlyGetState()) ? RuleSession.Current.HandLimit : CardPile.MaxCardsInHand;
    internal static int Count(IReadOnlyCollection<CardModel> cards)
    {
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (!HandSharingMode.Shared || state?.Players.Count is not (>= 2 and <= PartyLimits.MaxPlayers) || !state.Players.Any(p => ReferenceEquals(p.PlayerCombatState?.Hand.Cards, cards)))
            return cards.Count;
        return state.Players.Sum(p => p.PlayerCombatState?.Hand.Cards.Count ?? 0);
    }
}

// Patch the actual call sites as well as the property: small constant getters
// can be inlined by the JIT and evade a getter-only Harmony patch. Only native
// methods which read this hand limit are touched. Counts of other piles/lists
// retain their ordinary values. Only physical hand counts use the public cap.
[HarmonyPatch]
internal static class SharedHandLimitConsumersPatch
{
    private static readonly MethodInfo Getter = AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.MaxCardsInHand));
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (var type in typeof(CardPile).Assembly.GetTypes())
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.ContainsGenericParameters || method.IsAbstract) continue;
                if(CompatibilityScan.CallsAny(method,new MethodBase[]{Getter}))yield return method;
            }
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        bool commands = __originalMethod.DeclaringType == typeof(CardPileCmd) ||
            __originalMethod.DeclaringType?.DeclaringType == typeof(CardPileCmd);
        var code=instructions.ToArray();
        bool IsCount(CodeInstruction instruction)=>instruction.operand is MethodInfo method && method.Name=="get_Count" &&
            method.DeclaringType?.IsGenericType==true && method.DeclaringType.GetGenericArguments().Length==1 &&
            method.DeclaringType.GetGenericArguments()[0]==typeof(CardModel);
        var capacityCounts=new HashSet<CodeInstruction>();
        if(commands)
            for(int i=0;i<code.Length;i++)
            {
                if(!IsCount(code[i]))continue;
                int after=i+1;while(after<code.Length&&code[after].opcode==OpCodes.Nop)after++;
                // Count >= limit. The same method can also use Count as a real
                // physical insertion bound; that second read must stay local.
                for(int j=after;j<Math.Min(code.Length,i+10);j++)
                {
                    if(code[j].Calls(Getter)){capacityCounts.Add(code[i]);break;}
                    if(code[j].opcode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch)break;
                    if(code[j].operand is MethodBase next && !next.IsSpecialName)break;
                }
                if(capacityCounts.Contains(code[i]))continue;
                // limit - hand.Count. No intervening branch or unrelated call.
                if(after>=code.Length||code[after].opcode!=OpCodes.Sub)continue;
                for(int j=i-1;j>=Math.Max(0,i-12);j--)
                {
                    if(code[j].Calls(Getter)){capacityCounts.Add(code[i]);break;}
                    if(code[j].opcode.FlowControl is FlowControl.Branch or FlowControl.Cond_Branch)break;
                    if(code[j].operand is MethodBase called && !called.IsSpecialName && !called.Name.StartsWith("GetMaxHandSize",StringComparison.Ordinal))break;
                }
            }
        foreach (var instruction in code)
        {
            if (instruction.Calls(Getter))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(SharedHandLimits), nameof(SharedHandLimits.Maximum));
            }
            else if (capacityCounts.Contains(instruction))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(SharedHandLimits), nameof(SharedHandLimits.Count));
            }
            yield return instruction;
        }
    }
}

[HarmonyPatch(typeof(CardPile), "get_MaxCardsInHand")]
internal static class SharedHandLimitPropertyPatch
{
    private static void Postfix(ref int __result)
    {
        if (HandSharingMode.Shared && SharedRules.IsSupportedParty(CombatManager.Instance.DebugOnlyGetState()))
            __result = RuleSession.Current.HandLimit;
    }
}





