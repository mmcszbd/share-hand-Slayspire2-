using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;


namespace SharedHandPrototype;

[ModInitializer(nameof(Init))]
public static class Entry
{
    public const string ModId = "SharedHandPrototype";
    internal const string Version = "0.9.23";
    private static readonly MegaCrit.Sts2.Core.Logging.Logger Log = new(ModId, LogType.Generic);
    private static readonly ConcurrentQueue<(Packet packet, ulong sender, INetGameService service)> Incoming = new();
    private static readonly PlayGate Gate = new();
    private static readonly HashSet<ulong> ReadyPeers = new();
    private static readonly HashSet<uint> Awaiting = new();
    private static readonly Dictionary<ulong, uint> LocalRequests = new();
    private static readonly Dictionary<ulong, PlayCardAction> NativeRequests = new();
    private static readonly MethodInfo Enqueue = typeof(ActionQueueSynchronizer).GetMethod(
        "EnqueueAction", BindingFlags.Instance | BindingFlags.NonPublic,
        null, new[] { typeof(GameAction), typeof(ulong) }, null)
        ?? throw new MissingMethodException("Unsupported game: EnqueueAction signature changed.");
    private static readonly SyncMessageDescriptor<Packet> Descriptor = new(
        ModId, "shared_hand_v15", Protocol.Encode, Protocol.Decode, Receive,
        LocationTargeted: true, ShouldBuffer: true, Mode: NetTransferMode.Reliable,
        DispatchLocalOnBroadcast: false, ShouldBroadcast: false);

    private static CombatState? _combat;
    private static Guid _battle;
    private static bool _ready;
    private static bool _installed;
    private static bool _faulted;
    private static ulong _sequence;
    private static ulong _lastHello;
    private static ulong _lastRefresh;
    private static string _lastError = "";
    internal static string Status = "等待所有玩家安装同版本 Mod 并进入战斗。";
    internal static Guid Battle => _battle;
    internal static bool Ready => _ready;
    internal static bool RouteNative => _ready || _faulted;
    internal static bool IsAwaiting(uint card) => Awaiting.Contains(card);

    public static void Init()
    {
        if (_installed) return;
        if (Engine.GetMainLoop() is not SceneTree tree)
            throw new InvalidOperationException("Godot scene tree is unavailable.");
        SharedHandConfig.Register();
        RuleSession.Install();
        SelectionHints.Install();
        DeckSelectionHints.Install();
        DeckCheckpoint.Install();
        SharedRules.Install();
        SyncMessages.Register(Descriptor);
        BoardDuelCoordinator.Install();
        BoardDuelCompat.Active = () => BoardDuelCoordinator.InCombat;
        tree.ProcessFrame += Frame;
        _installed = true;
        Log.Info($"Loaded {Version}: shared-hand rules and native borrowed target validation; experimental party limit {PartyLimits.MaxPlayers}.");
    }

    private static Task Receive(SyncMessageContext<Packet> ctx)
    {
        // Never mutate the scene/game state in a transport callback.
        Incoming.Enqueue((ctx.Message, ctx.SenderNetId, ctx.NetService));
        return Task.CompletedTask;
    }

    private static void Frame()
    {
        try
        {
            SharedHandConfig.Instance.Tick();
            SharedHandSettingsUi.Tick();
            OptionalSettingsFrontends.Tick();
            SyncMessages.Tick();
            RuleSession.Tick();
            if (RuleSession.Ready && RuleSession.Current.BoardDuel)
                BoardDuelCompat.Install();
            DeckCheckpoint.Tick();
            SelectionHints.Tick();
            DeckSelectionHints.Tick();
            var manager = CombatManager.Instance;
            SharedHandSemantics.Scan();
            FriendlyGreedTransport.Tick();
            var state = manager.IsInProgress ? manager.DebugOnlyGetState() : null;
            if (!ReferenceEquals(state, _combat)) Reset(state);
            if (_combat == null || _combat.Players.Count is not (>= 2 and <= PartyLimits.MaxPlayers))
            {
                BoardDuelCoordinator.Reset();
                SharedPanel.Hide();
                while (Incoming.TryDequeue(out _)) { }
                return;
            }
            AllyTargeting.Tick();
            SharedPileCounters.Tick();
            SharedSmog.Tick();
            HandLayouts.Tick();
            for (int i = 0; i < 32 && Incoming.TryDequeue(out var message); i++)
                Handle(message.packet, message.sender, message.service);
            var board = RuleSession.Current;
            BoardDuelCoordinator.Tick(board.BoardDuel && BoardDuelCoordinator.IsYouJinPvp(), board.BoardCards, board.BoardTurnSeconds, board.BoardRpsSeconds);

            var now = Time.GetTicksMsec();
            if (now - _lastHello >= 2000 || _lastHello == 0)
            {
                _lastHello = now;
                SyncMessages.SendToHost(RunManager.Instance, Descriptor,
                    new Packet { Kind = PacketKind.Hello });
            }
            if (NCombatRoom.Instance != null && (SharedPanel.Dirty || now - _lastRefresh >= 150))
            {
                _lastRefresh = now;
                SharedPanel.Refresh(_combat);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Failure(ex);
            Status = "试验功能出错，请查看日志。";
            var error = ex.ToString();
            if (_lastError != error) { _lastError = error; Log.Info(error); }
        }
    }

    private static void Reset(CombatState? state)
    {
        SharedPanel.Hide();
        _combat = state;
        _ready = false;
        _faulted = false;
        _battle = Guid.Empty;
        _lastHello = 0;
        ReadyPeers.Clear();
        Awaiting.Clear();
        LocalRequests.Clear();
        NativeRequests.Clear();
        SharedRules.Queued.Clear();
        if (state != null && RunManager.Instance.NetService.Type == NetGameType.Host)
        {
            _battle = Guid.NewGuid();
            ReadyPeers.Add(RunManager.Instance.NetService.NetId);
        }
        Gate.Reset(_battle);
        Status = "等待全员握手……";
    }

    internal static bool Supports(CardModel card) => StarterCards.Supports(card);

    internal static void Request(uint cardId, uint? enemyId, PlayCardAction? native = null)=>RequestWithRecipient(cardId,enemyId,native,null);
    internal static void RequestWithRecipient(uint cardId, uint? enemyId, PlayCardAction? native, ulong? recipient)
    {
        if (!_ready || _combat == null || Awaiting.Contains(cardId) || SharedRules.Queued.Contains(cardId))
        { native?.Cancel(); return; }
        if (!NetCombatCardDb.Instance.TryGetCard(cardId, out var card) || card == null) { native?.Cancel(); return; }
        var actor = native?.Player ?? LocalContext.GetMe(_combat);
        if (actor?.PlayerCombatState == null || card.Owner.PlayerCombatState == null) { native?.Cancel(); return; }
        if (SelectionHints.IsLocked(card, actor))
        { Status = "队友正在选择这张牌。"; native?.Cancel(); return; }
        if (TeammatePlayLimit.Blocked(card, actor))
        { TeammatePlayLimit.Speak(actor); Status = TeammatePlayLimit.Rejection; native?.Cancel(); return; }
        var sequence = ++_sequence;
        var packet = new Packet
        {
            Recipient=recipient, Kind = PacketKind.Play, Battle = _battle, Sequence = sequence, Card = cardId,
            Generation = HandEntries.Get(card), Holder = card.Owner.NetId, Actor = actor.NetId,
            // Vanilla card actions use null for Self; an explicit self target
            // fails CardModel.IsValidTarget (potions use different semantics).
            Target = card.TargetType == TargetType.Self ? null : enemyId,
            OwnerTurn = card.Owner.PlayerCombatState.TurnNumber,
            ActorTurn = actor.PlayerCombatState.TurnNumber
        };
        Awaiting.Add(cardId);
        LocalRequests[sequence] = cardId;
        if (native != null) NativeRequests[sequence] = native;
        try
        {
            if (SyncMessages.SendToHost(RunManager.Instance, Descriptor, packet))
            { Status = "已发送，等待房主确认。"; return; }
            Status = "发送未被接受；没有执行出牌。";
        }
        catch (Exception ex) { Status = "发送失败：" + ex.Message; Log.Info(ex.ToString()); }
        Awaiting.Remove(cardId);
        LocalRequests.Remove(sequence);
        if (NativeRequests.Remove(sequence, out var failed)) failed.Cancel();
    }

    private static void Handle(Packet p, ulong sender, INetGameService net)
    {
        if (_combat == null || !ReferenceEquals(net, RunManager.Instance.NetService)) return;
        if (p.Kind is PacketKind.Session or PacketKind.Result)
        {
            bool fromHost = net.Type == NetGameType.Host ? sender == net.NetId :
                net is NetClientGameService client && sender == client.HostNetId;
            if (!fromHost) return;
            if (p.Kind == PacketKind.Session)
            {
                if (p.Battle == Guid.Empty) return;
                _battle = p.Battle;
                _ready = p.Ready;
                if (!_ready) Status = string.IsNullOrEmpty(p.Text) ? "等待另一位玩家的试验 Mod 握手。" : p.Text;
                else if (Status.StartsWith("等待")) Status = "共享已就绪；下方混合手牌可直接点击、拖拽出牌。";
            }
            else if (p.Battle == _battle && LocalRequests.Remove(p.Sequence, out var cardId))
            {
                Awaiting.Remove(cardId);
                if (NativeRequests.Remove(p.Sequence, out var original) && !p.Accepted) original.Cancel();
                Status = p.Text;
                Diagnostics.Record("manual-host-reply",LocalContext.GetMe(_combat),"accepted="+p.Accepted+";"+p.Text);
                Log.Info($"Result seq={p.Sequence} accepted={p.Accepted}: {p.Text}");
            }
            return;
        }
        if (net.Type != NetGameType.Host || !_combat.Players.Any(x => x.NetId == sender)) return;
        if (p.Kind == PacketKind.Hello)
        {
            ReadyPeers.Add(sender);
            _ready = !_faulted && _combat.Players.All(x => ReadyPeers.Contains(PuppetCompat.ControllerOf(x.NetId)));
            foreach (var controller in _combat.Players.Select(x => PuppetCompat.ControllerOf(x.NetId)).Distinct().Where(ReadyPeers.Contains))
                Reply(net, controller, new Packet { Kind = PacketKind.Session, Battle = _battle, Ready = _ready,
                    Text = _faulted ? "房主入队异常，本场试验面板已停用，请查看日志。" : "" });
            return;
        }
        if (p.Kind != PacketKind.Play) return;
        var failure = Validate(p, sender, out var model, out var target);
        if (failure == null && !BoardDuelCoordinator.CanSubmit(p.Actor)) failure = "请等待当前玩家完成行动。";
        bool accepted = failure == null;
        if (accepted && !BoardDuelCoordinator.TryReserve(p.Actor)) { accepted = false; failure = "请等待当前玩家完成行动。"; }
        if (accepted)
        {
            accepted = Gate.TryReserve(p.Battle, sender, p.Sequence, p.Card, p.Generation, HandEntries.Get(model!), out failure);
            if (!accepted) BoardDuelCoordinator.CancelReservation(p.Actor);
        }
        if (accepted)
        {
            try
            {
                var actor = _combat.Players.Single(x => x.NetId == p.Actor);
                GameAction action = p.Recipient.HasValue ? AllyPlay.Action(actor,p) : new PlayCardAction(actor, MegaCrit.Sts2.Core.Entities.Multiplayer.NetCombatCard.FromModel(model!), model!.Id, p.Target);
                BoardDuelCoordinator.NoteActionEnqueued(action,actor.NetId);
                // Every peer receives the real actor in the normal action stream.
                // Ownership transfer happens inside that ordered action, never on packet receipt.
                Enqueue.Invoke(RunManager.Instance.ActionQueueSynchronizer, new object[] { action, actor.NetId });
                Log.Info($"Accepted sender={sender} actor={actor.NetId} owner={model.Owner.NetId} card={p.Card} target={p.Target} seq={p.Sequence}");
            }
            catch (Exception ex)
            {
                BoardDuelCoordinator.CancelReservation(p.Actor);
                // EnqueueAction sends to peers before local insertion. An exception
                // can occur after transmission; never retry or release this reservation.
                _faulted = true;
                _ready = false;
                accepted = false;
                failure = "入队异常，停止再次尝试并查看日志。";
                Log.Info(ex.ToString());
            }
        }
        Diagnostics.Record("manual-host-result",_combat.Players.FirstOrDefault(x=>x.NetId==p.Actor),"accepted="+accepted+";"+(failure??"queued"));
        Reply(net, sender, new Packet { Kind = PacketKind.Result, Battle = _battle,
            Sequence = p.Sequence, Accepted = accepted,
            Text = accepted ? "房主已将出牌动作入队；最终结算由原版执行。" : failure ?? "请求被拒绝。" });
    }

    private static string? Validate(Packet p, ulong sender, out CardModel? card, out Creature? target)
    {
        card = null; target = null;
        if (_faulted) return "本场共享面板已因入队异常停用。";
        if (!_ready || _combat == null) return "全员尚未就绪。";
        if (p.Battle != _battle) return "旧战斗的请求已拒绝。";
        if (!PuppetCompat.MayAct(sender, p.Actor)) return "发送者没有这个角色的控制权。";
        var actor = _combat.Players.FirstOrDefault(x => x.NetId == p.Actor);
        if (actor?.PlayerCombatState == null || !actor.Creature.IsAlive) return "操作者无法行动。";
        if (!NetCombatCardDb.Instance.TryGetCard(p.Card, out card) || card == null || !Supports(card))
            return "卡牌不属于当前有效战斗。";
        var owner = card.Owner;
        // Private hands keep native payment/effects, but use the same ordered
        // host submission path. Never authorize another hand or Harmony here.
        if(HandSharingMode.Private && (owner != actor || card.Pile != actor.PlayerCombatState.Hand))
            return "手牌不共享模式只能打出自己手中的牌。";
        if (p.Holder != owner.NetId || p.Generation != HandEntries.Get(card)) return "手牌归属或代次已变化。";
        if (SelectionHints.IsLocked(card, actor)) return "队友正在选择这张牌，请等待队友松开或完成选择。";
        if (TeammatePlayLimit.Blocked(card, actor)) return TeammatePlayLimit.Rejection;
        if (SharedRules.Queued.Contains(p.Card)) return "这张牌已在出牌队列中。";
        if (!_combat.Players.Contains(owner) || owner.PlayerCombatState == null || !owner.Creature.IsAlive)
            return "卡牌原主人无法行动。";
        if (p.OwnerTurn != owner.PlayerCombatState.TurnNumber || p.ActorTurn != actor.PlayerCombatState.TurnNumber)
            return "回合已变化，请重新操作。";
        var manager = CombatManager.Instance;
        if (_combat.CurrentSide != CombatSide.Player ||
            RunManager.Instance.ActionQueueSynchronizer.CombatState != MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState.PlayPhase ||
            actor.PlayerCombatState.Phase != PlayerTurnPhase.Play ||
            manager.IsPlayerReadyToEndTurn(actor) ||
            (manager.PlayersTakingExtraTurn.Count > 0 && !manager.PlayersTakingExtraTurn.Contains(actor)))
            return "当前角色不处于可出牌阶段。";
        if (card.Pile != owner.PlayerCombatState.Hand) return "这张牌已不在手中。";
        target = _combat.GetCreature(p.Target);
        if (card.TargetType == TargetType.Self && p.Target != null) return "防御不接受显式目标。";
        if (card.TargetType == TargetType.AnyEnemy && (target == null || !target.IsAlive)) return "请选择有效敌人。";
        var checkedCard = card; var checkedTarget = target;
        if(p.Recipient is ulong recipient) return AllyPlay.Validate(checkedCard,actor,recipient,checkedTarget);
        if (!CardEvaluation.As(checkedCard, actor, () => checkedCard.CanPlay() && checkedCard.IsValidTarget(checkedTarget)))
            return "出牌者资源不足或原版判定此牌当前不可打出。";
        return null;
    }

    private static void Reply(INetGameService net, ulong recipient, Packet packet)
    {
        if (recipient == net.NetId) Incoming.Enqueue((packet, net.NetId, net));
        else if (!SyncMessages.SendToPeer(net, recipient, Descriptor, packet))
            Log.Info($"Response could not be delivered to {recipient}; no retry is performed.");
    }
}









