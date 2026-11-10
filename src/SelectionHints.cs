using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Runs;


namespace SharedHandPrototype;

// Selection presence is relayed by the host. Optional manual-play exclusion is
// checked at host admission only; ordered action execution never consults this
// timing-dependent presence table, so peers cannot diverge during resolution.
internal static class SelectionHints
{
    private sealed record Selected(uint Card, ulong Generation);
    private sealed class Message
    {
        public int Version { get; set; } = 16;
        public Guid Battle { get; set; }
        public ulong Actor { get; set; }
        public ulong Sequence { get; set; }
        public Selected[] Cards { get; set; } = Array.Empty<Selected>();
    }
    private static readonly ConcurrentQueue<(Message message, ulong sender, INetGameService net)> Inbox = new();
    private static readonly SyncMessageDescriptor<Message> Descriptor = new(Entry.ModId,
        "shared_hand_selection_v15", p => JsonSerializer.SerializeToUtf8Bytes(p),
        bytes => bytes.Length is > 0 and <= 16384 ? JsonSerializer.Deserialize<Message>(bytes)! : throw new FormatException(),
        Receive, LocationTargeted: true, ShouldBuffer: false, Mode: NetTransferMode.Reliable,
        DispatchLocalOnBroadcast: false, ShouldBroadcast: false);
    private static Task Receive(SyncMessageContext<Message> context)
    { Inbox.Enqueue((context.Message, context.SenderNetId, context.NetService)); return Task.CompletedTask; }
    private static readonly FieldInfo Play = AccessTools.Field(typeof(NPlayerHand), "_currentCardPlay");
    private static readonly FieldInfo Selection = AccessTools.Field(typeof(NPlayerHand), "_selectedCards");
    private static readonly Dictionary<ulong, (Message message, ulong time)> Remote = new();
    private static readonly Dictionary<NCard, Control> Overlays = new();
    private static Guid _battle;
    private static ulong _lastTick, _lastSent, _sequence;
    private static string _signature = "";
    internal static void Install() => SyncMessages.Register(Descriptor);
    internal static bool IsLocked(CardModel card, MegaCrit.Sts2.Core.Entities.Players.Player actor)
    {
        if (!HandSharingMode.Shared || !Entry.Ready || !RuleSession.Current.LockSelections || _battle != Entry.Battle ||
            !NetCombatCardDb.Instance.TryGetCardId(card, out var id) || !SharedSelection.Candidate(card)) return false;
        var now = Time.GetTicksMsec();
        var generation = HandEntries.Get(card);
        var state = card.CombatState;
        var local = state == null ? null : LocalContext.GetMe(state);
        // The host need not wait for its own next 90 ms heartbeat to protect
        // an actual local selection, and local release takes effect at once.
        if (local != null && local != actor && local.Creature.IsAlive && NPlayerHand.Instance is { } hand &&
            LocalCards(hand).Contains(card)) return true;
        return Remote.Any(p => p.Key != actor.NetId && p.Key != local?.NetId && now - p.Value.time < 1800 &&
            state?.Players.Any(player => player.NetId == p.Key && player.Creature.IsAlive) == true &&
            p.Value.message.Cards.Any(selected => selected.Card == id && selected.Generation == generation));
    }

    internal static bool LockedForLocal(CardModel card) => card.CombatState is { } state &&
        LocalContext.GetMe(state) is { } local && IsLocked(card, local);
    private static IEnumerable<CardModel> LocalCards(NPlayerHand hand)
    {
        if (hand.InCardPlay && Play.GetValue(hand) is NCardPlay play && GodotObject.IsInstanceValid(play))
            yield return play.Holder.CardModel;
        if (!hand.IsInCardSelection) yield break;
        foreach (var card in (List<CardModel>)Selection.GetValue(hand)!) yield return card;
        var preview = AccessTools.Field(typeof(NPlayerHand), "_upgradePreview").GetValue(hand);
        if (preview != null && AccessTools.Property(preview.GetType(), "Card").GetValue(preview) is CardModel upgrade) yield return upgrade;
    }
    internal static void Tick()
    {
        var now = Time.GetTicksMsec();
        if (now - _lastTick < 90) return;
        _lastTick = now;
        if (_battle != Entry.Battle) { _battle = Entry.Battle; Remote.Clear(); _signature = ""; }
        var state = CombatManager.Instance.DebugOnlyGetState();
        var hand = NPlayerHand.Instance;
        if (!HandSharingMode.Shared || !Entry.Ready || state?.Players.Count is not (>= 2 and <= PartyLimits.MaxPlayers) || hand == null)
        { Remote.Clear(); ClearOverlays(); return; }
        var net = RunManager.Instance.NetService;
        // Drop presence data from players whose controller's connection is
        // gone; Publish below already skips those peers, and stale entries
        // must not keep locking local cards after a disconnect.
        foreach (var gone in Remote.Keys.Where(id => !SyncMessages.PeerConnected(net, PuppetCompat.ControllerOf(id))).ToArray()) Remote.Remove(gone);
        while (Inbox.TryDequeue(out var item))
        {
            var m = item.message;
            if (!ReferenceEquals(net, item.net) || m.Version != 16 || m.Battle != _battle ||
                m.Cards == null || m.Cards.Length > 99 || !state.Players.Any(p => p.NetId == m.Actor)) continue;
            if (net.Type == NetGameType.Host && PuppetCompat.MayAct(item.sender, m.Actor))
                Publish(m, now, net);
            else if (net is NetClientGameService client && item.sender == client.HostNetId) Store(m, now);
        }
        var actor = LocalContext.GetMe(state);
        if (actor == null) return;
        var selected = LocalCards(hand).Where(SharedSelection.Candidate).Distinct()
            .Select(c => new Selected(NetCombatCardDb.Instance.GetCardId(c), HandEntries.Get(c))).ToArray();
        var signature = actor.NetId + ":" + string.Join(",", selected.Select(s => s.Card + ":" + s.Generation));
        if (signature != _signature || now - _lastSent > 500)
        {
            _signature = signature; _lastSent = now;
            var m = new Message { Actor = actor.NetId, Cards = selected, Battle = _battle, Sequence = ++_sequence };
            if (net.Type == NetGameType.Host) Publish(m, now, net);
            else SyncMessages.SendToHost(net, Descriptor, m);
        }
        var wanted = SharedHandConfig.SelectionChains ? Remote.Where(p => p.Key != actor.NetId && now - p.Value.time < 1800)
            .SelectMany(p => p.Value.message.Cards).Where(s => NetCombatCardDb.Instance.TryGetCard(s.Card, out var card) &&
                card != null && SharedSelection.Candidate(card) && HandEntries.Get(card) == s.Generation).Select(s => s.Card).ToHashSet() : new();
        // Quota feedback is local and independent of the optional remote
        // selection hint. Reuse the native visual only: never apply Bound to
        // the model, since effects and automatic plays remain legal at cap.
        var quota = !hand.IsInCardSelection ? state.Players
            .SelectMany(p => p.PlayerCombatState?.Hand.Cards ?? Array.Empty<CardModel>())
            .Where(c => TeammatePlayLimit.Blocked(c, actor) && NetCombatCardDb.Instance.TryGetCardId(c, out _))
            .Select(NetCombatCardDb.Instance.GetCardId).ToHashSet() : new HashSet<uint>();
        wanted.UnionWith(quota);
        foreach (var pair in Overlays.ToArray())
            if (!GodotObject.IsInstanceValid(pair.Key) || !GodotObject.IsInstanceValid(pair.Value) || pair.Value.IsQueuedForDeletion() || pair.Key.Model == null ||
                !NetCombatCardDb.Instance.TryGetCardId(pair.Key.Model, out var id) || !wanted.Contains(id))
            { if (GodotObject.IsInstanceValid(pair.Value)) pair.Value.QueueFree(); Overlays.Remove(pair.Key); }
        foreach (var node in CardsBelow(hand))
        {
            if (node.Model == null || !NetCombatCardDb.Instance.TryGetCardId(node.Model, out var id) || !wanted.Contains(id)) continue;
            string name = quota.Contains(id) ? "SharedHandQuotaChain" : "SharedHandSelectionChain";
            if (Overlays.TryGetValue(node, out var existing)) { existing.Name = name; continue; }
            var overlay = ModelDb.Affliction<Bound>().CreateOverlay();
            overlay.Name = name;
            IgnoreMouse(overlay);
            node.OverlayContainer.AddChild(overlay);
            Overlays[node] = overlay;
        }
    }
    private static void Store(Message message, ulong now)
    {
        if (Remote.TryGetValue(message.Actor, out var old) && message.Sequence <= old.message.Sequence) return;
        Remote[message.Actor] = (message, now);
    }
    private static void Publish(Message message, ulong now, INetGameService net)
    {
        Store(message, now);
        foreach (var peer in RunManager.Instance.DebugOnlyGetState()!.Players.Select(p => PuppetCompat.ControllerOf(p.NetId)).Distinct().Where(id => id != net.NetId))
            SyncMessages.SendToPeer(net, peer, Descriptor, message);
    }
    private static IEnumerable<NCard> CardsBelow(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            // Inner stack cards are decorative copies, not playable holders.
            if (child is StackBackdrop) continue;
            if (child is NCard card) yield return card;
            else foreach (var descendant in CardsBelow(child)) yield return descendant;
        }
    }
    private static void IgnoreMouse(Node node)
    {
        if (node is Control c) c.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var child in node.GetChildren()) IgnoreMouse(child);
    }
    private static void ClearOverlays()
    {
        foreach (var overlay in Overlays.Values) if (GodotObject.IsInstanceValid(overlay)) overlay.QueueFree();
        Overlays.Clear();
    }
}




