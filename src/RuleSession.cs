using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Entities.Players;

namespace SharedHandPrototype;

// Run-wide reliable channel: room-targeted combat packets cannot configure a
// new adventure, rewards, or a restored save before the first combat exists.
internal static class RuleSession
{
    internal static readonly PersistentValue<IRunState, string> Saved = new(() => "", "SharedHand_v6_Rules");
    private sealed class Message
    {
        public int Version { get; set; } = 18;
        public bool Request { get; set; }
        public string Run { get; set; } = "";
        public GameplaySettings? Rules { get; set; }
    }
    private static readonly ConcurrentQueue<(Message message, ulong sender, INetGameService net)> Inbox = new();
    private static readonly SyncMessageDescriptor<Message> Descriptor = new(Entry.ModId,
        "shared_hand_rules_v15", m => JsonSerializer.SerializeToUtf8Bytes(m),
        bytes => bytes.Length is > 0 and <= 4096 ? JsonSerializer.Deserialize<Message>(bytes)! : throw new FormatException(),
        // StartRunLobby enables vanilla buffering before NGame.StartRun. That
        // method awaits our rules barrier before Launch releases the buffer.
        // Only this bootstrap channel must bypass it; Receive still just queues
        // data, and Tick validates the service, run and authoritative sender.
        Receive, LocationTargeted: false, ShouldBuffer: false, Mode: NetTransferMode.Reliable,
        DispatchLocalOnBroadcast: false, ShouldBroadcast: false);
    private static Task Receive(SyncMessageContext<Message> c)
    { Inbox.Enqueue((c.Message, c.SenderNetId, c.NetService)); return Task.CompletedTask; }
    private static IRunState? _run;
    private static GameplaySettings? _active;
    private static bool _confirmed;
    private static ulong _lastHello;
    private static GameplaySettings? _queuedRuntime;
    internal static GameplaySettings Current => _active ?? new();
    internal static bool Ready => _active != null && _confirmed;
    internal static bool SupportedParty => _run?.Players.Count is >= 2 and <= PartyLimits.MaxPlayers;
    internal static void Install()
    {
        _ = SharedDeckSync.Identity;
        _ = StartingDeckRules.Adjusted;
        SyncMessages.Register(Descriptor);
    }
    private static string Key(IRunState run) => run.Rng.StringSeed + ":" + string.Join(",", run.Players.Select(p => p.NetId));
    internal static void Tick()
    {
        var manager = RunManager.Instance;
        if (manager == null) return;
        var run = manager.DebugOnlyGetState();
        if (!ReferenceEquals(_run, run)) { _run = run; _active = null; _confirmed = false; _lastHello = 0; _queuedRuntime = null; }
        if (run == null || run.Players.Count is not (>= 2 and <= PartyLimits.MaxPlayers)) { while (Inbox.TryDequeue(out _)) { } return; }
        var net = manager.NetService;
        // During a restored lobby the run can exist one frame before its
        // network service is ready. Leave queued rules intact until it is.
        if (net == null) return;
        if (_active == null)
        {
            if(net.Type != NetGameType.Client)SharedHandConfig.SetAutomaticHandLimit(run.Players.Count);
            var saved = Saved.Get(run);
            if (!string.IsNullOrEmpty(saved)) _active = JsonSerializer.Deserialize<GameplaySettings>(saved)?.Normalize();
            // Continue an existing adventure with the host's current deck/pile
            // switches and cap/draw settings. Starter reduction remains save-owned.
            if (_active != null && net.Type != NetGameType.Client)
                Apply((_active with { HandLimit = SharedHandConfig.PublicHandLimit, DrawPerTurn = SharedHandConfig.BaseDraw,
                    Advanced=SharedHandConfig.AdvancedRequested, OnlyHands=SharedHandConfig.OnlyHands, BoardDuel=SharedHandConfig.BoardDuelEnabled, BoardCards=SharedHandConfig.BoardDuelCards, BoardTurnSeconds=SharedHandConfig.BoardDuelTurnSeconds, BoardRpsSeconds=SharedHandConfig.BoardDuelRpsSeconds, UnifiedForge=SharedHandConfig.UnifiedForge, PrivateHands=SharedHandConfig.PrivateHands, Harmony=SharedHandConfig.Harmony, LockSelections=SharedHandConfig.LockSelections, ShareDeck = SharedHandConfig.SharedDeck, Piles = SharedHandConfig.PileMode, IndependentCopies = SharedHandConfig.IndependentPiles, TeammateCardLimit = SharedHandConfig.Requested.TeammateCardLimit }).Normalize());
            if (_active == null && net.Type != NetGameType.Client) Apply(SharedHandConfig.Requested.Normalize());
        }
        if (_active != null && net.Type != NetGameType.Client) _confirmed = true;
        for (int i = 0; i < 32 && Inbox.TryDequeue(out var item); i++)
        {
            var m = item.message;
            if (!ReferenceEquals(net, item.net) || m.Version != 18 || m.Run != Key(run)) continue;
            if (m.Request && net.Type == NetGameType.Host && run.Players.Any(p => p.NetId == item.sender) && _active != null)
                SyncMessages.SendToPeer(net, item.sender, Descriptor, new Message { Run = Key(run), Rules = _active });
            else if (!m.Request && net is NetClientGameService client && item.sender == client.HostNetId && m.Rules != null)
            {
                var received = m.Rules.Normalize();
                // The host may override cap/draw on reload. Starter reduction is never replayed.
                if (_active != null && (_active with { Advanced=received.Advanced, OnlyHands=received.OnlyHands, BoardDuel=received.BoardDuel, BoardCards=received.BoardCards, BoardTurnSeconds=received.BoardTurnSeconds, BoardRpsSeconds=received.BoardRpsSeconds, UnifiedForge=received.UnifiedForge, PrivateHands=received.PrivateHands, Harmony=received.Harmony, LockSelections=received.LockSelections, ShareDeck = received.ShareDeck, Piles = received.Piles, IndependentCopies = received.IndependentCopies, TeammateCardLimit = received.TeammateCardLimit,
                    SmallerDeck = received.SmallerDeck, HandLimit = received.HandLimit, DrawPerTurn = received.DrawPerTurn }).Normalize() != received)
                    throw new InvalidOperationException("房主共享手牌规则与当前冒险存档不一致，请所有玩家重新载入同一存档。");
                Apply(received);
            }
        }
        var now = Time.GetTicksMsec();
        if (net.Type == NetGameType.Client && !_confirmed && (_lastHello == 0 || now - _lastHello > 1000))
        {
            _lastHello = now;
            SyncMessages.SendToHost(net, Descriptor, new Message { Request = true, Run = Key(run) });
        }
        QueueRuntimeChange();
    }
    private static GameplaySettings RuntimeRequested() => (Current with
    { Advanced=SharedHandConfig.AdvancedRequested, OnlyHands=SharedHandConfig.OnlyHands, BoardDuel=SharedHandConfig.BoardDuelEnabled, BoardCards=SharedHandConfig.BoardDuelCards, BoardTurnSeconds=SharedHandConfig.BoardDuelTurnSeconds, BoardRpsSeconds=SharedHandConfig.BoardDuelRpsSeconds, UnifiedForge=SharedHandConfig.UnifiedForge, PrivateHands=SharedHandConfig.PrivateHands, Harmony=SharedHandConfig.Harmony, LockSelections=SharedHandConfig.LockSelections, ShareDeck = SharedHandConfig.SharedDeck, Piles = SharedHandConfig.PileMode, IndependentCopies = SharedHandConfig.IndependentPiles, TeammateCardLimit = SharedHandConfig.Requested.TeammateCardLimit }).Normalize();
    private static void QueueRuntimeChange()
    {
        if (!Ready || _run == null || RunManager.Instance.NetService.Type == NetGameType.Client ||
            CombatManager.Instance.IsInProgress || _run.CurrentRoom is CombatRoom ||
            NRun.Instance == null || !ReferenceEquals(AccessTools.Field(typeof(NRun), "_state").GetValue(NRun.Instance), _run) ||
            RunManager.Instance.ActionExecutor.IsPaused) return;
        var desired = RuntimeRequested();
        if (desired == Current || _queuedRuntime != null) return;
        _queuedRuntime = desired;
        // Use the native ordered action envelope, rather than a sidecar callback
        // racing rewards, transformations or saving. This internal payload is
        // intercepted before DevConsole; no command or console window is opened.
        var net = RunManager.Instance.NetService;
        var owner = _run.Players.FirstOrDefault(p => p.NetId == net.NetId) ?? _run.Players[0];
        string payload = JsonSerializer.Serialize(new RuntimeChange { Run = Key(_run), Rules = desired });
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(owner,
            RuntimeCommand + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload)), false));
    }
    internal const string RuntimeCommand = "__shared_hand_rules_v15 ";
    private sealed class RuntimeChange
    {
        public string Run { get; set; } = "";
        public GameplaySettings Rules { get; set; } = new();
    }
    internal static void ExecuteRuntime(ConsoleCmdGameAction action)
    {
        // If a room transition overtakes this action, retry the host preference
        // after combat instead of retaining an unfinishable pending marker.
        _queuedRuntime = null;
        if (_run == null || !Ready) return;
        var net = RunManager.Instance.NetService;
        ulong host = net is NetClientGameService client ? client.HostNetId : net.NetId;
        if (action.Player.NetId != host || CombatManager.Instance.IsInProgress) return;
        var change = JsonSerializer.Deserialize<RuntimeChange>(Convert.FromBase64String(action.Cmd.Substring(RuntimeCommand.Length)));
        if (change == null || change.Run != Key(_run)) return;
        var desired = change.Rules.Normalize();
        if ((Current with { Advanced=desired.Advanced, OnlyHands=desired.OnlyHands, BoardDuel=desired.BoardDuel, BoardCards=desired.BoardCards, BoardTurnSeconds=desired.BoardTurnSeconds, BoardRpsSeconds=desired.BoardRpsSeconds, UnifiedForge=desired.UnifiedForge, PrivateHands=desired.PrivateHands, Harmony=desired.Harmony, LockSelections=desired.LockSelections, ShareDeck = desired.ShareDeck, Piles = desired.Piles, IndependentCopies = desired.IndependentCopies, TeammateCardLimit = desired.TeammateCardLimit }).Normalize() != desired)
            throw new InvalidOperationException("局中规则消息包含了不允许修改的开局参数。");
        Apply(desired);
        StartingDeckRules.Adjusted.Set(_run, true);
        SharedDeckSync.Initialize(_run, reconcile: true);
        SharedPanel.MarkDirty();
    }
    private static void Apply(GameplaySettings rules)
    {
        _active = rules;
        _confirmed = true;
        if (_run != null) Saved.Set(_run, JsonSerializer.Serialize(rules));
    }
    internal static async Task WaitReady()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        while (true)
        {
            Tick();
            if (!SupportedParty || Ready) break;
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        if (_run != null && SupportedParty) SharedDeckSync.Initialize(_run);
    }
    internal static void NewRun() { _run = null; _active = null; Tick(); }
    internal static void LoadedRun()
    {
        var run = RunManager.Instance.DebugOnlyGetState();
        if (run?.Players.Count is >= 2 and <= PartyLimits.MaxPlayers && string.IsNullOrEmpty(Saved.Get(run)))
        {
            // A pre-0.5 save has no opt-in. Keep the existing 5/15 separate
            // decks, rather than shrinking acquired cards or merging a live run
            // because the user changed their next-adventure menu preferences.
            Saved.Set(run, JsonSerializer.Serialize(new GameplaySettings()));
            StartingDeckRules.Adjusted.Set(run, true);
        }
        NewRun();
    }
}

[HarmonyPatch(typeof(ConsoleCmdGameAction), "ExecuteAction")]
internal static class RuntimeRulesActionPatch
{
    private static bool Prefix(ConsoleCmdGameAction __instance, ref Task __result)
    {
        if (!__instance.Cmd.StartsWith(RuleSession.RuntimeCommand, StringComparison.Ordinal)) return true;
        RuleSession.ExecuteRuntime(__instance); __result = Task.CompletedTask; return false;
    }
}

[HarmonyPatch(typeof(RunManager), "InitializeSavedRun")]
internal static class LoadedRunRulesPatch
{
    private static void Postfix() => RuleSession.LoadedRun();
}

[HarmonyPatch(typeof(RunManager), "InitializeNewRun")]
internal static class NewRunRulesPatch
{
    private static void Postfix() => RuleSession.NewRun();
}

// Gate setup before starting relic effects or loading a room, so native setup
// cannot clone an unsynchronized deck while a configuration packet is in flight.
[HarmonyPatch]
internal static class RunRulesBarrierPatch
{
    [ThreadStatic] private static bool _reentry;
    private static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(RunManager), nameof(RunManager.FinalizeStartingRelics));
        yield return AccessTools.Method(typeof(RunManager), "EnterRoomInternal");
    }
    private static bool Prefix(RunManager __instance, MethodBase __originalMethod, object[] __args, ref Task __result)
    {
        if (_reentry) return true;
        __result = Run(__instance, __originalMethod, __args);
        return false;
    }
    private static async Task Run(RunManager manager, MethodBase method, object[] args)
    {
        await RuleSession.WaitReady();
        await DeckCheckpoint.Synchronize();
        Task task;
        _reentry = true;
        try { task = (Task)((MethodInfo)method).Invoke(manager, args)!; }
        finally { _reentry = false; }
        await task;
    }
}






