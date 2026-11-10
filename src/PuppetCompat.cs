using System;
using System.Linq;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

// Optional bridge to Steam Random Match's existing, host-authoritative bindings.
// A fake-looking ID alone never grants control or satisfies a handshake.
internal static class PuppetCompat
{
    private static MethodInfo? _controller;
    private static MethodInfo? _focus;
    private static FieldInfo? _captures, _captureKind, _captureFilter;
    private static bool _choiceGuard;
    private static bool _uiGuard, _deathGuard, _enemyGuard, _propagatingEnemy;
    private static long _lastScan;

    internal static ulong ControllerOf(ulong player)
    {
        try
        {
            if (_controller == null && Environment.TickCount64 - _lastScan >= 1000)
            {
                _lastScan = Environment.TickCount64;
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "SteamRandomMatchMod");
                _controller = assembly?.GetType("SteamRandomMatchMod.Puppet.PuppetSession")?.GetMethod(
                    "TryGetController", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(ulong), typeof(ulong).MakeByRefType() }, null);
                _focus = assembly?.GetType("SteamRandomMatchMod.Puppet.PuppetSession")?.GetMethod("GetLocalFocus");
                var choices = assembly?.GetType("SteamRandomMatchMod.Puppet.PuppetDeferredCardSelect");
                var isolation = assembly?.GetType("SteamRandomMatchMod.Puppet.PuppetCardSelectIsolation");
                var belongs = isolation?.GetMethod("CardsBelongToPlayer", BindingFlags.Static | BindingFlags.NonPublic);
                if (!_choiceGuard && choices != null && belongs != null)
                {
                    _captures = AccessTools.Field(choices, "Captures");
                    var capture = choices.GetNestedType("Capture", BindingFlags.NonPublic);
                    _captureKind = capture == null ? null : AccessTools.Field(capture, "Kind");
                    _captureFilter = capture == null ? null : AccessTools.Field(capture, "Filter");
                    if (_captures != null && _captureKind != null && _captureFilter != null)
                    {
                        new Harmony(Entry.ModId + ".puppet-shared-choice").Patch(belongs,
                            prefix: new HarmonyMethod(typeof(PuppetCompat), nameof(SharedHandChoicePrefix)));
                        _choiceGuard = true;
                    }
                }
                var endTurn = assembly?.GetType("SteamRandomMatchMod.Puppet.PuppetEndTurnReadyPropagate")?.GetMethod("PropagateEndTurn", BindingFlags.Static | BindingFlags.NonPublic);
                if (!_deathGuard && endTurn != null)
                {
                    new Harmony(Entry.ModId + ".puppet-death").Patch(endTurn,
                        prefix: new HarmonyMethod(typeof(PuppetCompat), nameof(DeadEndTurnPrefix)));
                    _deathGuard = true;
                }
                var beginEnemy=assembly?.GetType("SteamRandomMatchMod.Puppet.PuppetEndTurnReadyPropagate")?.GetMethod("PropagateBeginEnemyTurn", BindingFlags.Static | BindingFlags.NonPublic);
                if(!_enemyGuard&&beginEnemy!=null)
                {
                    new Harmony(Entry.ModId+".puppet-dead-enemy").Patch(beginEnemy,
                        postfix:new HarmonyMethod(typeof(PuppetCompat),nameof(DeadEnemyReadyPostfix)));
                    _enemyGuard=true;
                }
                var sync = assembly?.GetType("SteamRandomMatchMod.Puppet.PuppetCombatUiSync")?.GetMethod(
                    "SyncHandIfFocused", BindingFlags.Static | BindingFlags.NonPublic);
                if (!_uiGuard && sync != null && _focus != null)
                {
                    new Harmony(Entry.ModId + ".puppet-ui").Patch(sync,
                        prefix: new HarmonyMethod(typeof(PuppetCompat), nameof(FocusedHandSyncPrefix)));
                    _uiGuard = true;
                }
            }
            if (_controller != null)
            {
                var args = new object[] { player, 0UL };
                if ((bool)_controller.Invoke(null, args)! && (ulong)args[1] != 0)
                    return (ulong)args[1];
            }
        }
        catch { /* Missing/changed optional API leaves normal peer rules intact. */ }
        return player;
    }

    // Vanilla automatically readies dead roles. That is not a human pressing
    // End Turn and must never ready their living controller or fellow puppets.
    private static bool DeadEndTurnPrefix(Player source) => !Entry.Ready || source?.Creature.IsDead != true;

    // This second barrier includes every network role, including the dead.
    // Random Match propagates only to living roles; dead puppets have no
    // independent network process to send their acknowledgement.
    private static void DeadEnemyReadyPostfix(Player source,Func<System.Threading.Tasks.Task>? actionDuringEnemyTurn)
    {
        if(!Entry.Ready||_propagatingEnemy||source==null)return;
        var state=MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState();
        if(state==null)return;
        _propagatingEnemy=true;
        try
        {
            var controller=ControllerOf(source.NetId);
            foreach(var player in state.Players.Where(p=>p.Creature.IsDead&&ControllerOf(p.NetId)==controller))
                MegaCrit.Sts2.Core.Combat.CombatManager.Instance.SetReadyToBeginEnemyTurn(player,actionDuringEnemyTurn);
        }
        finally{_propagatingEnemy=false;}
    }

    internal static bool MayAct(ulong sender, ulong actor) => actor != 0 && ControllerOf(actor) == sender;

    // Random Match rejects foreign cards for a private hand. Relax its shared
    // deferred/direct-sync guard only for a captured hand/hand-upgrade choice.
    // Other selectors and stale/queued/filtered cards retain their own rules.
    private static bool SharedHandChoicePrefix(Player player, PlayerChoiceResult result, ref bool __result)
    {
        if (!SharedHandSemantics.Active || player == null || _captures?.GetValue(null) is not IDictionary captures || !captures.Contains(player.NetId))
            return true;
        var capture = captures[player.NetId];
        if (_captureKind!.GetValue(capture)?.ToString() is not ("Hand" or "HandUpgrade"))
            return true;
        try
        {
            var cards = result.AsCombatCards().ToArray();
            if (!cards.Any(c => c.Owner != player)) return true;
            var filter = _captureFilter!.GetValue(capture) as Func<CardModel, bool>;
            __result = cards.All(c => SharedSelection.Candidate(c) && (filter?.Invoke(c) ?? true));
            return false;
        }
        catch (InvalidOperationException) { return true; }
    }

    // Random Match queues a deferred callback capturing the previous role. Its
    // SyncHandIfFocused currently accepts that stale role even after a switch.
    // While shared play is active, only the current focus may rebuild this UI.
    private static bool FocusedHandSyncPrefix(Player? __0)
    {
        if (!Entry.Ready || __0 == null || _focus == null) return true;
        var active = (ulong)_focus.Invoke(null, null)!;
        return active == 0 || active == __0.NetId;
    }
}





