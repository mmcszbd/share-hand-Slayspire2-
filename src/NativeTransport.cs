using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace SharedHandPrototype;

internal sealed record SyncMessageContext<T>(T Message,ulong SenderNetId,INetGameService NetService);
internal sealed record SyncMessageDescriptor<T>(string ModuleId,string MessageKey,Func<T,byte[]> Serialize,
    Func<ReadOnlySpan<byte>,T> Deserialize,Func<SyncMessageContext<T>,Task> Handle,bool LocationTargeted=false,
    bool ShouldBuffer=true,NetTransferMode Mode=NetTransferMode.Reliable,bool DispatchLocalOnBroadcast=false,bool ShouldBroadcast=false);

// One native message subtype, discovered by vanilla MessageTypes.Initialize.
// All logical channels retain their own validation in their original handlers.
public sealed class SharedHandWireMessage : INetMessage,IPacketSerializable
{
    public string Channel="";
    public byte[] Payload=Array.Empty<byte>();
    public bool Buffered,Targeted;
    public RunLocation Location;
    public bool ShouldBroadcast=>false;
    public bool ShouldBuffer=>Buffered;
    public NetTransferMode Mode=>NetTransferMode.Reliable;
    public LogLevel LogLevel=>LogLevel.Debug;
    public void Serialize(PacketWriter writer)
    {
        if(Channel.Length>100||Payload.Length>262144)throw new FormatException("SharedHand message size");
        writer.WriteInt(18);writer.WriteString(Channel);writer.WriteBool(Buffered);writer.WriteBool(Targeted);
        if(Targeted)Location.Serialize(writer);
        writer.WriteInt(Payload.Length);writer.WriteBytes(Payload,Payload.Length);
    }
    public void Deserialize(PacketReader reader)
    {
        if(reader.ReadInt()!=18)throw new FormatException("SharedHand wire version");
        Channel=reader.ReadString();Buffered=reader.ReadBool();Targeted=reader.ReadBool();
        if(Channel.Length>100)throw new FormatException("SharedHand channel size");
        if(Targeted)Location.Deserialize(reader);
        int count=reader.ReadInt();if(count is <0 or >262144)throw new FormatException("SharedHand payload size");
        Payload=new byte[count];reader.ReadBytes(Payload,count);
    }
}

internal static class SyncMessages
{
    private sealed class Bound{}
    private sealed record Pending(INetGameService Net,ulong Sender,SharedHandWireMessage Message,ulong Time);
    private static readonly ConditionalWeakTable<INetGameService,Bound> BoundServices=new();
    private static readonly Dictionary<string,Action<SharedHandWireMessage,ulong,INetGameService>> Channels=new(StringComparer.Ordinal);
    private static readonly ConcurrentQueue<Pending> Inbox=new();
    private static readonly List<Pending> Waiting=new();
    internal static void Register<T>(SyncMessageDescriptor<T> descriptor)
    {
        string key=descriptor.ModuleId+"/"+descriptor.MessageKey;
        if(!Channels.TryAdd(key,(wire,sender,net)=>
        {
            var task=descriptor.Handle(new(descriptor.Deserialize(wire.Payload),sender,net));
            if(!task.IsCompletedSuccessfully)MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(task);
        }))throw new InvalidOperationException("Duplicate SharedHand channel "+key);
    }
    internal static void Bind(INetGameService net)
    {
        if(BoundServices.TryGetValue(net,out _))return;
        net.RegisterMessageHandler<SharedHandWireMessage>((wire,sender)=>Inbox.Enqueue(new(net,sender,wire,Time.GetTicksMsec())));
        BoundServices.Add(net,new());
    }
    internal static void Tick()
    {
        var manager=RunManager.Instance;var net=manager.NetService;
        if(net!=null)Bind(net);
        while(Inbox.TryDequeue(out var p))Waiting.Add(p);
        var location=manager.RunLocationTargetedBuffer?.CurrentLocation;
        for(int i=0;i<Waiting.Count;)
        {
            var p=Waiting[i];
            if(!ReferenceEquals(p.Net,net)||Time.GetTicksMsec()-p.Time>15000)
            {Waiting.RemoveAt(i);continue;}
            if(p.Message.Targeted)
            {
                if(location==null||p.Message.Location.CompareTo(location.Value)>0){i++;continue;}
                if(p.Message.Location.CompareTo(location.Value)<0){Waiting.RemoveAt(i);continue;}
            }
            Waiting.RemoveAt(i);
            if(Channels.TryGetValue(p.Message.Channel,out var receive))receive(p.Message,p.Sender,p.Net);
        }
    }
    private static SharedHandWireMessage Make<T>(SyncMessageDescriptor<T> descriptor,T message)=>new()
    {
        Channel=descriptor.ModuleId+"/"+descriptor.MessageKey,Payload=descriptor.Serialize(message),
        Buffered=descriptor.ShouldBuffer,Targeted=descriptor.LocationTargeted,
        Location=RunManager.Instance.RunLocationTargetedBuffer?.CurrentLocation??default
    };
    internal static bool SendToHost<T>(RunManager manager,SyncMessageDescriptor<T> descriptor,T message)=>SendToHost(manager.NetService,descriptor,message);
    internal static bool SendToHost<T>(INetGameService net,SyncMessageDescriptor<T> descriptor,T message)
    {
        Bind(net);if(!net.IsConnected)return false;
        var wire=Make(descriptor,message);
        if(net.Type!=NetGameType.Client)Inbox.Enqueue(new(net,net.NetId,wire,Time.GetTicksMsec()));
        else net.SendMessage(wire);
        return true;
    }
    internal static bool SendToPeer<T>(INetGameService net,ulong peer,SyncMessageDescriptor<T> descriptor,T message)
    {
        Bind(net);
        var wire=Make(descriptor,message);
        if(peer==net.NetId){Inbox.Enqueue(new(net,peer,wire,Time.GetTicksMsec()));return true;}
        // A session-level IsConnected check is not enough: after a peer leaves
        // (or is dropped by state divergence) the host still reports connected
        // while SteamHost.SendMessageToClient throws "Could not find connection
        // for peer". Every per-frame channel publishes through here, so the
        // check must be per peer or the exception repeats forever.
        if(!PeerConnected(net,peer))return false;
        net.SendMessage(wire,peer);
        return true;
    }
    // True only when this specific peer can still receive a targeted message.
    internal static bool PeerConnected(INetGameService net,ulong peer)
    {
        if(net==null||!net.IsConnected)return false;
        if(peer==net.NetId)return true;
        if(net is NetHostGameService host)return host.ConnectedPeers.Any(p=>p.peerId==peer);
        if(net is NetClientGameService client)return peer==client.HostNetId;
        return false;
    }
}

[HarmonyPatch]
internal static class SharedNativeTransportBindPatch
{
    private static IEnumerable<MethodBase> TargetMethods()=>new[]{typeof(NetHostGameService),typeof(NetClientGameService),typeof(NetSingleplayerGameService)}
        .SelectMany(t=>t.GetConstructors(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic));
    private static void Postfix(INetGameService __instance)=>SyncMessages.Bind(__instance);
}
