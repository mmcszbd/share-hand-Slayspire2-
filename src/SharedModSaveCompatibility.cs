using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace SharedHandPrototype;

// Preserve registered extension data when a multiplayer save adapter replaces
// vanilla's writer or clones SerializableRun with the native JSON context.
// No character/resource names or gameplay values belong in this bridge.
internal static class SharedModSaveCompatibility
{
    private static readonly Harmony Patcher=new(Entry.ModId+".mod-save");
    private static bool _cloneInstalled;
    private static Type? Helpers=>AppDomain.CurrentDomain.GetAssemblies()
        .Select(a=>a.GetType("STS2RitsuLib.RunData.Patches.RunSavedDataPatchHelpers",false)).FirstOrDefault(t=>t!=null);
    internal sealed class Capture
    {
        internal object State=null!;
        internal MethodInfo End=null!;
        internal bool Closed;
        internal Task Finish(Task task)
        {
            if(Closed)return task;
            Closed=true;
            return (Task)End.Invoke(null,new[]{task,State})!;
        }
    }
    internal static Capture? Begin(RunSaveManager manager)
    {
        if(RunManager.Instance.DebugOnlyGetState()?.Players.Count is not (>=2 and <=PartyLimits.MaxPlayers)||Helpers is not {} helper)return null;
        var begin=AccessTools.Method(helper,"BeginSaveRunCapture");
        var end=AccessTools.Method(helper,"EndSaveRunCaptureAfter");
        if(begin==null||end==null)return null;
        return new Capture{State=begin.Invoke(null,new object?[]{manager,null,null})!,End=end};
    }
    internal static void Scan(Type[] types)
    {
        if(_cloneInstalled)return;
        var type=types.FirstOrDefault(t=>t.FullName=="SteamRandomMatchMod.Net.MpRestartRunPruner");
        if(type==null)return;
        var clone=AccessTools.Method(type,"CloneRun",new[]{typeof(SerializableRun)});
        if(clone==null||clone.ReturnType!=typeof(SerializableRun))return;
        Patcher.Patch(clone,postfix:new HarmonyMethod(typeof(SharedModSaveCompatibility),nameof(Copied)));
        _cloneInstalled=true;
    }
    private static void Copied(SerializableRun source,SerializableRun __result)
    {
        if(ReferenceEquals(source,__result)||source.Players.Count is not (>=2 and <=PartyLimits.MaxPlayers))return;
        var registry=AppDomain.CurrentDomain.GetAssemblies().Select(a=>a.GetType("STS2RitsuLib.RunData.RunSavedDataRegistry",false)).FirstOrDefault(t=>t!=null);
        if(registry!=null)AccessTools.Method(registry,"MergeDocuments")?.Invoke(null,new object[]{__result,source});
    }
}

[HarmonyPatch(typeof(RunSaveManager),nameof(RunSaveManager.SaveRun),new[]{typeof(AbstractRoom)})]
internal static class SharedRootSaveCapturePatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(RunSaveManager __instance,out SharedModSaveCompatibility.Capture? __state)=>
        __state=SharedModSaveCompatibility.Begin(__instance);
    private static void Postfix(ref Task __result,SharedModSaveCompatibility.Capture? __state)
    {if(__state!=null)__result=__state.Finish(__result);}
    private static void Finalizer(Exception? __exception,SharedModSaveCompatibility.Capture? __state)
    {if(__exception!=null&&__state!=null)__state.Finish(Task.CompletedTask).GetAwaiter().GetResult();}
}

