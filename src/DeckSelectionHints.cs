using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Afflictions;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Runs;


namespace SharedHandPrototype;

// Deck IDs survive mirrored copies, unlike combat IDs. This channel only
// displays the vanilla Bound overlay; it never applies an affliction/lock.
internal static class DeckSelectionHints
{
    private sealed class Message
    {
        public int Version{get;set;}=12;
        public string Run{get;set;}="";
        public ulong Actor{get;set;}
        public ulong Sequence{get;set;}
        public string[] Cards{get;set;}=Array.Empty<string>();
    }
    private static readonly ConcurrentQueue<(Message m,ulong sender,INetGameService net)> Inbox=new();
    private static readonly Dictionary<ulong,(Message m,ulong time)> Remote=new();
    private static readonly Dictionary<NCard,Control> Overlays=new();
    private static readonly SyncMessageDescriptor<Message> Descriptor=new(Entry.ModId,"shared_hand_deck_selection_v12",
        m=>JsonSerializer.SerializeToUtf8Bytes(m),bytes=>bytes.Length is >0 and <=16384?JsonSerializer.Deserialize<Message>(bytes)!:throw new FormatException(),
        Receive,LocationTargeted:true,ShouldBuffer:false,Mode:NetTransferMode.Reliable,DispatchLocalOnBroadcast:false,ShouldBroadcast:false);
    private static ulong _lastTick,_lastSent,_sequence;
    private static string _run="",_signature="";
    internal static void Install()=>SyncMessages.Register(Descriptor);
    private static Task Receive(SyncMessageContext<Message> c)
    {Inbox.Enqueue((c.Message,c.SenderNetId,c.NetService));return Task.CompletedTask;}
    private static string Identity(CardModel card)=>SharedDeckSync.Identity.Get(card.DeckVersion??card);
    internal static void Tick()
    {
        ulong now=Time.GetTicksMsec();if(now-_lastTick<90)return;_lastTick=now;
        var run=RunManager.Instance.DebugOnlyGetState();
        if(run==null||!SharedDeckSync.Enabled(run))
        {Remote.Clear();Clear();while(Inbox.TryDequeue(out _)){}return;}
        string key=run.Rng.StringSeed+":"+string.Join(",",run.Players.Select(p=>p.NetId));
        if(_run!=key){_run=key;_signature="";Remote.Clear();Clear();}
        var net=RunManager.Instance.NetService;
        foreach(var gone in Remote.Keys.Where(id=>!SyncMessages.PeerConnected(net,PuppetCompat.ControllerOf(id))).ToArray())Remote.Remove(gone);
        while(Inbox.TryDequeue(out var item))
        {
            var m=item.m;
            if(!ReferenceEquals(net,item.net)||m.Version!=12||m.Run!=key||m.Cards==null||m.Cards.Length>99||
                m.Cards.Any(c=>c==null||c.Length>80)||!run.Players.Any(p=>p.NetId==m.Actor))continue;
            if(net.Type==NetGameType.Host&&PuppetCompat.MayAct(item.sender,m.Actor))Publish(m,now,net);
            else if(net is NetClientGameService client&&item.sender==client.HostNetId)Store(m,now);
        }
        var actor=run.Players.FirstOrDefault(p=>p.NetId==(MegaCrit.Sts2.Core.Context.LocalContext.NetId??net.NetId));
        if(actor==null){Clear();return;}
        var selected=SharedDeckSelection.SelectedCards().Select(Identity).Where(id=>id!="").Distinct().OrderBy(id=>id,StringComparer.Ordinal).ToArray();
        string signature=actor.NetId+":"+string.Join(",",selected);
        if(signature!=_signature||now-_lastSent>500)
        {
            _signature=signature;_lastSent=now;
            var m=new Message{Run=key,Actor=actor.NetId,Sequence=++_sequence,Cards=selected};
            if(net.Type==NetGameType.Host)Publish(m,now,net);
            else if(net.Type==NetGameType.Client)SyncMessages.SendToHost(net,Descriptor,m);
        }
        var wanted=SharedHandConfig.SelectionChains?Remote.Where(p=>p.Key!=actor.NetId&&now-p.Value.time<1800)
            .SelectMany(p=>p.Value.m.Cards).ToHashSet():new HashSet<string>();
        foreach(var pair in Overlays.ToArray())
            if(!GodotObject.IsInstanceValid(pair.Key)||!GodotObject.IsInstanceValid(pair.Value)||pair.Value.IsQueuedForDeletion()||pair.Key.Model==null||!wanted.Contains(Identity(pair.Key.Model))||!pair.Key.IsVisibleInTree())
            {if(GodotObject.IsInstanceValid(pair.Value))pair.Value.QueueFree();Overlays.Remove(pair.Key);}
        foreach(var screen in SharedDeckSelection.Screens())foreach(var card in CardsBelow(screen))
        {
            if(card.Model==null||!wanted.Contains(Identity(card.Model))||Overlays.ContainsKey(card))continue;
            var overlay=ModelDb.Affliction<Bound>().CreateOverlay();overlay.Name="SharedDeckSelectionChain";Ignore(overlay);
            card.OverlayContainer.AddChild(overlay);Overlays[card]=overlay;
        }
    }
    private static void Store(Message m,ulong now)
    {if(Remote.TryGetValue(m.Actor,out var old)&&m.Sequence<=old.m.Sequence)return;Remote[m.Actor]=(m,now);}
    private static void Publish(Message m,ulong now,INetGameService net)
    {
        Store(m,now);
        foreach(var peer in RunManager.Instance.DebugOnlyGetState()!.Players.Select(p=>PuppetCompat.ControllerOf(p.NetId)).Distinct().Where(id=>id!=net.NetId))
            SyncMessages.SendToPeer(net,peer,Descriptor,m);
    }
    private static IEnumerable<NCard> CardsBelow(Node node)
    {foreach(var child in node.GetChildren()){if(child is NCard card)yield return card;else foreach(var c in CardsBelow(child))yield return c;}}
    private static void Ignore(Node node)
    {if(node is Control c)c.MouseFilter=Control.MouseFilterEnum.Ignore;foreach(var child in node.GetChildren())Ignore(child);}
    private static void Clear(){foreach(var overlay in Overlays.Values)if(GodotObject.IsInstanceValid(overlay))overlay.QueueFree();Overlays.Clear();}
}

