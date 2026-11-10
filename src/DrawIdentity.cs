using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

// Quota identity is separate from native ownership and original contribution.
// Only a successful native draw into a hand assigns it. A visit to any other
// pile ends it; recovery, creation and physical hand-to-hand borrowing do not
// manufacture a new identity. Track() captures it before entry to the play pile.
internal static class DrawIdentity
{
    private static readonly Dictionary<CardModel, ulong> Drawers = new();
    private static readonly Dictionary<CardModel, (CardPile Hand, ulong Drawer)> Pending = new();
    internal static void Begin() { Drawers.Clear(); Pending.Clear(); SharedGhostSeed.Begin(); }
    internal static ulong? Drawer(CardModel card) => Drawers.TryGetValue(card,out var id) ? id : null;
    internal static bool Teammate(CardModel card,Player actor) => Drawer(card) is ulong id && id != actor.NetId;
    internal static void Arrive(CardPile pile,CardModel card)
    {
        if(CardEvaluation.Active || !SharedRules.IsSupportedParty(card.Owner?.Creature.CombatState)) return;
        SharedGhostSeed.Arrive(pile,card);
        if(pile.Type != PileType.Hand) { Drawers.Remove(card); return; }
        if(Pending.TryGetValue(card,out var draw) && ReferenceEquals(pile,draw.Hand))
        { Pending.Remove(card); Drawers[card]=draw.Drawer; }
    }
    internal static async Task<CardPileAddResult> DrawIntoHand(CardModel card,CardPile hand,
        CardPilePosition position,AbstractModel? clonedBy,bool skipVisuals)
    {
        var receiver=card.CombatState?.Players.FirstOrDefault(p=>p.PlayerCombatState?.Hand==hand);
        if(receiver==null || !SharedRules.IsSupportedParty(card))
            return await CardPileCmd.Add(card,hand,position,clonedBy,skipVisuals);
        Pending[card]=(hand,receiver.NetId);
        try { return await CardPileCmd.Add(card,hand,position,clonedBy,skipVisuals); }
        finally { Pending.Remove(card); }
    }
    internal static void Add(CardPile pile,CardModel card,int index,bool silent)
    {
        // Native async command call sites may inline AddInternal. Observe here
        // as well, before CardAdded listeners can make another gameplay choice.
        if(!pile.Cards.Contains(card))Arrive(pile,card);
        pile.AddInternal(card,index,silent);
    }
}

[HarmonyPatch(typeof(CardPile),nameof(CardPile.AddInternal))]
internal static class DrawIdentityArrivalPatch
{
    private static void Prefix(CardPile __instance,CardModel card)
    { if(!__instance.Cards.Contains(card))DrawIdentity.Arrive(__instance,card); }
}

[HarmonyPatch]
internal static class DrawIdentityNativeDrawPatch
{
    private static MethodBase TargetMethod()=>AccessTools.Method(AccessTools.Method(typeof(CardPileCmd),"DrawInternal")
        .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType,"MoveNext");
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var add=AccessTools.Method(typeof(CardPileCmd),nameof(CardPileCmd.Add),new[]{typeof(CardModel),typeof(CardPile),typeof(CardPilePosition),typeof(AbstractModel),typeof(bool)});
        int replaced=0;
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(add))
            { instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(DrawIdentity),nameof(DrawIdentity.DrawIntoHand));replaced++; }
            yield return instruction;
        }
        if(replaced!=1)throw new InvalidOperationException("无法定位原版抽牌入手点，临时归属适配不能安全加载。");
    }
}

[HarmonyPatch]
internal static class DrawIdentityNativeArrivalPatch
{
    private static MethodBase TargetMethod()=>AccessTools.Method(AccessTools.Method(typeof(CardPileCmd),nameof(CardPileCmd.Add),
        new[]{typeof(IEnumerable<CardModel>),typeof(CardPile),typeof(CardPilePosition),typeof(AbstractModel),typeof(bool),typeof(bool)})
        .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType,"MoveNext");
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var add=AccessTools.Method(typeof(CardPile),nameof(CardPile.AddInternal));int replaced=0;
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(add))
            {instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(DrawIdentity),nameof(DrawIdentity.Add));replaced++;}
            yield return instruction;
        }
        if(replaced!=1)throw new InvalidOperationException("无法定位原版卡牌入堆点，临时归属适配不能安全加载。");
    }
}

