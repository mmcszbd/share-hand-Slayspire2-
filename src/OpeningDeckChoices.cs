using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Hooks;

namespace SharedHandPrototype;

// Contextless permanent-deck selectors synchronize a choice, but do not resume
// its resulting effects through the combat action queue. In an opening hook,
// that can allocate generated-card IDs in a different order on each peer.
// Carry only the current native setup context across its asynchronous work.
// The scope is restored immediately on return to the caller, so frame previews,
// other players, noncombat screens and singleplayer keep their original behavior.
[HarmonyPatch]
internal static class OpeningChoiceContext
{
    private static readonly AsyncLocal<HookPlayerChoiceContext?> Current = new();
    internal static HookPlayerChoiceContext? Active
    {
        get
        {
            var context = Current.Value;
            var state = context?.Owner?.Creature.CombatState;
            return Entry.Ready && context?.Owner?.Creature.IsAlive == true && SharedRules.IsSupportedParty(state) &&
                ReferenceEquals(state, CombatManager.Instance.DebugOnlyGetState()) &&
                !CombatManager.Instance.IsOverOrEnding ? context : null;
        }
    }
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CombatManager), "SetupPlayerTurn");
        // Round-robin draws execute several players' draw hooks inside one
        // caller. Bind the explicit hook context instead of inheriting that
        // caller's context. Also cover choices preceding automatic controllers.
        foreach (var name in new[] { nameof(Hook.BeforeHandDraw), nameof(Hook.AfterCardDrawn),
                     nameof(Hook.AfterPlayerTurnStart), nameof(Hook.AfterAutoPrePlayPhaseEntered) })
            yield return AccessTools.Method(typeof(Hook), name);
    }
    private static void Prefix(object[] __args, out HookPlayerChoiceContext? __state)
    {
        __state = Current.Value;
        var context = __args.OfType<PlayerChoiceContext>().FirstOrDefault() as HookPlayerChoiceContext;
        Current.Value = SharedRules.IsSupportedParty(context?.Owner?.Creature.CombatState) ? context : null;
    }
    private static void Postfix(HookPlayerChoiceContext? __state) => Current.Value = __state;
    private static void Finalizer(HookPlayerChoiceContext? __state) => Current.Value = __state;
}

[HarmonyPatch]
internal static class OpeningDeckChoices
{
    [ThreadStatic] private static bool _invokingNative;
    private static IEnumerable<MethodBase> TargetMethods()
    {
        // Patch leaf selection APIs, not wrappers such as FromDeckForRemoval.
        // APIs already accepting PlayerChoiceContext handle their own actions.
        foreach (var method in typeof(CardSelectCmd).GetMethods(BindingFlags.Public | BindingFlags.Static))
            if (method.Name is nameof(CardSelectCmd.FromDeckGeneric) or
                    nameof(CardSelectCmd.FromDeckForUpgrade) or
                    nameof(CardSelectCmd.FromDeckForTransformation) or
                    nameof(CardSelectCmd.FromChooseABundleScreen) ||
                method.Name == nameof(CardSelectCmd.FromDeckForEnchantment) &&
                    method.GetParameters()[0].ParameterType == typeof(IReadOnlyList<CardModel>))
                yield return method;
    }
    private static bool Prefix(MethodBase __originalMethod, object[] __args,
        ref Task<IEnumerable<CardModel>> __result)
    {
        if (_invokingNative || CardSelectCmd.Selector != null || OpeningChoiceContext.Active is not {} context)
            return true;
        var player = __args.OfType<Player>().FirstOrDefault();
        // The list-based enchant selector chooses cards[0].Owner natively.
        // Preserve that chooser even when a hook targets another player's deck.
        if (player == null && __args.FirstOrDefault() is IReadOnlyList<CardModel> cards && cards.Count > 0)
            player = cards[0].Owner;
        if (player == null || !player.Creature.IsAlive) return true;
        __result = Select(context, player, __originalMethod, __args);
        return false;
    }
    private static async Task<IEnumerable<CardModel>> Select(HookPlayerChoiceContext context,
        Player player, MethodBase method, object[] arguments)
    {
        await context.SignalPlayerChoiceBegun(player, PlayerChoiceOptions.None);
        try
        {
            Task<IEnumerable<CardModel>> native;
            _invokingNative = true;
            try { native = (Task<IEnumerable<CardModel>>)method.Invoke(null, arguments)!; }
            finally { _invokingNative = false; }
            return await native;
        }
        finally
        {
            // Resume through the native ordered action queue before returning to
            // the relic/power that creates, upgrades, removes or transforms cards.
            await context.SignalPlayerChoiceEnded();
        }
    }
}

