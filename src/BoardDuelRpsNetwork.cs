using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;

namespace SharedHandPrototype;

// One host-authoritative tournament per combat turn. Call Install at mod init,
// Begin on all peers before starting the turn, Tick once per frame and Cancel
// on combat exit. UI callbacks are executed on the game thread by Tick.
internal static class BoardDuelRpsNetwork
{
    private sealed class Message
    {
        public int Version { get; set; } = 1;
        public int Kind { get; set; } // 1 start round, 2 pick, 3 final order
        public Guid Battle { get; set; }
        public Guid Operation { get; set; }
        public int Turn { get; set; }
        public int Round { get; set; }
        public int Seconds { get; set; }
        public ulong[] Players { get; set; } = Array.Empty<ulong>();
        public int Gesture { get; set; }
    }
    private static readonly ConcurrentQueue<(Message msg, ulong sender, INetGameService net)> Inbox = new();
    private static readonly SyncMessageDescriptor<Message> Descriptor = new(Entry.ModId,"board_duel_rps_v1",
        m=>JsonSerializer.SerializeToUtf8Bytes(m),
        bytes=>bytes.Length is >0 and <=4096 ? JsonSerializer.Deserialize<Message>(bytes) ?? throw new FormatException() : throw new FormatException(),
        c=>{Inbox.Enqueue((c.Message,c.SenderNetId,c.NetService));return Task.CompletedTask;},
        LocationTargeted:true,ShouldBuffer:false,Mode:NetTransferMode.Reliable);
    private static bool _installed;
    private static INetGameService? _net;
    private static Guid _battle, _operation;
    private static int _turn, _seconds, _round;
    private static ulong _deadline;
    private static ulong[] _players=Array.Empty<ulong>(), _active=Array.Empty<ulong>();
    private static readonly List<List<ulong>> Groups=new();
    private static readonly Dictionary<ulong,BoardDuelGesture> Picks=new();
    private static readonly HashSet<ulong> Ready=new();
    private static ulong _lastReady;
    private static Action<IReadOnlyList<ulong>,int,Action<BoardDuelGesture>>? _open;
    private static Action? _close;
    private static Action<IReadOnlyList<ulong>>? _complete;
    private static bool _picked, _finished;
    internal static float RemainingSeconds => _deadline==0 ? 0 : (float)Math.Max(0,((double)_deadline-Time.GetTicksMsec())/1000.0);

    internal static void Install()
    {
        if (_installed) return;
        SyncMessages.Register(Descriptor); _installed=true;
    }

    internal static void Begin(INetGameService net, Guid battle, int turn,
        IReadOnlyList<ulong> players, int seconds,
        Action<IReadOnlyList<ulong>,int,Action<BoardDuelGesture>> open,
        Action close, Action<IReadOnlyList<ulong>> complete)
    {
        if (!_installed) throw new InvalidOperationException("Install board duel RPS before use.");
        if (net == null || !net.IsConnected || battle == Guid.Empty || turn < 0 || players == null ||
            players.Count is <2 or >12 || players.Distinct().Count()!=players.Count || seconds is <1 or >60)
            throw new ArgumentException("Invalid board duel RPS context.");
        Cancel();
        _net=net; _battle=battle; _turn=turn; _seconds=seconds; _players=players.ToArray();
        _open=open ?? throw new ArgumentNullException(nameof(open));
        _close=close ?? throw new ArgumentNullException(nameof(close));
        _complete=complete ?? throw new ArgumentNullException(nameof(complete));
        if (net.Type==NetGameType.Host) { _operation=Guid.NewGuid(); Groups.Add(_players.ToList()); Ready.Add(net.NetId); }
    }

    private static Message Base(int kind) => new(){Kind=kind,Battle=_battle,Operation=_operation,Turn=_turn,Round=_round,Seconds=_seconds};
    private static void Send(ulong peer,Message m)
    {
        if (_net == null) return;
        if(peer==_net.NetId) Inbox.Enqueue((m,peer,_net));
        else SyncMessages.SendToPeer(_net,peer,Descriptor,m);
    }
    private static void Broadcast(Message m) { foreach (var id in _players) Send(id,m); }

    private static void StartNextRound()
    {
        if (_net == null || _net.Type!=NetGameType.Host) return;
        int i=Groups.FindIndex(g=>g.Count>1);
        if(i<0)
        {
            var result=Base(3); result.Players=Groups.Select(g=>g[0]).ToArray(); Broadcast(result); return;
        }
        _active=Groups[i].ToArray(); Picks.Clear(); _picked=false; _round++;
        _deadline=Time.GetTicksMsec()+(ulong)_seconds*1000;
        var start=Base(1);start.Players=_active.ToArray();Broadcast(start);
    }

    private static bool HostMessage(INetGameService net,ulong sender) =>
        net.Type==NetGameType.Host ? sender==net.NetId : net is NetClientGameService client && sender==client.HostNetId;

    internal static void Tick()
    {
        while(Inbox.TryDequeue(out var item))
        {
            var m=item.msg; var net=_net;
            if(net==null || !ReferenceEquals(net,item.net) || _finished || m.Version!=1 ||
                m.Battle!=_battle || m.Turn!=_turn || m.Players==null || m.Players.Length>12) continue;
            if(m.Kind==4 && net.Type==NetGameType.Host && _players.Contains(item.sender) && m.Seconds==_seconds)
            { Ready.Add(item.sender); continue; }
            if(m.Operation==Guid.Empty) continue;
            if(m.Kind==1 && HostMessage(net,item.sender))
            {
                if(_operation!=Guid.Empty && m.Operation!=_operation) continue;
                if(m.Round<=0 || m.Round<(_round+(net.Type==NetGameType.Host?0:1)) ||
                    m.Seconds!=_seconds || m.Players.Length<2 || m.Players.Distinct().Count()!=m.Players.Length ||
                    m.Players.Any(id=>!_players.Contains(id))) continue;
                _operation=m.Operation;_round=m.Round;_active=m.Players.ToArray();_picked=false;
                if(net.Type!=NetGameType.Host) _deadline=Time.GetTicksMsec()+(ulong)_seconds*1000;
                _close?.Invoke();
                if(_active.Contains(net.NetId))
                {
                    Guid op=_operation;int round=_round;
                    _open?.Invoke(_active,_seconds,gesture=>Submit(op,round,gesture));
                }
            }
            else if(m.Kind==2 && net.Type==NetGameType.Host && m.Operation==_operation && m.Round==_round &&
                    _active.Contains(item.sender) && m.Gesture is >=0 and <=2 && !Picks.ContainsKey(item.sender))
                Picks[item.sender]=(BoardDuelGesture)m.Gesture;
            else if(m.Kind==3 && HostMessage(net,item.sender) && m.Operation==_operation && m.Round==_round &&
                    m.Players.Length==_players.Length && m.Players.Distinct().Count()==_players.Length &&
                    m.Players.All(id=>_players.Contains(id)))
            {
                _finished=true;_close?.Invoke();_complete?.Invoke(Array.AsReadOnly(m.Players.ToArray()));
            }
        }
        if(_net==null || _finished) return;
        if(_net.Type==NetGameType.Client && _round==0 && Time.GetTicksMsec()-_lastReady>=1000)
        { _lastReady=Time.GetTicksMsec(); SyncMessages.SendToHost(_net,Descriptor,Base(4)); }
        if(_net.Type!=NetGameType.Host) return;
        if(_round==0 && Ready.Count==_players.Length) StartNextRound();
        if(_active.Length==0) return;
        if(Picks.Count<_active.Length && Time.GetTicksMsec()<_deadline) return;
        foreach(var id in _active) if(!Picks.ContainsKey(id)) Picks[id]=(BoardDuelGesture)System.Security.Cryptography.RandomNumberGenerator.GetInt32(3);
        var result=BoardDuelRps.Resolve(_active,Picks);
        if(result.IsTie) { StartNextRound(); return; }
        int index=Groups.FindIndex(g=>g.SequenceEqual(_active));
        if(index<0) { Cancel(); return; }
        Groups.RemoveAt(index);Groups.Insert(index,result.Eliminated.ToList());Groups.Insert(index,result.Survivors.ToList());
        _active=Array.Empty<ulong>();StartNextRound();
    }

    private static void Submit(Guid operation,int round,BoardDuelGesture gesture)
    {
        if(_net==null || _finished || _picked || operation!=_operation || round!=_round ||
            !_active.Contains(_net.NetId) || (byte)gesture>2) return;
        _picked=true;var m=Base(2);m.Gesture=(int)gesture;
        if(_net.Type==NetGameType.Host) Inbox.Enqueue((m,_net.NetId,_net));
        else SyncMessages.SendToHost(_net,Descriptor,m);
    }

    internal static void Cancel()
    {
        _close?.Invoke();_net=null;_battle=Guid.Empty;_operation=Guid.Empty;_turn=0;_round=0;
        _deadline=0;_lastReady=0;_players=Array.Empty<ulong>();_active=Array.Empty<ulong>();Groups.Clear();Picks.Clear();Ready.Clear();
        _open=null;_close=null;_complete=null;_picked=false;_finished=false;
        while(Inbox.TryDequeue(out _)){}
    }
}
