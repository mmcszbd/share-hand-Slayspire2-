using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Godot;

namespace SharedHandPrototype;

// The optional frontend assemblies are never resolved unless their framework is
// already loaded. The standalone core has no AssemblyRef to either framework.
internal static class OptionalSettingsFrontends
{
    private static readonly HashSet<string> Attempted=new();
    private static ulong _next;
    internal static void Tick()
    {
        if(Time.GetTicksMsec()<_next)return;_next=Time.GetTicksMsec()+2000;
        var loaded=AppDomain.CurrentDomain.GetAssemblies();
        Try("BaseLib", "SharedHandPrototype.BaseLibUi",loaded);
        Try("STS2-RitsuLib.Settings", "SharedHandPrototype.RitsuUi",loaded);
    }
    private static void Try(string framework,string name,Assembly[] loaded)
    {
        if(Attempted.Contains(name)||!loaded.Any(a=>a.GetName().Name==framework))return;
        Attempted.Add(name);
        try
        {
            var core=typeof(Entry).Assembly;
            var path=Path.Combine(Path.GetDirectoryName(core.Location)!,name+".dll");
            if(!File.Exists(path))return;
            var adapter=AssemblyLoadContext.GetLoadContext(core)!.LoadFromAssemblyPath(path);
            adapter.GetType("SharedHandPrototype.Optional.SettingsRegistration",true)!.GetMethod("Register")!.Invoke(null,null);
            GD.Print("[SharedHand] Optional settings frontend: "+framework);
        }
        catch(Exception e){GD.PushWarning("[SharedHand] Optional settings frontend unavailable; native settings remain available: "+e.GetBaseException().Message);}
    }
}
