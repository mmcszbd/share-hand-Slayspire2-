using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

// New YouJin smoke escapes without running AfterDeath. Its duel's normal
// last-survivor cleanup lives in AfterDeath, so repeat only the placeholder
// cleanup after an actual escape. Never synthesize a death or its rewards.
internal static class FriendlySmoke
{
    private static MethodInfo? Escaped;
    private static PropertyInfo? Versus;
    private static bool installed;
    internal static void Scan(Type[] types)
    {
        if(installed)return;
        var escape=types.FirstOrDefault(t=>t.FullName=="YouJin.Scripts.SmokeBombEscape");
        if(escape==null)return;
        installed=true;
        CompatibilityScan.Attempt("friendly-smoke-escape",escape.FullName!,()=>
        {
            Escaped=AccessTools.Method(escape,"IsEscaped",new[]{typeof(Creature)})??throw new MissingMethodException();
            var duel=types.Single(t=>t.FullName=="YouJin.Scripts.ArchitectBetrayalEvent");
            Versus=AccessTools.Property(duel,"BetrayalCombatActive")??throw new MissingMemberException();
            var potion=types.Single(t=>t.FullName=="YouJin.Scripts.SmokeBombPotion");
            var use=AccessTools.Method(potion,"OnUse");
            if(use?.ReturnType!=typeof(Task))throw new NotSupportedException("YouJin smoke signature changed.");
            new Harmony(Entry.ModId+".friendly-smoke-escape").Patch(use,
                postfix:new HarmonyMethod(typeof(FriendlySmoke),nameof(AfterUse)));
        });
    }
    private static void AfterUse(PotionModel __instance,ref Task __result)
    {
        if(!RuleSession.Ready||!RuleSession.SupportedParty||Versus?.GetValue(null) is not true||
            CombatManager.Instance.DebugOnlyGetState() is not {} state||__instance.Owner is not {} player||
            !state.Players.Contains(player))return;
        __result=Finish(__result,state,player.Creature);
    }
    private static async Task Finish(Task original,CombatState state,Creature smoker)
    {
        await original;
        var manager=CombatManager.Instance;
        if(!RuleSession.Ready||!ReferenceEquals(manager.DebugOnlyGetState(),state)||!manager.IsInProgress||manager.IsOverOrEnding||
            Versus?.GetValue(null) is not true||Escaped?.Invoke(null,new object[]{smoker}) is not true||
            state.Players.Count(p=>p.Creature.IsAlive)>1)return;
        // Native ordered potion execution awaits this on every peer. Killing
        // the same placeholder is the duel's own ordinary victory trigger.
        foreach(var dummy in state.Enemies.Where(c=>c.IsAlive&&
            c.Monster?.GetType().FullName=="YouJin.Scripts.BetrayalEggMonster").ToArray())
            await CreatureCmd.Kill(dummy,true);
    }
}

