using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace SharedHandPrototype;

// Optional compatibility for YouJin's turn-based duel. No references to the
// other mod are required, and the caller must opt in for the current combat.
internal static class BoardDuelCompat
{
    internal static Func<bool>? Active;
    private static bool _installed;
    private static Type? _duelSession;
    private static PropertyInfo? _inDuel;
    private static FieldInfo? _actor;
    private static FieldInfo? _player;
    private static PropertyInfo? _extraTurns;
    private static MethodInfo? _participants;

    private static bool Enabled()
    {
        try { return Active?.Invoke() == true && _inDuel?.GetValue(null) is true; }
        catch { return false; }
    }

    internal static bool Install()
    {
        if (_installed) return true;
        var actorPatch = AccessTools.TypeByName("PvpDuel.Core.Duel.DuelActorTurnPatch");
        _duelSession = AccessTools.TypeByName("PvpDuel.Core.Duel.DuelSession");
        var turnState = AccessTools.TypeByName("MegaCrit.Sts2.Core.Combat.CombatTurnState");
        if (actorPatch == null || _duelSession == null || turnState == null) return false;
        _inDuel = AccessTools.Property(_duelSession, "InDuel");
        _actor = AccessTools.Field(_duelSession, "<Actor>k__BackingField");
        _participants = AccessTools.Method(_duelSession, "ParticipantsOf");
        _player = AccessTools.Field(typeof(PlayerCombatState), "_player");
        _extraTurns = AccessTools.Property(turnState, "PlayersTakingExtraTurn");
        var start = AccessTools.Method(typeof(CombatManager), "StartTurn");
        var increment = AccessTools.Method(typeof(PlayerCombatState), "IncrementTurnNumber");
        if (_inDuel == null || _actor == null || _participants == null ||
            _player == null || _extraTurns == null || start == null || increment == null) return false;
        var harmony = new Harmony(Entry.ModId + ".board-duel-compat");
        harmony.Patch(start,
            prefix: new HarmonyMethod(typeof(BoardDuelCompat), nameof(BeforeStart)) { priority = Priority.First });
        harmony.Patch(start,
            prefix: new HarmonyMethod(typeof(BoardDuelCompat), nameof(AfterYouJinStart)) { priority = Priority.Last });
        harmony.Patch(increment,
            prefix: new HarmonyMethod(typeof(BoardDuelCompat), nameof(BeforeIncrement)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(BoardDuelCompat), nameof(AfterIncrement)) { priority = Priority.Last });
        _installed = true;
        return true;
    }

    // Remember whether this was a normal turn before YouJin adds its actor.
    // Do not change genuine extra turns initiated by game effects.
    private static void BeforeStart(object __0, out bool __state)
    {
        __state = Enabled() && _extraTurns?.GetValue(__0) is List<Player> turns && turns.Count == 0;
    }

    private static void AfterYouJinStart(object __0, bool __state)
    {
        if (__state && Enabled() && _extraTurns?.GetValue(__0) is List<Player> turns)
            turns.Clear();
    }

    // YouJin normally increments only the one chosen actor's turn. During a
    // shared chess turn temporarily identify each participant as that actor;
    // restore immediately even if another patch throws. Spectators stay gated.
    private sealed class ActorState { internal object? Previous; }

    private static void BeforeIncrement(PlayerCombatState __instance, out ActorState? __state)
    {
        __state = null;
        if (!Enabled() || _player?.GetValue(__instance) is not Player player ||
            CombatManager.Instance.PlayersTakingExtraTurn.Count != 0) return;
        var state = player.Creature.CombatState;
        if (state == null || _participants?.Invoke(null, new object?[] { state }) is not IReadOnlyList<Player> participants ||
            !participants.Contains(player)) return;
        __state = new ActorState { Previous = _actor!.GetValue(null) };
        _actor.SetValue(null, player);
    }

    private static void AfterIncrement(ActorState? __state)
    {
        if (__state != null) _actor?.SetValue(null, __state.Previous);
    }
}
