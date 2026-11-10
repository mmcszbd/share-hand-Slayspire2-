using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Rooms;


namespace SharedHandPrototype;

// Explicit reliable routing avoids reusing an incoming transport's None mode
// when Ritsu relays a client's YouJin choice to the remaining peers. Wait for
// peer receipt before the local rest option can announce that it is finished.
internal static class FriendlyGreedTransport
{
    private sealed record Message(int Kind,Guid Operation,string Run,bool Relic,ulong Thief,ulong Victim,int Index);
    private sealed record Pending(Message Choice,HashSet<ulong> Waiting);
    private static readonly ConcurrentQueue<(Message message,ulong sender,INetGameService net)> Inbox=new();
    private static readonly Dictionary<Guid,Pending> HostPending=new();
    private static readonly Dictionary<ulong,TaskCompletionSource<bool>> Deliveries=new();
    private static readonly SyncMessageDescriptor<Message> Descriptor=new(Entry.ModId,"friendly_greed_choice_v1",
        m=>JsonSerializer.SerializeToUtf8Bytes(m),bytes=>bytes.Length is >0 and <=4096?JsonSerializer.Deserialize<Message>(bytes)!:throw new FormatException(),
        Receive,LocationTargeted:false,ShouldBuffer:false,Mode:NetTransferMode.Reliable,DispatchLocalOnBroadcast:false,ShouldBroadcast:false);
    private static bool installed;
    private static string lastRun="";
    private static object? lastRoom;
    private static Task Receive(SyncMessageContext<Message> context)
    {Inbox.Enqueue((context.Message,context.SenderNetId,context.NetService));return Task.CompletedTask;}
    internal static void Install(Harmony patcher,Type[] types)
    {
        if(installed)return;
        foreach(string kind in new[]{"Card","Relic"})
        {
            var sync=types.Single(t=>t.FullName=="YouJin.Scripts.Steal"+kind+"Sync");
            var model=types.Single(t=>t.FullName=="YouJin.Scripts.Steal"+kind+"RestSiteModel");
            patcher.Patch(AccessTools.Method(sync,"Broadcast",new[]{typeof(ulong),typeof(ulong),typeof(int)}),
                prefix:new HarmonyMethod(typeof(FriendlyGreedTransport),nameof(Broadcast)));
            patcher.Patch(AccessTools.Method(model,"StealAsync"),postfix:new HarmonyMethod(typeof(FriendlyGreedTransport),nameof(WaitForDelivery)));
        }
        SyncMessages.Register(Descriptor);installed=true;
    }
    private static string Key(IRunState run)=>run.Rng.StringSeed+":"+run.TotalFloor+":"+string.Join(",",run.Players.Select(p=>p.NetId));
    private static void Send(INetGameService net,ulong peer,Message message)
    {if(peer==net.NetId)Inbox.Enqueue((message,peer,net));else SyncMessages.SendToPeer(net,peer,Descriptor,message);}
    private static bool Broadcast(MethodBase __originalMethod,ulong __0,ulong __1,int __2)
    {
        if(FriendlyGreed.Active() is not {} run)return true;
        EnsureRun(run);
        var net=RunManager.Instance.NetService;
        if(!PuppetCompat.MayAct(net.NetId,__0))return false;
        var message=new Message(0,Guid.NewGuid(),Key(run),__originalMethod.DeclaringType!.Name=="StealRelicSync",__0,__1,__2);
        if(net.Type==NetGameType.Host)Send(net,net.NetId,message);else SyncMessages.SendToHost(net,Descriptor,message);
        return false;
    }
    private static void WaitForDelivery(Player self,ref Task<bool> __result)=>__result=Wait(__result,self);
    private static async Task<bool> Wait(Task<bool> original,Player player)
    {
        if(FriendlyGreed.Active() is not {} run)return await original;
        EnsureRun(run);
        if(!Deliveries.TryGetValue(player.NetId,out var complete)||complete.Task.IsCompleted)
            Deliveries[player.NetId]=complete=new();
        bool result=await original;
        if(!result)
        {
            var net=RunManager.Instance.NetService;
            if(PuppetCompat.MayAct(net.NetId,player.NetId))
            {
                // Other peers skip the local UI. Tell them a cancellation is
                // false too, rather than silently consuming their rest option.
                var cancel=new Message(0,Guid.NewGuid(),Key(run),false,player.NetId,run.Players.First(p=>p!=player).NetId,-1);
                if(net.Type==NetGameType.Host)Send(net,net.NetId,cancel);else SyncMessages.SendToHost(net,Descriptor,cancel);
            }
        }
        if(await Task.WhenAny(complete.Task,Task.Delay(10000))!=complete.Task)
            throw new TimeoutException("友尽偷窃选择未被全体玩家确认，未提交火堆操作。");
        return await complete.Task;
    }
    private static void Note(Message message){if(message.Index>=0)FriendlyGreed.NoteChoice(message.Relic,message.Thief,message.Victim,message.Index);}
    internal static void Tick()
    {
        if(!installed)return;
        var run=FriendlyGreed.Active();
        if(run==null){while(Inbox.TryDequeue(out _)){}Clear();lastRun="";lastRoom=null;return;}
        EnsureRun(run);
        var net=RunManager.Instance.NetService;
        for(int i=0;i<64&&Inbox.TryDequeue(out var item);i++)
        {
            var m=item.message;
            if(!ReferenceEquals(net,item.net)||m.Run!=Key(run)||m.Operation==Guid.Empty||m.Index< -1||
                !run.Players.Any(p=>p.NetId==m.Thief)||!run.Players.Any(p=>p.NetId==m.Victim)||m.Thief==m.Victim)continue;
            bool host=net.Type==NetGameType.Host;
            bool fromHost=host?item.sender==net.NetId:net is NetClientGameService client&&item.sender==client.HostNetId;
            if(host&&m.Kind==0&&PuppetCompat.MayAct(item.sender,m.Thief))
            {
                if(HostPending.ContainsKey(m.Operation))continue;
                Note(m);
                var waiting=run.Players.Select(p=>PuppetCompat.ControllerOf(p.NetId)).Where(id=>id!=net.NetId).ToHashSet();
                HostPending[m.Operation]=new(m,waiting);
                foreach(var peer in waiting)Send(net,peer,m with{Kind=1});
                if(waiting.Count==0){HostPending.Remove(m.Operation);Commit(net,run,m);}
            }
            else if(!host&&m.Kind==1&&fromHost)
            {Note(m);SyncMessages.SendToHost(net,Descriptor,m with{Kind=2});}
            else if(host&&m.Kind==2&&HostPending.TryGetValue(m.Operation,out var batch))
            {
                if(!batch.Waiting.Remove(item.sender)||batch.Waiting.Count!=0)continue;
                HostPending.Remove(m.Operation);
                Commit(net,run,batch.Choice);
            }
            else if(m.Kind==3&&fromHost)
            {
                if(!Deliveries.TryGetValue(m.Thief,out var complete))Deliveries[m.Thief]=complete=new();
                complete.TrySetResult(m.Index>=0);
            }
        }
    }
    private static void EnsureRun(IRunState run)
    {string key=Key(run);if(lastRun!=key||!ReferenceEquals(lastRoom,NRestSiteRoom.Instance)){Clear();lastRun=key;lastRoom=NRestSiteRoom.Instance;}}
    private static void Commit(INetGameService net,IRunState run,Message message)
    {foreach(var peer in run.Players.Select(p=>PuppetCompat.ControllerOf(p.NetId)).Distinct())Send(net,peer,message with{Kind=3});}
    private static void Clear()
    {
        foreach(var pending in Deliveries.Values)pending.TrySetCanceled();
        HostPending.Clear();Deliveries.Clear();
    }
}

