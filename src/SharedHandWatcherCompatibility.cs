using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

// These two independently authored Watcher ports implement Scrawl using
// literal 10 - hand.Count, bypassing both native and framework limit APIs.
// Restrict the adapter to their known card methods and verify the IL shape.
internal static class SharedHandWatcherCompatibility
{
    private static readonly Harmony Patcher = new(Entry.ModId + ".watcher-fill-hand");
    private static readonly HashSet<MethodInfo> Patched = new();
    private static readonly HashSet<Assembly> Scanned = new();
    private static readonly string[] ScrawlTypes =
    {
        "Watcher.Code.Cards.Rare.ScrawlWatcher", // lamali
        "WatcherMod.WatcherScrawl_P"             // Boninall
    };
    private static readonly HashSet<string> HistoryTypes = new()
    {
        "Watcher.Code.Cards.Uncommon.Sanctity",
        "Watcher.Code.Cards.Common.CrushJoints",
        "Watcher.Code.Cards.Common.FollowUp",
        "Watcher.Code.Cards.Common.SashWhip",
        "WatcherMod.WatcherSimpleAHelper"
    };
    internal static int PatchedMethods => Patched.Count;

    internal static void Scan(Assembly[] assemblies)
    {
        foreach (var assembly in assemblies.Where(a => !a.IsDynamic && a.GetName().Name == "Watcher"))
        {
            if (Scanned.Contains(assembly)) continue;
            CompatibilityScan.Attempt("watcher-assembly", assembly.GetName().Name!, () =>
            {
                foreach (string name in ScrawlTypes)
                {
                    CompatibilityScan.Attempt("watcher-scrawl", assembly.GetName().Name + ":" + name, () =>
                    {
                        var type = assembly.GetType(name, throwOnError: false);
                        if (type == null) return;
                        var onPlay = type.GetMethod("OnPlay", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        var stateMachine = onPlay?.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
                        var body = stateMachine?.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (body == null || Patched.Contains(body)) return;
                        var il = PatchProcessor.GetCurrentInstructions(body);
                        if (!Matches(il)) return;
                        Patcher.Patch(body, transpiler: new HarmonyMethod(typeof(SharedHandWatcherCompatibility), nameof(Transpiler)));
                        Patched.Add(body);
                    });
                }
                ScanHistory(assembly);
            });
            Scanned.Add(assembly);
        }
    }

    private static void ScanHistory(Assembly assembly)
    {
        var types = CompatibilityScan.Types(assembly);
        foreach (var type in types)
        {
            CompatibilityScan.Attempt("watcher-history-type", CompatibilityScan.Name(type), () =>
            {
                bool target = false;
                for (var parent = type; parent != null; parent = parent.DeclaringType)
                    if (parent.FullName != null && HistoryTypes.Contains(parent.FullName)) { target = true; break; }
                if (!target) return;
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic |
                    BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    CompatibilityScan.Attempt("watcher-history-method", CompatibilityScan.Name(method), () =>
                    {
                        if (Patched.Contains(method) || method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) return;
                        var il = PatchProcessor.GetCurrentInstructions(method).ToArray();
                        if (!Enumerable.Range(0, Math.Max(0, il.Length - 1)).Any(i => IsHistoryOwnerRead(il, i))) return;
                        Patcher.Patch(method, transpiler: new HarmonyMethod(typeof(SharedHandWatcherCompatibility), nameof(HistoryTranspiler)));
                        Patched.Add(method);
                    });
                }
            });
        }
    }

    // Card.Owner returns to its storage holder after a borrowed play. The
    // immutable CardPlay.Player is the actual actor for that historical play.
    private static Player HistoryOwner(CardPlay play) => SharedHandSemantics.Active ? play.Player : play.Card.Owner;

    private static bool IsHistoryOwnerRead(CodeInstruction[] il, int i) =>
        il[i].Calls(AccessTools.PropertyGetter(typeof(CardPlay), nameof(CardPlay.Card))) &&
        il[i + 1].Calls(AccessTools.PropertyGetter(typeof(CardModel), nameof(CardModel.Owner)));

    private static IEnumerable<CodeInstruction> HistoryTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var il = instructions.ToArray();
        for (int i = 0; i + 1 < il.Length; i++)
            if (IsHistoryOwnerRead(il, i))
            {
                il[i].opcode = OpCodes.Call;
                il[i].operand = AccessTools.Method(typeof(SharedHandWatcherCompatibility), nameof(HistoryOwner));
                il[i + 1].opcode = OpCodes.Nop;
                il[i + 1].operand = null;
            }
        return il;
    }

    private static bool IsTen(CodeInstruction instruction) =>
        (instruction.opcode == OpCodes.Ldc_I4_S && instruction.operand is sbyte s && s == 10) ||
        (instruction.opcode == OpCodes.Ldc_I4 && instruction.operand is int n && n == 10);

    private static bool Matches(IEnumerable<CodeInstruction> instructions)
    {
        var il = instructions.ToArray();
        return il.Count(IsTen) == 1 && il.Any(i => i.opcode == OpCodes.Sub) &&
            il.Any(i => i.operand is MethodInfo m && m.DeclaringType == typeof(CardPileCmd) && m.Name == nameof(CardPileCmd.Draw)) &&
            il.Any(i => i.operand is MethodInfo m && m.DeclaringType == typeof(CardPile) && m.Name == "get_Cards");
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var il = instructions.ToArray();
        if (!Matches(il)) return il;
        foreach (var instruction in il.Where(IsTen))
        {
            instruction.opcode = OpCodes.Call;
            instruction.operand = AccessTools.Method(typeof(SharedHandLimits), nameof(SharedHandLimits.Maximum));
        }
        return il;
    }
}

