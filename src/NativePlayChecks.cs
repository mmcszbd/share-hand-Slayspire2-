using System;
using System.Threading;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models.Relics;

namespace SharedHandPrototype;

// Pre-play AI runs outside the manual action executor. Its CanPlay queries
// must never be rebound to the UI-local viewer of a shared hand. AsyncLocal
// carries the scope across AI card choices without leaking into frame/UI work.
[HarmonyPatch(typeof(WhisperingEarring),nameof(WhisperingEarring.AfterAutoPrePlayPhaseEnteredLate))]
internal static class NativePlayChecks
{
    private static readonly AsyncLocal<int> Depth=new();
    internal static void Guard(MethodBase method)
    {
        new Harmony(Entry.ModId+".native-ai-checks").Patch(method,
            prefix:new HarmonyMethod(typeof(NativePlayChecks),nameof(Prefix)),
            postfix:new HarmonyMethod(typeof(NativePlayChecks),nameof(Postfix)),
            finalizer:new HarmonyMethod(typeof(NativePlayChecks),nameof(Finalizer)));
    }
    internal static bool Active=>Depth.Value>0;
    private static void Prefix(out int __state) { __state=Depth.Value; Depth.Value++; }
    private static void Postfix(int __state)=>Depth.Value=__state;
    private static void Finalizer(int __state)=>Depth.Value=__state;
}

