using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace SharedHandPrototype;

// Private, typed state storage. Preserve the legacy field names for card IDs.
internal sealed class PersistentValue<TKey,TValue> where TKey:class
{
    private sealed class Value{internal TValue Data=default!;}
    private readonly ConditionalWeakTable<TKey,Value> _values=new();
    private readonly Func<TValue> _factory;
    internal string Name{get;}
    internal PersistentValue(Func<TValue> factory,string name){_factory=factory;Name=typeof(TKey).Name+"_"+name;}
    public TValue Get(TKey key)=>_values.GetValue(key,_=>new(){Data=_factory()}).Data;
    public void Set(TKey key,TValue value)=>_values.GetValue(key,_=>new()).Data=value;
}

internal static class NativeSavedState
{
    internal sealed class Document
    {
        public int Version{get;set;}=1;
        public string Rules{get;set;}="";
        public bool Adjusted{get;set;}
        public Dictionary<ulong,int> Counters{get;set;}=new();
    }
    private sealed class Text{internal string Value="";}
    private static readonly ConditionalWeakTable<SerializableRun,Text> Documents=new();
    internal static readonly PersistentValue<Player,int> Counter=new(()=>0,"SharedHand_v6_Counter");
    internal static string Read(SerializableRun save)=>Documents.TryGetValue(save,out var value)?value.Value:"";
    internal static void Write(SerializableRun save,string value)
    {if(value.Length>65536)throw new FormatException("SharedHand save document size");Documents.GetOrCreateValue(save).Value=value;}
    internal static void Export(IRunState run,SerializableRun save)
    {
        if(run.Players.Count is not (>=2 and <=PartyLimits.MaxPlayers))return;
        Write(save,JsonSerializer.Serialize(new Document{Rules=RuleSession.Saved.Get(run),Adjusted=StartingDeckRules.Adjusted.Get(run),
            Counters=run.Players.ToDictionary(p=>p.NetId,p=>Counter.Get(p))}));
    }
    internal static void Import(SerializableRun save,RunState run)
    {
        var text=Read(save);if(string.IsNullOrEmpty(text))return;
        var document=JsonSerializer.Deserialize<Document>(text)??throw new FormatException("SharedHand save document");
        if(document.Version!=1||document.Counters.Count>PartyLimits.MaxPlayers||document.Rules.Length>8192)throw new FormatException("SharedHand save schema");
        RuleSession.Saved.Set(run,document.Rules);StartingDeckRules.Adjusted.Set(run,document.Adjusted);
        foreach(var p in run.Players)Counter.Set(p,Math.Max(0,document.Counters.GetValueOrDefault(p.NetId)));
    }
    // Read old BaseLib dictionaries only from the exact native run save being
    // loaded. No profile scanning, inferred resource values or unrelated files.
    internal static void ImportLegacy(RunSaveManager manager,ReadSaveResult<SerializableRun> result,string property)
    {
        if(!result.Success||result.SaveData==null||!string.IsNullOrEmpty(Read(result.SaveData)))return;
        var store=AccessTools.Field(typeof(RunSaveManager),"_saveStore").GetValue(manager)!;
        var path=(string)AccessTools.Property(typeof(RunSaveManager),property).GetValue(manager)!;
        var text=(string?)AccessTools.Method(store.GetType(),"ReadFile",new[]{typeof(string)}).Invoke(store,new object[]{path});
        if(string.IsNullOrEmpty(text))return;
        using var json=JsonDocument.Parse(text);var root=json.RootElement;
        var data=new Document();
        if(root.TryGetProperty("save_dict_String",out var strings)&&strings.TryGetProperty("spirefield_IRunState_SharedHand_v6_Rules",out var rules))data.Rules=rules.GetString()??"";
        if(root.TryGetProperty("save_dict_Boolean",out var booleans)&&booleans.TryGetProperty("spirefield_IRunState_SharedHand_v6_StartingDeckAdjusted",out var adjusted))data.Adjusted=adjusted.GetBoolean();
        foreach(var player in root.GetProperty("players").EnumerateArray())
            if(player.TryGetProperty("save_dict_Int32",out var ints)&&ints.TryGetProperty("spirefield_Player_SharedHand_v6_Counter",out var counter))
                data.Counters[player.GetProperty("net_id").GetUInt64()]=counter.GetInt32();
        if(data.Rules.Length>0)Write(result.SaveData,JsonSerializer.Serialize(data));
    }
}

[HarmonyPatch(typeof(RunManager),nameof(RunManager.ToSave),new[]{typeof(AbstractRoom)})]
internal static class SharedNativeSaveExport
{private static void Postfix(RunManager __instance,SerializableRun __result){if(__instance.DebugOnlyGetState() is {} run)NativeSavedState.Export(run,__result);}}
[HarmonyPatch(typeof(RunState),nameof(RunState.FromSerializable))]
internal static class SharedNativeSaveImport
{private static void Postfix(SerializableRun save,RunState __result)=>NativeSavedState.Import(save,__result);}

[HarmonyPatch]
internal static class SharedNativeSaveJsonMetadata
{
    private static MethodBase TargetMethod()=>AccessTools.Method(typeof(CardModel).Assembly.GetType("MegaCrit.Sts2.Core.Saves.MegaCritSerializerContext")!,"Create_SerializableRun");
    private static void Postfix(JsonTypeInfo<SerializableRun> __result)
    {
        if(__result.Properties.Any(p=>p.Name=="shared_hand_state"))return;
        var property=__result.CreateJsonPropertyInfo(typeof(string),"shared_hand_state");
        property.Get=obj=>NativeSavedState.Read((SerializableRun)obj);
        property.Set=(obj,value)=>NativeSavedState.Write((SerializableRun)obj,(string?)value??"");
        property.ShouldSerialize=(_,value)=>value is string text&&text.Length>0;
        __result.Properties.Add(property);
    }
}
[HarmonyPatch(typeof(SerializableRun),nameof(SerializableRun.Serialize))]
internal static class SharedNativeSaveWrite
{private static void Postfix(SerializableRun __instance,PacketWriter writer){if(__instance.Players.Count>=2)writer.WriteString(NativeSavedState.Read(__instance));}}
[HarmonyPatch(typeof(SerializableRun),nameof(SerializableRun.Deserialize))]
internal static class SharedNativeSaveRead
{private static void Postfix(SerializableRun __instance,PacketReader reader){if(__instance.Players.Count>=2)NativeSavedState.Write(__instance,reader.ReadString());}}
[HarmonyPatch(typeof(RunManager),nameof(RunManager.CanonicalizeSave))]
internal static class SharedNativeSaveCanonicalize
{private static void Postfix(SerializableRun save,SerializableRun __result){if(string.IsNullOrEmpty(NativeSavedState.Read(__result)))NativeSavedState.Write(__result,NativeSavedState.Read(save));}}
[HarmonyPatch(typeof(RunSaveManager),nameof(RunSaveManager.LoadRunSave))]
internal static class SharedNativeLegacySingleLoad
{private static void Postfix(RunSaveManager __instance,ReadSaveResult<SerializableRun> __result){if(__result.SaveData?.Players.Count>=2)NativeSavedState.ImportLegacy(__instance,__result,"CurrentRunSavePath");}}
[HarmonyPatch(typeof(RunSaveManager),nameof(RunSaveManager.LoadMultiplayerRunSave))]
internal static class SharedNativeLegacyMultiLoad
{private static void Postfix(RunSaveManager __instance,ReadSaveResult<SerializableRun> __result)=>NativeSavedState.ImportLegacy(__instance,__result,"CurrentMultiplayerRunSavePath");}

[HarmonyPatch(typeof(SavedProperties),"FromInternal")]
internal static class SharedNativeCardIdentityExport
{
    private static void Postfix(object model,ref SavedProperties? __result)
    {
        if(model is not CardModel card||string.IsNullOrEmpty(SharedDeckSync.Identity.Get(card)))return;
        __result??=new();__result.strings??=new();
        __result.strings.RemoveAll(p=>p.name==SharedDeckSync.Identity.Name);
        __result.strings.Add(new(SharedDeckSync.Identity.Name,SharedDeckSync.Identity.Get(card)));
    }
}
[HarmonyPatch(typeof(SavedProperties),"FillInternal")]
internal static class SharedNativeCardIdentityImport
{
    private static void Postfix(SavedProperties __instance,object model)
    {
        if(model is not CardModel card)return;
        foreach(var p in __instance.strings??new())if(p.name==SharedDeckSync.Identity.Name){SharedDeckSync.Identity.Set(card,p.value);break;}
    }
}
[HarmonyPatch(typeof(ModelIdSerializationCache),"Init")]
internal static class SharedNativeCardIdentityName
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix()
    {
        var ids=(Dictionary<string,int>)AccessTools.Field(typeof(ModelIdSerializationCache),"_propertyNameToNetIdMap").GetValue(null)!;
        var names=(List<string>)AccessTools.Field(typeof(ModelIdSerializationCache),"_netIdToPropertyNameMap").GetValue(null)!;
        string name=SharedDeckSync.Identity.Name;if(ids.ContainsKey(name))return;
        ids.Add(name,names.Count);names.Add(name);
        AccessTools.PropertySetter(typeof(ModelIdSerializationCache),"PropertyIdBitSize").Invoke(null,new object[]{(int)Math.Ceiling(Math.Log2(names.Count))});
    }
}
