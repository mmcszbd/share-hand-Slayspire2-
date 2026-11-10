using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

// Collect only the native start-of-turn draw requests, after each player's
// energy, relics, powers and innate setup have run. Other draws remain native.
// Releasing the first setup wait at this rendezvous lets native StartTurn set
// up the other player, without inventing a choice or a new network action.
internal static class FairHandDraw
{
    private sealed class Request
    {
        internal PlayerChoiceContext Context = null!;
        internal int Left;
        internal readonly List<CardModel> Cards = new();
        internal readonly TaskCompletionSource<IEnumerable<CardModel>> Result = new();
    }
    private sealed class Round
    {
        internal CombatState State = null!;
        internal Player[] Players = Array.Empty<Player>();
        internal readonly Dictionary<Player, Request> Requests = new();
        internal readonly Dictionary<Player, TaskCompletionSource> Reached = new();
        internal readonly Dictionary<Player,decimal> NativeOpening = new();
        internal readonly Dictionary<Player, Task<bool>> SetupWaits = new();
        internal bool Bypass, Draining;
    }
    private static Round? _round;
    internal static decimal OpeningDraw(decimal basic,decimal innate,Player player)
    {
        var round=_round;
        if(!CommonPiles.SharesDraw||round==null||round.Bypass||!round.Reached.ContainsKey(player))return Math.Max(basic,innate);
        round.NativeOpening[player]=Math.Min(Math.Max(basic,innate),CardPile.MaxCardsInHand);
        // One common set of innate cards cannot enlarge every player's draw.
        // Drain grants a single team-wide minimum after all base draws arrive.
        return basic;
    }
    [ThreadStatic] private static bool _native;
    internal static void Begin(object turnState)
    {
        if (_round is { } previous)
            foreach (var request in previous.Requests.Values) request.Result.TrySetResult(request.Cards);
        _round = null;
        if (!CommonPiles.SharesDraw) return;
        var type = turnState.GetType();
        var state = (CombatState)AccessTools.Property(type, "State").GetValue(turnState)!;
        if (state.CurrentSide != CombatSide.Player) return;
        var extra = ((IEnumerable<Player>)AccessTools.Property(type, "PlayersTakingExtraTurn").GetValue(turnState)!).ToArray();
        var players = (extra.Length > 0 ? extra : state.Players).Where(p => p.Creature.IsAlive).ToArray();
        if (players.Length < 2) return;
        _round = new Round { State = state, Players = players };
        foreach (var player in players) _round.Reached[player] = new();
    }
    internal static bool TryDraw(PlayerChoiceContext context, decimal count, Player player, bool fromHandDraw,
        out Task<IEnumerable<CardModel>>? result)
    {
        result = null;
        var round = _round;
        if (_native || !CommonPiles.SharesDraw || !fromHandDraw || round == null || round.Bypass ||
            !ReferenceEquals(round.State, player.Creature.CombatState) || !round.Reached.ContainsKey(player) ||
            round.Requests.ContainsKey(player)) return false;
        var request = new Request { Context = context, Left = count > 0 ? (int)Math.Min(9999, Math.Ceiling(count)) : 0 };
        round.Requests.Add(player, request);
        result = request.Result.Task;
        round.Reached[player].TrySetResult();
        if (round.Requests.Count == round.Players.Length) _ = Drain(round);
        return true;
    }
    private static async Task<IEnumerable<CardModel>> NativeDraw(Request request, decimal count, Player player)
    {
        Task<IEnumerable<CardModel>> task;
        _native = true;
        try { task = CardPileCmd.Draw(request.Context, count, player, fromHandDraw: true); }
        finally { _native = false; }
        return await task;
    }
    private static async Task Drain(Round round)
    {
        if (round.Draining) return;
        round.Draining = true;
        try
        {
            if (round.Bypass)
            {
                foreach (var player in round.Players)
                    if (round.Requests.TryGetValue(player, out var request))
                    { request.Cards.AddRange(await NativeDraw(request,Math.Max(request.Left,round.NativeOpening.GetValueOrDefault(player)),player)); request.Left = 0; }
            }
            else
            {
                if(round.NativeOpening.Count>0)
                {
                    int innate=round.Players[0].PlayerCombatState!.DrawPile.Cards.Count(c=>c.Keywords.Contains(CardKeyword.Innate)&&!(c.Enchantment?.ShouldStartAtBottomOfDrawPile??false));
                    int extra=Math.Max(0,innate-round.Requests.Values.Sum(r=>r.Left));
                    while(extra>0)foreach(var player in round.Players){if(extra--<=0)break;round.Requests[player].Left++;}
                }
                while (round.Requests.Values.Any(r => r.Left > 0) && CommonPiles.SharesDraw &&
                    ReferenceEquals(round, _round) && !CombatManager.Instance.IsOverOrEnding)
                    foreach (var player in round.Players)
                    {
                        var request = round.Requests[player];
                        if (request.Left <= 0) continue;
                        var drawn = (await NativeDraw(request, 1, player)).ToArray();
                        request.Cards.AddRange(drawn);
                        request.Left = drawn.Length == 0 ? 0 : request.Left - 1;
                    }
            }
            foreach (var request in round.Requests.Values) request.Result.TrySetResult(request.Cards);
        }
        catch (Exception e)
        { foreach (var request in round.Requests.Values) request.Result.TrySetException(e); }
    }
    internal static async Task<bool> Wait(HookPlayerChoiceContext context, Task setup)
    {
        OpeningAutoOrder.Register(context,setup);
        var round = _round;
        var nativeWait = context.WaitForPauseOrCompletionWithoutAssigningTask(setup);
        if (round == null || context.Owner == null || !round.Reached.TryGetValue(context.Owner, out var reached))
            return await nativeWait;
        round.SetupWaits[context.Owner] = nativeWait;
        if (round.Bypass) return await nativeWait;
        // A choice before reaching the draw rendezvous must retain vanilla
        // independent setup. In particular, never block the second setup on
        // a first player's choice whose native task has not been assigned yet.
        if (round.Requests.Count < round.Players.Length)
        {
            var winner = await Task.WhenAny(nativeWait, reached.Task);
            if (winner == nativeWait)
            {
                round.Bypass = true;
                if (round.Requests.Count > 0) _ = Drain(round);
                // A pre-draw choice releases native StartTurn, but queued draws
                // must first finish or pause on their own native choice. Otherwise
                // the checksum observes animation-dependent partial draws.
                await Task.WhenAll(round.SetupWaits.Values.ToArray());
                return setup.IsCompleted;
            }
            if (round.Requests.Count < round.Players.Length) return false;
        }
        // Earlier players were released only to collect the remaining draw
        // requests. Reach a stable native setup boundary for every player before
        // StartTurn advances to its checksum and binds the pending hook actions.
        // Waiting for the full setup tasks here would deadlock native choices.
        await Task.WhenAll(round.SetupWaits.Values.ToArray());
        return setup.IsCompleted;
    }
}

[HarmonyPatch]
internal static class FairOpeningInnatePatch
{
    private static readonly Type Machine=AccessTools.Method(typeof(CombatManager),"SetupPlayerTurn").GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
    private static MethodBase TargetMethod()=>AccessTools.Method(Machine,"MoveNext");
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        int changed=0;var max=AccessTools.Method(typeof(Math),nameof(Math.Max),new[]{typeof(decimal),typeof(decimal)});
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(max))
            {
                var load=new CodeInstruction(OpCodes.Ldarg_0);load.labels.AddRange(instruction.labels);instruction.labels.Clear();load.blocks.AddRange(instruction.blocks);instruction.blocks.Clear();yield return load;
                yield return new CodeInstruction(OpCodes.Ldfld,AccessTools.Field(Machine,"player"));
                instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(FairHandDraw),nameof(FairHandDraw.OpeningDraw));changed++;
            }
            yield return instruction;
        }
        if(changed!=1)throw new InvalidOperationException("无法定位开局固有牌的抽牌数量计算。");
    }
}

[HarmonyPatch(typeof(CombatManager), "StartTurn")]
internal static class FairTurnStartPatch
{
    private static void Prefix(object turnState) => FairHandDraw.Begin(turnState);
}
[HarmonyPatch]
internal static class FairTurnSetupWaitPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(AccessTools.Method(typeof(CombatManager), "StartTurn")
        .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType, "MoveNext");
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original = AccessTools.Method(typeof(HookPlayerChoiceContext), nameof(HookPlayerChoiceContext.WaitForPauseOrCompletionWithoutAssigningTask));
        bool replaced = false;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original))
            { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(FairHandDraw), nameof(FairHandDraw.Wait)); replaced = true; }
            yield return instruction;
        }
        if (!replaced) throw new InvalidOperationException("无法定位原版回合开始抽牌等待点。");
    }
}
[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.Draw), new[] { typeof(PlayerChoiceContext), typeof(decimal), typeof(Player), typeof(bool) })]
internal static class FairStartDrawPatch
{
    private static bool Prefix(PlayerChoiceContext choiceContext, decimal count, Player player, bool fromHandDraw,
        ref Task<IEnumerable<CardModel>> __result)
    {
        if (!FairHandDraw.TryDraw(choiceContext, count, player, fromHandDraw, out var task)) return true;
        __result = task!; return false;
    }
}



