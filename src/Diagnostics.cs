using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace SharedHandPrototype;
// Explicitly constructed diagnostics only. No save files, platform/user IDs,
// raw console commands, arbitrary mod settings, credentials or general logs.
internal static class Diagnostics
{
    internal const string Confirmation="导出联机诊断包，不收集账号凭据或无关文件。若可以的话，请先将文件给你信任的AI进行初步判断。报告问题时需完整提供所有本局联机玩家的诊断包。";
    private static readonly Queue<object> Events=new();
    private static bool _dialog;
    internal static string Role(Player? player)
    {
        if(player==null)return "none";
        try { int index=player.RunState.Players.ToList().IndexOf(player);return index<0?"unknown":"P"+(index+1); }
        catch {return "unbound";}
    }
    internal static void Record(string kind,Player? player=null,string detail="")
    {
        Events.Enqueue(new{tick=Time.GetTicksMsec(),kind,role=Role(player),detail});
        while(Events.Count>400)Events.Dequeue();
    }
    internal static void Failure(Exception exception)
    {
        Record("exception",detail:exception.GetType().FullName??"Exception");
        GD.PrintErr("[SharedHandPrototype] diagnostics: "+exception.GetType().FullName+"\n"+new System.Diagnostics.StackTrace(exception,false));
    }
    private static object? KnownCoopSettings()
    {
        var type=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("YouJin.Scripts.YouJinSettings")).FirstOrDefault(t=>t!=null);
        if(type==null)return null;
        var flags=new Dictionary<string,object?>();
        foreach(var name in new[]{"GreedyEnabled","GreedyLootEnabled","SharedEnabled","SharedHpEnabled","SharedGoldEnabled","SharedEnergyEnabled","QuestEnabled"})
        {
            var property=type.GetProperty(name,System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Static);
            flags[name]=property?.PropertyType==typeof(bool)?property.GetValue(null):null;
        }
        return flags;
    }
    private static LocString Text(string key,string text)
    {
        LocManager.Instance.GetTable("settings_ui").MergeWith(new(){["SharedHandDiagnostics."+key]=text});
        return new LocString("settings_ui","SharedHandDiagnostics."+key);
    }
    private static async Task<bool> Dialog(LocString body,LocString title,LocString? cancel)
    {
        if(NModalContainer.Instance==null||NModalContainer.Instance.OpenModal!=null)return false;
        var popup=NGenericPopup.Create();if(popup==null)return false;
        NModalContainer.Instance.Add(popup);
        try{return await popup.WaitForConfirmation(body,title,cancel,Text("confirm","确认"));}
        finally
        {
            // NGenericPopup queues itself for deletion, but does not release
            // NModalContainer.OpenModal. Only clear our own popup, never one
            // opened by a different mod.
            if(ReferenceEquals(NModalContainer.Instance?.OpenModal,popup))NModalContainer.Instance!.Clear();
        }
    }
    internal static async Task Prompt()
    {
        if(_dialog)return;
        _dialog=true;
        try
        {
            if(!await Dialog(Text("body",Confirmation),Text("title","导出联机诊断包"),Text("cancel","取消")))return;
            string file=Export(Path.Combine(OS.GetUserDataDir(),"SharedHandDiagnostics"));
            try{Reveal(file);}catch(Exception ex){Failure(ex);}
            await ((SceneTree)Engine.GetMainLoop()).ToSignal((SceneTree)Engine.GetMainLoop(),SceneTree.SignalName.ProcessFrame);
            // A Windows path is data, never a SmartFormat format string.
            var done=Text("done","已导出：\n游戏用户数据目录 / SharedHandDiagnostics\n{file}");done.Add("file",Path.GetFileName(file));
            await Dialog(done,Text("done-title","导出完成"),null);
        }
        catch(Exception ex)
        {
            Failure(ex);
            await Dialog(Text("failed","导出失败："+ex.GetType().Name),Text("title","导出联机诊断包"),null);
        }
        finally{_dialog=false;}
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal static void Reveal(string file)=>OS.ShellOpen(Path.GetDirectoryName(file)!);
    internal static string Export(string directory)
    {
        // Never require LocalContext.GetMe: a puppet/controller can have a
        // transient identity that does not occur in the current combat list.
        var errors=new List<object>();
        T Read<T>(string section,Func<T> getter,T fallback)
        {
            try{return getter();}
            catch(Exception ex)
            {
                errors.Add(new{section,exception=ex.GetType().FullName,
                    methods=new System.Diagnostics.StackTrace(ex,false).GetFrames()?.Take(10).Select(f=>f.GetMethod()?.DeclaringType?.FullName+"."+f.GetMethod()?.Name).ToArray()});
                return fallback;
            }
        }
        var state=Read<CombatState?>("combat-state",()=>CombatManager.Instance.DebugOnlyGetState(),null);
        var players=Read("players",()=>state?.Players.ToArray()??Array.Empty<Player>(),Array.Empty<Player>());
        var local=Read<Player?>("local-role",()=>players.FirstOrDefault(p=>p.NetId==LocalContext.NetId),null);
        string Provider(CardModel c)=>Read("card-provider:"+c.Id.Entry,()=>Role(players.FirstOrDefault(p=>p.NetId==(CommonPiles.Active?CommonDeaths.Source(c):DeckContributors.Provider(c)))),"unknown");
        object Card(CardModel c)=>new{
            model=Read("card-model",()=>c.Id.ToString(),"unknown"),
            combatIndex=Read<uint?>("card-index",()=>NetCombatCardDb.Instance.TryGetCardId(c,out var id)?id:null,null),
            energyCost=Read<int?>("card-energy-cost",()=>c.EnergyCost.GetWithModifiers(CostModifiers.All),null),
            upgrade=Read("card-upgrade",()=>c.CurrentUpgradeLevel,-1),
            holder=Read("card-holder",()=>Role(c.Owner),"unknown"),provider=Provider(c),
            drawIdentity=Read<string?>("card-draw-identity",()=>DrawIdentity.Drawer(c) is ulong id ? Role(players.FirstOrDefault(p=>p.NetId==id)) : null,null),
            localKeywords=Read("local-keywords",()=>c.GetKeywordsWithSources(KeywordSources.Local).Select(k=>k.ToString()).ToArray(),Array.Empty<string>()),
            effectiveKeywords=Read("effective-keywords",()=>c.Keywords.Select(k=>k.ToString()).ToArray(),Array.Empty<string>()),
            removed=Read("card-removed",()=>c.HasBeenRemovedFromState,false)};
        object[] Pile(Player p,PileType type)=>Read(Role(p)+"-"+type,
            ()=>CardPile.Get(type,p)?.Cards.Select(Card).ToArray()??Array.Empty<object>(),Array.Empty<object>());
        object PlayerState(Player p)=>new{
            role=Role(p),character=Read("character",()=>p.Character.Id.ToString(),"unknown"),
            alive=Read("alive",()=>p.Creature.IsAlive,false),hp=Read("hp",()=>p.Creature.CurrentHp,0),
            energy=Read<int?>("energy",()=>p.PlayerCombatState?.Energy,null),
            turn=Read<int?>("turn",()=>p.PlayerCombatState?.TurnNumber,null),
            phase=Read<string?>("phase",()=>p.PlayerCombatState?.Phase.ToString(),null),
            readyToEnd=Read("ready-to-end",()=>CombatManager.Instance.IsPlayerReadyToEndTurn(p),false),
            relics=Read("relics",()=>p.Relics.Select(r=>r.Id.ToString()).ToArray(),Array.Empty<string>()),
            powers=Read("powers",()=>p.Creature.Powers.Select(r=>r.Id.ToString()+":"+r.Amount).ToArray(),Array.Empty<string>()),
            hand=Pile(p,PileType.Hand),draw=Pile(p,PileType.Draw),discard=Pile(p,PileType.Discard),exhaust=Pile(p,PileType.Exhaust)};
        float? Number(float value)=>float.IsFinite(value)?value:null;
        object Point(Vector2 v)=>new{x=Number(v.X),y=Number(v.Y)};
        object[] Views()=>Read("hand-views",()=>{
            var hand=MegaCrit.Sts2.Core.Nodes.Combat.NPlayerHand.Instance;
            if(!GodotObject.IsInstanceValid(hand))return Array.Empty<object>();
            return hand!.CardHolderContainer.GetChildren().OfType<MegaCrit.Sts2.Core.Nodes.Cards.Holders.NHandCardHolder>().Select(h=>Read<object>("view",()=>new{
                model=h.CardModel.Id.ToString(),holder=Role(h.CardModel.Owner),visible=h.Visible,
                position=Point(h.Position),target=Point(h.TargetPosition),angle=Number(h.RotationDegrees),targetAngle=Number(h.TargetAngle),scale=Point(h.Scale),focused=hand.FocusedHolder==h},new{unavailable=true})).ToArray();
        },Array.Empty<object>());
        var snapshot=new Dictionary<string,object?>
        {
            ["format"]=2,["mod"]="SharedHandPrototype",["version"]=Entry.Version,["game"]="0.111.0",["createdUtc"]=DateTime.UtcNow,
            ["local"]=Role(local),["network"]=Read<string?>("network",()=>RunManager.Instance.NetService?.Type.ToString(),null),["ready"]=Entry.Ready,
            ["requested"]=Read<GameplaySettings?>("requested",()=>SharedHandConfig.Requested,null),
            ["active"]=Read<GameplaySettings?>("active",()=>RuleSession.Current,null),
            ["display"]=new{SharedHandConfig.StackBasics,SharedHandConfig.StackSameName,SharedHandConfig.HandLayout,SharedHandConfig.TeammateGlow,SharedHandConfig.OwnCardGlow,SharedHandConfig.SelectionChains},
            ["layout"]=Read<HandDesign?>("layout",()=>LayoutDesign.Current,null),["handViews"]=Views(),
            ["combat"]=state==null?null:new{
                side=Read("combat-side",()=>state.CurrentSide.ToString(),"unknown"),
                actionsDisabled=Read("actions-disabled",()=>CombatManager.Instance.PlayerActionsDisabled,false),
                action=Read<string?>("current-action",()=>RunManager.Instance.ActionExecutor.CurrentlyRunningAction?.GetType().FullName,null),
                players=players.Select(p=>Read<object>("player:"+Role(p),()=>PlayerState(p),new{role=Role(p),unavailable=true})).ToArray()},
            ["assemblies"]=Read("assemblies",()=>AppDomain.CurrentDomain.GetAssemblies().Where(a=>!a.IsDynamic).Select(a=>Read<object>("assembly",()=>new{name=a.GetName().Name,version=a.GetName().Version?.ToString()},new{unavailable=true})).ToArray(),Array.Empty<object>()),
            ["compatibilityScan"]=CompatibilityScan.Snapshot(),
            ["knownCoopSettings"]=Read<object?>("known-coop-settings",KnownCoopSettings,null),
            ["unavailableSections"]=errors
        };
        var options=new JsonSerializerOptions{WriteIndented=true};
        // Serialize before creating the file, and publish only a completely
        // closed ZIP. A read failure in one section remains inside the report.
        string json=JsonSerializer.Serialize(snapshot,options);
        string recent=JsonSerializer.Serialize(Events.ToArray(),options);
        Directory.CreateDirectory(directory);
        string file=Path.Combine(directory,"SharedHand-"+Entry.Version+"-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+Guid.NewGuid().ToString("N").Substring(0,6)+".zip");
        string pending=file+".partial";
        try
        {
            using(var zip=ZipFile.Open(pending,ZipArchiveMode.Create))
            {
                void Write(string name,string content){using var writer=new StreamWriter(zip.CreateEntry(name).Open(),new UTF8Encoding(false));writer.Write(content);}
                Write("说明.txt","共享手牌联机诊断包\n请完整提供所有本局玩家的诊断包。P1/P2/P3/P4按本局玩家顺序对应。\n包含：模组版本、共享规则、卡牌状态、布局与手牌显示位置、最近400条共享及出牌操作；若加载友尽尖塔，仅补充其贪婪、共享与任务玩法开关。某项状态不可用时，记录在 unavailableSections，其他部分仍可使用。\n不包含：Steam账号ID、昵称、账号凭据、原始存档、控制台输入、其余模组配置、系统环境变量或无关文件。\n");
                Write("state.json",json);Write("recent-events.json",recent);
            }
            File.Move(pending,file);
            return file;
        }
        catch{if(File.Exists(pending))File.Delete(pending);throw;}
    }

}
[HarmonyPatch(typeof(CardPile),nameof(CardPile.AddInternal))]
internal static class DiagnosticsPileAdd
{private static void Postfix(CardPile __instance,CardModel card){if(card.Owner!=null)Diagnostics.Record("card-add",card.Owner,__instance.Type+":"+card.Id.Entry);}}
[HarmonyPatch(typeof(CardPile),nameof(CardPile.RemoveInternal))]
internal static class DiagnosticsPileRemove
{private static void Prefix(CardPile __instance,CardModel card){if(card.Owner!=null)Diagnostics.Record("card-remove",card.Owner,__instance.Type+":"+card.Id.Entry);}}
[HarmonyPatch(typeof(CombatManager),nameof(CombatManager.SetReadyToEndTurn))]
internal static class DiagnosticsTurnEnd
{private static void Prefix(Player player,bool canBackOut)=>Diagnostics.Record("ready-end",player,canBackOut?"manual":"automatic");}

