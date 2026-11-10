using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;


namespace SharedHandPrototype;

// Repair deletions made through local-only debug tools or direct list edits.
// Exchange tombstones, never stale full decks that could resurrect a removed
// card or overwrite another player's legitimate rewards.
internal static class DeckCheckpoint
{
    private sealed class Message
    {
        public int Version { get; set; } = 12;
        public int Kind { get; set; } // request, probe, report, commit, ack
        public string Run { get; set; } = "";
        public Guid Operation { get; set; }
        public string[] Deleted { get; set; } = Array.Empty<string>();
    }
    private sealed class Batch
    {
        internal readonly HashSet<ulong> Expected = new(), Reported = new(), Acked = new();
        internal readonly HashSet<string> Deleted = new();
        internal ulong Created;
        internal bool Committed;
    }
    private static readonly ConcurrentQueue<(Message message, ulong sender, INetGameService net)> Inbox = new();
    private static readonly Dictionary<Guid, Batch> Batches = new();
    private static readonly Dictionary<Guid, TaskCompletionSource> Pending = new();
    private static readonly SyncMessageDescriptor<Message> Descriptor = new(Entry.ModId,
        "shared_hand_deck_checkpoint_v12", m => JsonSerializer.SerializeToUtf8Bytes(m),
        bytes => bytes.Length is > 0 and <= 131072 ? JsonSerializer.Deserialize<Message>(bytes)! : throw new FormatException(),
        // A checkpoint also runs before starting relics, while native messages
        // are buffered. Its probe/report/commit/ack must not wait for Launch.
        Receive, LocationTargeted: false, ShouldBuffer: false, Mode: NetTransferMode.Reliable,
        DispatchLocalOnBroadcast: false, ShouldBroadcast: false);
    private static Task Receive(SyncMessageContext<Message> c)
    { Inbox.Enqueue((c.Message, c.SenderNetId, c.NetService)); return Task.CompletedTask; }
    internal static void Install() => SyncMessages.Register(Descriptor);
    private static string Key(IRunState run) => run.Rng.StringSeed + ":" + string.Join(",", run.Players.Select(p => p.NetId));
    private static ulong[] Controllers(IRunState run)
    {
        var net = RunManager.Instance.NetService;
        return run.Players.Select(p => PuppetCompat.ControllerOf(p.NetId)).Distinct()
            .Where(id => SyncMessages.PeerConnected(net, id)).ToArray();
    }
    private static Message Packet(IRunState run, Guid id, int kind, IEnumerable<string>? deleted = null) =>
        new() { Kind = kind, Run = Key(run), Operation = id, Deleted = deleted?.ToArray() ?? Array.Empty<string>() };
    private static void Send(INetGameService net, ulong peer, Message packet)
    {
        if (peer == net.NetId) Inbox.Enqueue((packet, net.NetId, net));
        else SyncMessages.SendToPeer(net, peer, Descriptor, packet);
    }
    internal static void Tick()
    {
        var run = RunManager.Instance.DebugOnlyGetState();
        if (run == null || run.Players.Count is not (>= 2 and <= PartyLimits.MaxPlayers)) { while (Inbox.TryDequeue(out _)) { } return; }
        if (!SharedDeckSync.Enabled(run)) return;
        var net = RunManager.Instance.NetService;
        for (int i = 0; i < 64 && Inbox.TryDequeue(out var item); i++)
        {
            var m = item.message;
            if (!ReferenceEquals(net, item.net) || m.Version != 12 || m.Operation == Guid.Empty || m.Run != Key(run) ||
                m.Deleted == null || m.Deleted.Length > 2048 || m.Deleted.Any(id => id == null || id.Length > 80)) continue;
            bool host = net.Type == NetGameType.Host;
            bool fromHost = host ? item.sender == net.NetId : net is NetClientGameService client && item.sender == client.HostNetId;
            if (host && m.Kind == 1 && Controllers(run).Contains(item.sender))
            {
                if (Batches.ContainsKey(m.Operation)) continue;
                var b = new Batch { Created = Time.GetTicksMsec() };
                b.Expected.UnionWith(Controllers(run)); Batches[m.Operation] = b;
                foreach (var peer in b.Expected) Send(net, peer, Packet(run, m.Operation, 2));
            }
            else if (m.Kind == 2 && fromHost)
            {
                var report = Packet(run, m.Operation, 3, SharedDeckSync.LocalDeletions(run, net.NetId));
                if (host) Send(net, net.NetId, report); else SyncMessages.SendToHost(net, Descriptor, report);
            }
            else if (host && m.Kind == 3 && Batches.TryGetValue(m.Operation, out var batch) && batch.Expected.Contains(item.sender))
            {
                if (batch.Committed || !batch.Reported.Add(item.sender)) continue;
                batch.Deleted.UnionWith(m.Deleted);
                if (!batch.Reported.SetEquals(batch.Expected)) continue;
                batch.Committed = true;
                SharedDeckSync.ApplyDeletions(run, batch.Deleted);
                foreach (var peer in batch.Expected) Send(net, peer, Packet(run, m.Operation, 4, batch.Deleted));
            }
            else if (m.Kind == 4 && fromHost)
            {
                SharedDeckSync.ApplyDeletions(run, m.Deleted);
                var ack = Packet(run, m.Operation, 5);
                if (host) Send(net, net.NetId, ack); else SyncMessages.SendToHost(net, Descriptor, ack);
                if (!host && Pending.Remove(m.Operation, out var complete)) complete.TrySetResult();
            }
            else if (host && m.Kind == 5 && Batches.TryGetValue(m.Operation, out var done) && done.Committed && done.Expected.Contains(item.sender))
            {
                done.Acked.Add(item.sender);
                if (!done.Acked.SetEquals(done.Expected)) continue;
                if (Pending.Remove(m.Operation, out var complete)) complete.TrySetResult();
                Batches.Remove(m.Operation);
            }
        }
        // A peer that disconnects after a batch starts must not keep the batch
        // (and the room-entry barrier waiting on it) open until the timeout.
        foreach (var pair in Batches.ToArray())
        {
            var batch = pair.Value;
            batch.Expected.RemoveWhere(id => !SyncMessages.PeerConnected(net, id));
            if (batch.Committed || !batch.Reported.SetEquals(batch.Expected)) continue;
            batch.Committed = true;
            SharedDeckSync.ApplyDeletions(run, batch.Deleted);
            foreach (var peer in batch.Expected) Send(net, peer, Packet(run, batch.Operation, 4, batch.Deleted));
        }
        foreach (var id in Batches.Where(p => Time.GetTicksMsec() - p.Value.Created > 10000).Select(p => p.Key).ToArray()) Batches.Remove(id);
    }
    internal static async Task Synchronize()
    {
        var run = RunManager.Instance.DebugOnlyGetState();
        if (run == null || !SharedDeckSync.Enabled(run)) return;
        var net = RunManager.Instance.NetService;
        if (net.Type is not (NetGameType.Host or NetGameType.Client))
        { SharedDeckSync.ApplyDeletions(run, SharedDeckSync.LocalDeletions(run, net.NetId)); return; }
        var id = Guid.NewGuid();
        var completion = new TaskCompletionSource(); Pending[id] = completion;
        var request = Packet(run, id, 1);
        if (net.Type == NetGameType.Host) Send(net, net.NetId, request);
        else SyncMessages.SendToHost(net, Descriptor, request);
        var start = Time.GetTicksMsec(); var tree = (SceneTree)Engine.GetMainLoop();
        while (!completion.Task.IsCompleted && ReferenceEquals(run, RunManager.Instance.DebugOnlyGetState()) && Time.GetTicksMsec() - start < 5000)
        { Tick(); await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame); }
        Pending.Remove(id);
        if (!completion.Task.IsCompleted) Entry.Status = "卡组删除校验超时，未确认对端已同步。";
    }
}

[HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRun))]
internal static class SharedDeckSaveCheckpointPatch
{
    [ThreadStatic] private static bool _reentry;
    private static bool Prefix(SaveManager __instance, MethodBase __originalMethod, object[] __args, ref Task __result)
    {
        if (_reentry) return true;
        __result = Save(__instance, __originalMethod, __args); return false;
    }
    private static async Task Save(SaveManager instance, MethodBase method, object[] args)
    {
        await DeckCheckpoint.Synchronize();
        Task task; _reentry = true;
        try { task = (Task)((MethodInfo)method).Invoke(instance, args)!; }
        finally { _reentry = false; }
        await task;
    }
}




