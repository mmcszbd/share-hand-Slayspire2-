using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

// Framework calculators may use a literal default (10), so changing the native
// property alone cannot make their answers agree with the public hand limit.
// Resolve optional APIs by signature, including APIs loaded after our Init.
internal static class SharedHandFrameworkLimits
{
    private static readonly Harmony Patcher = new(Entry.ModId + ".framework-hand-limit");
    private static readonly HashSet<MethodInfo> Patched = new();
    private static readonly HashSet<Assembly> Scanned = new();
    private static readonly Dictionary<string, string[]> Targets = new()
    {
        ["BaseLib.Patches.Hooks.MaxHandSizePatch"] = new[] { "GetMaxHandSize", "GetMaxHandSizeFromCard" },
        ["STS2RitsuLib.RitsuLibFramework"] = new[] { "GetMaxHandSize" },
        ["STS2RitsuLib.Combat.HandSize.MaxHandSizeCalculator"] = new[]
            { "Calculate", "CalculateFromCardOwner", "ApplyHookListenerModifiers" }
    };

    internal static int PatchedMethods => Patched.Count;

    internal static bool TryMaximum(Player? player, out int maximum)
    {
        var state = CombatManager.Instance.DebugOnlyGetState();
        if (player != null && SharedRules.IsHandSharingParty(state) && state!.Players.Contains(player))
        {
            maximum = RuleSession.Current.HandLimit;
            return true;
        }
        maximum = 0;
        return false;
    }

    internal static void Scan(Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            if (assembly.IsDynamic || Scanned.Contains(assembly)) continue;
            CompatibilityScan.Attempt("framework-assembly", assembly.GetName().Name!, () =>
            {
                foreach (var target in Targets)
                {
                    CompatibilityScan.Attempt("framework-type", assembly.GetName().Name + ":" + target.Key, () =>
                    {
                        var type = assembly.GetType(target.Key, throwOnError: false);
                        if (type == null) return;
                        foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public |
                            BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            CompatibilityScan.Attempt("framework-method", CompatibilityScan.Name(method), () =>
                            {
                                if (Patched.Contains(method) || method.ReturnType != typeof(int) ||
                                    method.ContainsGenericParameters || Array.IndexOf(target.Value, method.Name) < 0) return;
                                var args = method.GetParameters();
                                if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1].ParameterType != typeof(int))) return;
                                string postfix;
                                if (args[0].ParameterType == typeof(Player)) postfix = nameof(PlayerResult);
                                else if (args[0].ParameterType == typeof(CardModel)) postfix = nameof(CardResult);
                                else return;
                                Patcher.Patch(method, postfix: new HarmonyMethod(typeof(SharedHandFrameworkLimits), postfix)
                                {
                                    priority = Priority.Last,
                                    after = new[] { "com.ritsukage.sts2-RitsuLib.interop.max_hand_size" }
                                });
                                Patched.Add(method);
                            });
                        }
                    });
                }
            });
            Scanned.Add(assembly);
        }
    }

    // The host-synchronized public cap is authoritative, including when a
    // framework applies individual-player hand-size modifiers afterward.
    private static void PlayerResult(Player? __0, ref int __result)
    {
        if (TryMaximum(__0, out var maximum)) __result = maximum;
    }

    private static void CardResult(CardModel? __0, ref int __result)
    {
        if (TryMaximum(__0?.Owner, out var maximum)) __result = maximum;
    }
}

