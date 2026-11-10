using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

// Hook listeners need the cause's player, not the storage owner. This logical
// read is limited to discard/exhaust and energy-spent callbacks. Never change the real backing
// owner or move a card between piles while a hook is awaiting a choice.
internal static class SharedReactionHooks
{
    internal sealed record Reaction(CardModel Card,Player Actor);
    internal static readonly AsyncLocal<Reaction?> Current=new();
    internal static Player Owner(CardModel card)=>AllyPlay.PaymentActor(card)??(Current.Value is {} effect&&effect.Card==card?effect.Actor:card.Owner);
    private static readonly Harmony Patcher=new(Entry.ModId+".reaction-hooks");
    private static readonly HashSet<Assembly> Scanned=new();
    internal static int PatchedMethods;
    internal static void Scan(Assembly assembly,Type[] types)
    {
        if(Scanned.Contains(assembly))return;
        var getter=AccessTools.PropertyGetter(typeof(CardModel),nameof(CardModel.Owner));
        foreach(var type in types)
        {
            CompatibilityScan.Attempt("reaction-type", CompatibilityScan.Name(type), () =>
            {
                for(var parent=type;parent!=null;parent=parent.DeclaringType)
                {
                    if(!typeof(AbstractModel).IsAssignableFrom(parent))continue;
                    foreach(var method in type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly))
                    {
                        CompatibilityScan.Attempt("reaction-method", CompatibilityScan.Name(method), () =>
                        {
                            bool callback=method.Name.Contains("AfterEnergySpent",StringComparison.Ordinal)||method.Name.Contains("AfterCardDiscarded",StringComparison.Ordinal)||method.Name.Contains("AfterCardExhausted",StringComparison.Ordinal);
                            for(var owner=type;!callback&&owner!=null;owner=owner.DeclaringType)
                                callback=owner.Name.Contains("<AfterEnergySpent>",StringComparison.Ordinal)||owner.Name.Contains("<AfterCardDiscarded>",StringComparison.Ordinal)||owner.Name.Contains("<AfterCardExhausted>",StringComparison.Ordinal);
                            if(!callback||method.IsAbstract||method.ContainsGenericParameters) return;
                            if(!CompatibilityScan.CallsAny(method, new MethodBase[] { getter })) return;
                            Patcher.Patch(method,transpiler:new HarmonyMethod(typeof(SharedReactionHooks),nameof(Transpiler)));PatchedMethods++;
                        });
                    }
                    break;
                }
            });
        }
        Scanned.Add(assembly);
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getter=AccessTools.PropertyGetter(typeof(CardModel),nameof(CardModel.Owner));
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(getter)){instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(SharedReactionHooks),nameof(Owner));}
            yield return instruction;
        }
    }
}

[HarmonyPatch]
internal static class SharedReactionHookScope
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(Hook),nameof(Hook.AfterCardDiscarded));
        yield return AccessTools.Method(typeof(Hook),nameof(Hook.AfterCardExhausted));
    }
    private static void Prefix(MethodBase __originalMethod,object[] __args,out SharedReactionHooks.Reaction? __state)
    {
        __state=SharedReactionHooks.Current.Value;
        if(!SharedHandSemantics.Active&&!HandSharingMode.Private)return;
        var card=__args.OfType<CardModel>().First();
        var context=__args.OfType<PlayerChoiceContext>().First();
        bool ethereal=__originalMethod.Name==nameof(Hook.AfterCardExhausted)&&__args.OfType<bool>().FirstOrDefault();
        var actor=ethereal?SharedRules.PhysicalHolder(card):SharedSlyActor.Actor(context,card)??SharedRules.PhysicalHolder(card);
        SharedReactionHooks.Current.Value=new(card,actor);
    }
    // Async callbacks capture their execution context before the native Task
    // returns. Restore the caller's context immediately, including on faults.
    private static void Finalizer(SharedReactionHooks.Reaction? __state)=>SharedReactionHooks.Current.Value=__state;
}

