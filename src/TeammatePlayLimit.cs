using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.TestSupport;

namespace SharedHandPrototype;

// Only an admitted manual action opens a scope. Selection, upgrades, transforms,
// exhaust effects and automatic replays cannot consume another player's quota.
internal static class TeammatePlayLimit
{
    internal const string Rejection = "本回合打出队友牌的数量已达到上限。";
    private static CombatState? _state;
    private static readonly Dictionary<Player, (int Turn, int Count)> Counts = new();
    private static readonly Dictionary<Player, ulong> LastSpeech = new();
    private static readonly AsyncLocal<PlayScope?> Current = new();
    internal static void Begin(CombatState state) { _state = state; Counts.Clear(); LastSpeech.Clear(); DrawIdentity.Begin(); }
    private static bool Enabled(Player p) => HandSharingMode.Shared && RuleSession.Ready && RuleSession.Current.TeammateCardLimit > 0 &&
        ReferenceEquals(_state, CombatManager.Instance.DebugOnlyGetState()) && _state?.Players.Contains(p) == true;
    internal static int Used(Player player) => player.PlayerCombatState is { } pcs && Counts.TryGetValue(player,out var value) &&
        value.Turn == pcs.TurnNumber ? value.Count : 0;
    internal static bool Blocked(CardModel card, Player actor) => Enabled(actor) &&
        DrawIdentity.Teammate(card,actor) && Used(actor) >= RuleSession.Current.TeammateCardLimit;
    internal static bool BlockedForLocal(CardModel card) => card.CombatState is { } state &&
        LocalContext.GetMe(state) is { } p && Blocked(card,p);
    internal static void SpeakLocal(CardModel card)
    { if(card.CombatState is { } state && LocalContext.GetMe(state) is { } p) Speak(p); }
    internal static void Speak(Player actor)
    {
        if(TestMode.IsOn || !actor.Creature.IsAlive || NCombatRoom.Instance is not { } room) return;
        var now = Time.GetTicksMsec();
        if(LastSpeech.TryGetValue(actor,out var previous) && now-previous < 1800) return;
        LastSpeech[actor] = now;
        var bubble = NThoughtBubbleVfx.Create("我应该[sine]管好我[b]自己...[/b][/sine]",actor.Creature,2.0);
        if(bubble != null) room.CombatVfxContainer.AddChild(bubble);
    }
    internal static IDisposable? Track(CardModel card, Player actor) => Enabled(actor) &&
        card.Pile?.Type == PileType.Hand && DrawIdentity.Teammate(card,actor) ? new PlayScope(card,actor) : null;
    private sealed class PlayScope : IDisposable
    {
        internal readonly CardModel Card;
        internal readonly Player Actor;
        internal readonly CombatState State;
        internal readonly int Turn;
        internal bool Counted;
        private readonly PlayScope? _previous;
        internal PlayScope(CardModel card, Player actor)
        { Card=card;Actor=actor;State=_state!;Turn=actor.PlayerCombatState!.TurnNumber;_previous=Current.Value;Current.Value=this; }
        public void Dispose() => Current.Value=_previous;
    }
    internal static Task Observe(CardModel card, Task move)
    {
        var scope=Current.Value;
        if(scope?.Card != card || scope.Counted) return move;
        // Native manual insertion has already moved the model before its first
        // await. Charge now, before a pile hook can pause and let another action run.
        Commit(scope);
        return scope.Counted ? move : Finish(move,scope);
    }
    private static async Task Finish(Task move, PlayScope scope)
    {
        await move;
        Commit(scope);
    }
    private static void Commit(PlayScope scope)
    {
        if(scope.Counted || !ReferenceEquals(scope.State,_state) || !Enabled(scope.Actor) || scope.Actor.PlayerCombatState?.TurnNumber != scope.Turn ||
            scope.Card.Pile?.Type != PileType.Play) return;
        scope.Counted=true;
        int count=Used(scope.Actor)+1;
        Counts[scope.Actor]=(scope.Turn,count);
        Diagnostics.Record("teammate-card-play",scope.Actor,"count="+count+"; card="+scope.Card.Id.Entry);
    }
}

[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.AddDuringManualCardPlay))]
internal static class TeammateManualCountPatch
{
    private static void Postfix(CardModel card, ref Task __result) => __result=TeammatePlayLimit.Observe(card,__result);
}

