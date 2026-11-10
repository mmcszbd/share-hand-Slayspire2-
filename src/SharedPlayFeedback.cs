using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace SharedHandPrototype;

// This getter is replaced only inside the mouse/controller's rejection UI;
// it cannot change ownership, payment, effects or automatic-play speakers.
[HarmonyPatch]
internal static class SharedPlayFeedback
{
    internal static Player Speaker(CardModel card)=>NativeSharedHand.Borrower(card)??card.Owner;
    private static IEnumerable<MethodBase> TargetMethods()
    {
        foreach(var type in new[]{typeof(NMouseCardPlay),typeof(NControllerCardPlay)})
        foreach(var method in type.GetMethods(BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.DeclaredOnly))
        {
            var body=method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType.GetMethod("MoveNext",BindingFlags.Instance|BindingFlags.NonPublic)??method;
            if(CompatibilityScan.CallsAny(body,new[]{AccessTools.Method(typeof(MegaCrit.Sts2.Core.Nodes.Vfx.NThoughtBubbleVfx),"Create",new[]{typeof(string),typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature),typeof(double?)})}))yield return body;
        }
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getter=AccessTools.PropertyGetter(typeof(CardModel),nameof(CardModel.Owner));
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(getter)){instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(SharedPlayFeedback),nameof(Speaker));}
            yield return instruction;
        }
    }
}

