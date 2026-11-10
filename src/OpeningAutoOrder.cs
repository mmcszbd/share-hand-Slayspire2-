using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace SharedHandPrototype;

// Setup choices need their own native hook actions, so their completion may
// never depend on another player's setup or on the following AI phase.
// Separate the native contexts, let setup actions finish, then serialize AI
// phases in run player order. No invented choice/result or host-local RNG.
internal static class OpeningAutoOrder
{
    private sealed class Round
    {
        internal object Turn=null!;
        internal Player[] Players=Array.Empty<Player>();
        internal readonly Dictionary<Player,Task> Setups=new();
        internal readonly Dictionary<Player,TaskCompletionSource> Done=new();
        internal readonly TaskCompletionSource Registered=new();
        internal CancellationTokenSource Cancellation=null!;
    }
    private sealed record Pending(HookPlayerChoiceContext Context,bool Deferred);
    private static readonly ConditionalWeakTable<Task,Pending> PendingTasks=new();
    private static Round? _round;
    private static readonly MethodInfo NativeRun=AccessTools.Method(typeof(CombatManager),"RunAutoPrePlayPhase");
    internal static void Begin(object turnState)
    {
        _round?.Cancellation.Cancel();_round?.Cancellation.Dispose();_round=null;
        if(!HandSharingMode.Shared||!SharedRules.IsSupportedParty(CombatManager.Instance.DebugOnlyGetState()))return;
        var type=turnState.GetType();var state=(CombatState)AccessTools.Property(type,"State").GetValue(turnState)!;
        if(state.CurrentSide!=CombatSide.Player||!state.Players.Any(p=>p.Creature.IsAlive&&
            (p.Relics.OfType<WhisperingEarring>().Any()&&p.PlayerCombatState?.TurnNumber<=1||
             p.Creature.Powers.Any(power=>power.GetType().FullName=="LexNinja2.LexNinja2Code.Powers.TwoMonksPower"))))return;
        var extra=((IEnumerable<Player>)AccessTools.Property(type,"PlayersTakingExtraTurn").GetValue(turnState)!).ToArray();
        var players=state.Players.Where(p=>p.Creature.IsAlive&&(extra.Length==0||extra.Contains(p))).ToArray();
        var token=(CancellationToken)AccessTools.Property(type,"Ct").GetValue(turnState)!;
        var round=new Round{Turn=turnState,Players=players,Cancellation=CancellationTokenSource.CreateLinkedTokenSource(token)};
        foreach(var player in players)round.Done[player]=new();
        _round=round;
    }
    internal static void Register(HookPlayerChoiceContext context,Task setup)
    {
        if(_round is not {} round||context.Owner is not {} player||!round.Done.ContainsKey(player))return;
        round.Setups[player]=setup;
        if(round.Setups.Count==round.Players.Length)round.Registered.TrySetResult();
    }
    internal static Task Run(CombatManager manager,object turnState,HookPlayerChoiceContext context,Task setup,Player player)
    {
        if(_round is not {} round||!ReferenceEquals(round.Turn,turnState)||!round.Done.ContainsKey(player))
            return (Task)NativeRun.Invoke(manager,new object[]{turnState,context,setup,player})!;
        Register(context,setup);
        // Bind the original setup action only to setup. Binding it to the AI
        // barrier would make P1's action wait for P2's still-queued choice.
        TaskHelper.RunSafely(context.AssignTaskAndWaitForPauseOrCompletion(setup));
        var fresh=new HookPlayerChoiceContext(player,LocalContext.NetId!.Value,GameActionType.CombatPlayPhaseOnly);
        int index=Array.IndexOf(round.Players,player);
        bool deferred=!round.Registered.Task.IsCompleted||round.Setups.Values.Any(t=>!t.IsCompleted)||
            index>0&&!round.Done[round.Players[index-1]].Task.IsCompleted;
        var unique=new TaskCompletionSource();
        TaskHelper.RunSafely(Forward(Execute(round,manager,turnState,fresh,setup,player,index),unique));
        var task=unique.Task;
        PendingTasks.Add(task,new(fresh,deferred));
        return task;
    }
    private static async Task Forward(Task task,TaskCompletionSource completion)
    {
        try{await task;completion.TrySetResult();}
        catch(OperationCanceledException){completion.TrySetCanceled();}
        catch(Exception ex){completion.TrySetException(ex);}
    }
    private static async Task Execute(Round round,CombatManager manager,object turnState,HookPlayerChoiceContext context,Task setup,Player player,int index)
    {
        var token=round.Cancellation.Token;
        try
        {
            await round.Registered.Task.WaitAsync(token);
            await Task.WhenAll(round.Setups.Values).WaitAsync(token);
            if(index>0)await round.Done[round.Players[index-1]].Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if(CombatManager.Instance.IsOverOrEnding||player.Creature.IsDead)return;
            await (Task)NativeRun.Invoke(manager,new object[]{turnState,context,setup,player})!;
        }
        finally{round.Done[player].TrySetResult();}
    }
    internal static Task<bool> Assign(HookPlayerChoiceContext context,Task task)
    {
        if(!PendingTasks.TryGetValue(task,out var pending))return context.AssignTaskAndWaitForPauseOrCompletion(task);
        var assigned=pending.Context.AssignTaskAndWaitForPauseOrCompletion(task);
        if(!pending.Deferred)return assigned;
        TaskHelper.RunSafely(assigned);
        return Task.FromResult(false);
    }
}
[HarmonyPatch(typeof(CombatManager),"StartTurn")]
internal static class OpeningAutoOrderBegin
{private static void Prefix(object turnState)=>OpeningAutoOrder.Begin(turnState);}
[HarmonyPatch]
internal static class OpeningAutoOrderCalls
{
    private static MethodBase TargetMethod()=>AccessTools.Method(AccessTools.Method(typeof(CombatManager),"StartTurn").GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType,"MoveNext");
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var run=AccessTools.Method(typeof(CombatManager),"RunAutoPrePlayPhase");
        var assign=AccessTools.Method(typeof(HookPlayerChoiceContext),nameof(HookPlayerChoiceContext.AssignTaskAndWaitForPauseOrCompletion));
        int runs=0,assigns=0;
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(run)){instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(OpeningAutoOrder),nameof(OpeningAutoOrder.Run));runs++;}
            else if(instruction.Calls(assign)){instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(OpeningAutoOrder),nameof(OpeningAutoOrder.Assign));assigns++;}
            yield return instruction;
        }
        if(runs!=1||assigns<1)throw new InvalidOperationException("无法定位开局自动操控的原版等待顺序。");
    }
}

