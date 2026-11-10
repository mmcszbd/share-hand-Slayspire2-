using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace SharedHandPrototype;

// This coordinator governs manual actions only. Automatic card effects stay
// inside their parent action, and the host alone advances the visit timer.
internal static class BoardDuelCoordinator
{
    private sealed class Message
    {
        public Guid Battle { get; set; }
        public int Turn { get; set; }
        public long Revision { get; set; }
        public ulong Actor { get; set; }
        public int Played { get; set; }
        public int Limit { get; set; }
        public int Seconds { get; set; }
    }
    private static readonly ConcurrentQueue<(Message message,ulong sender,INetGameService net)> Inbox=new();
    private static readonly SyncMessageDescriptor<Message> Descriptor=new(Entry.ModId,"board_duel_visit_v1",
        m=>JsonSerializer.SerializeToUtf8Bytes(m),
        bytes=>bytes.Length is >0 and <=2048 ? JsonSerializer.Deserialize<Message>(bytes) ?? throw new FormatException() : throw new FormatException(),
        c=>{Inbox.Enqueue((c.Message,c.SenderNetId,c.NetService));return Task.CompletedTask;},
        LocationTargeted:true,ShouldBuffer:false,Mode:NetTransferMode.Reliable);
    private static bool _installed, _begun, _finished, _reserved, _commitPending;
    private static CombatState? _combat;
    private static Guid _battle;
    private static int _turn=-1, _cards, _seconds, _rpsSeconds;
    private static ulong _actor, _deadline, _reservedActor, _uiDeadline, _rpsUiDeadline;
    private static long _revision, _seenRevision;
    private static int _played;
    private static BoardDuelOrder? _order;

    internal static bool IsYouJinPvp()
    {
        try
        {
            var duel=AccessTools.TypeByName("PvpDuel.Core.Duel.DuelSession");
            var speed=AccessTools.TypeByName("YouJin.Scripts.ArchitectBetrayalEvent");
            return (duel!=null&&AccessTools.Property(duel,"InDuel")?.GetValue(null) is true)||
                   (speed!=null&&AccessTools.Property(speed,"BetrayalCombatActive")?.GetValue(null) is true);
        }
        catch{return false;}
    }
    internal static bool InCombat => RuleSession.Ready && RuleSession.Current.BoardDuel &&
        IsYouJinPvp();

    internal static void Install()
    {
        if(_installed)return;
        BoardDuelRpsNetwork.Install();SyncMessages.Register(Descriptor);_installed=true;
    }

    // Must be called after Entry has reset/received its combat session ID.
    internal static void Tick(bool enabled,int cards,int seconds,int rpsSeconds)
    {
        var manager=CombatManager.Instance;
        var state=manager?.IsInProgress==true?manager.DebugOnlyGetState():null;
        var net=RunManager.Instance?.NetService;
        if(!enabled || state==null || net==null || !net.IsConnected ||
           !Entry.Ready || Entry.Battle==Guid.Empty || manager!.IsOverOrEnding)
        { Reset(); return; }
        int turn=state.RoundNumber;
        if(!ReferenceEquals(state,_combat) || _battle!=Entry.Battle || _turn!=turn)
        {
            Reset();_combat=state;_battle=Entry.Battle;_turn=turn;
            _cards=Math.Clamp(cards,1,10);_seconds=Math.Clamp(seconds,1,60);_rpsSeconds=Math.Clamp(rpsSeconds,1,60);
        }
        // Await every active player's opening draw/relic/selection and queue
        // completion. A later change of phase cannot start an old tournament.
        if(!_begun && state.CurrentSide==CombatSide.Player &&
           state.Players.Where(p=>p.Creature.IsAlive).All(p=>p.PlayerCombatState?.Phase==PlayerTurnPhase.Play) &&
           state.Players.Count(p=>p.Creature.IsAlive)>=2 && !RunManager.Instance.ActionExecutor.IsRunning)
        {
            _begun=true; var battle=_battle;int round=_turn;
            BoardDuelRpsNetwork.Begin(net,battle,round,state.Players.Where(p=>p.Creature.IsAlive).Select(p=>p.NetId).ToArray(),_rpsSeconds,
                (_,duration,choose)=>{_rpsUiDeadline=Time.GetTicksMsec()+(ulong)duration*1000;
                    BoardDuelRpsUi.Open(duration,move=>choose((BoardDuelGesture)move));},
                BoardDuelRpsUi.Close,
                order=>{
                    if(_battle!=battle||_turn!=round)return;
                    _finished=true;
                    if(net.Type==NetGameType.Host){_order=new BoardDuelOrder(order,_cards);Publish(net);}
                });
        }
        if(_begun&&!_finished)
        {
            BoardDuelRpsNetwork.Tick();
            if(_rpsUiDeadline!=0)BoardDuelRpsUi.UpdateRemaining(Math.Max(0,(long)_rpsUiDeadline-(long)Time.GetTicksMsec())/1000f);
        }
        while(Inbox.TryDequeue(out var item))
        {
            var m=item.message;
            bool fromHost=net.Type==NetGameType.Host?item.sender==net.NetId:net is NetClientGameService c&&item.sender==c.HostNetId;
            if(!ReferenceEquals(net,item.net)||!fromHost||m.Battle!=_battle||m.Turn!=_turn||m.Revision<=_seenRevision||
               m.Limit!=_cards||m.Seconds is <1 or >60||m.Played<0||m.Played>=_cards||
               (m.Actor!=0&&!state.Players.Any(p=>p.NetId==m.Actor)))continue;
            _seenRevision=m.Revision;_actor=m.Actor;_played=m.Played;_uiDeadline=Time.GetTicksMsec()+(ulong)m.Seconds*1000;
        }
        if(net.Type==NetGameType.Host&&_finished&&_order!=null)
        {
            if(_commitPending&&!RunManager.Instance.ActionExecutor.IsRunning)
            {
                _commitPending=false;_reserved=false;
                _order.CommitManualPlay(_reservedActor);Publish(net);
            }
            var prior=_order.Current;
            foreach(var p in state.Players.Where(p=>p.Creature.IsDead||manager.IsPlayerReadyToEndTurn(p)).ToArray())_order.Remove(p.NetId);
            if(prior!=_order.Current){_reserved=false;Publish(net);}
            if(!_reserved&&_order.Current is ulong current&&Time.GetTicksMsec()>=_deadline)
            {_order.Timeout(current);Publish(net);}
        }
        if(_finished&&_actor!=0)
        {
            var p=state.Players.FirstOrDefault(x=>x.NetId==_actor);
            BoardDuelHud.Update(p?.Character?.Title?.GetFormattedText()??"玩家",_played,_cards,
                Math.Max(0,(long)_uiDeadline-(long)Time.GetTicksMsec())/1000f,LocalContext.GetMe(state)?.NetId==_actor);
        }
        else if(_finished)BoardDuelHud.Hide();
    }

    private static void Publish(INetGameService net)
    {
        if(_combat==null||_order==null)return;
        _deadline=Time.GetTicksMsec()+(ulong)_seconds*1000;
        var m=new Message{Battle=_battle,Turn=_turn,Revision=++_revision,Actor=_order.Current??0,Played=_order.Played,Limit=_cards,Seconds=_seconds};
        foreach(var p in _combat.Players)
        {
            if(p.NetId==net.NetId)Inbox.Enqueue((m,net.NetId,net));
            else SyncMessages.SendToPeer(net,p.NetId,Descriptor,m);
        }
    }

    internal static bool CanSubmit(ulong actor) => !InCombat ||
        (_combat!=null && _combat.CurrentSide==CombatSide.Player && _combat.RoundNumber==_turn && _finished && _actor==actor);

    // Called only by the host after actor and request validation, before
    // enqueue. A pending action suspends the timer and blocks double submits.
    internal static bool TryReserve(ulong actor)
    {
        if(!InCombat)return true;
        if(_combat==null || _combat.CurrentSide!=CombatSide.Player || _combat.RoundNumber!=_turn)return false;
        if(!_finished || _reserved || _order?.Current!=actor)return false;
        _reserved=true;_reservedActor=actor;return true;
    }
    internal static void CancelReservation(ulong actor)
    {if(_reserved&&_reservedActor==actor){_reserved=false;_commitPending=false;_deadline=Time.GetTicksMsec()+(ulong)_seconds*1000;}}
    internal static void NoteActionEnqueued(GameAction action,ulong actor)
    {
        if(!_reserved || _reservedActor!=actor || _combat==null)return;
        var battle=_battle;int turn=_turn;bool done=false;
        void Finished(GameAction completed)
        {
            if(done)return;done=true;
            if(_battle!=battle||_turn!=turn||!_reserved||_reservedActor!=actor)return;
            var net=RunManager.Instance?.NetService;
            if(net?.Type!=NetGameType.Host||_order==null)return;
            if(completed.Exception==null)_commitPending=true;
            else { _reserved=false;Publish(net); }
        }
        void Cancelled(GameAction _)
        {
            if(done)return;done=true;
            if(_battle!=battle||_turn!=turn||!_reserved||_reservedActor!=actor)return;
            _reserved=false;_commitPending=false;
            var net=RunManager.Instance?.NetService;
            if(net?.Type==NetGameType.Host)Publish(net);
        }
        action.AfterFinished+=Finished;action.BeforeCancelled+=Cancelled;
    }

    internal static void Reset()
    {
        if(_begun)BoardDuelRpsNetwork.Cancel();
        BoardDuelHud.Hide();_combat=null;_battle=Guid.Empty;_turn=-1;_begun=false;_finished=false;
        _reserved=false;_commitPending=false;_order=null;_actor=0;_played=0;_revision=0;_seenRevision=0;_rpsUiDeadline=0;
        while(Inbox.TryDequeue(out _)){}
    }
}
