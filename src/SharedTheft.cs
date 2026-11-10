using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Monsters;

namespace SharedHandPrototype;

// One synchronized permanent identity can have several independent combat
// copies. Hopper chooses every victim before removing any permanent card;
// reserve its chosen identities so it never deletes a now-missing mirror.
internal static class SharedTheft
{
    internal static readonly AsyncLocal<HashSet<string>?> Reserved = new();
    internal static CardModel? EligibleDeck(CardModel card)
    {
        var deck=card.DeckVersion;
        if(Reserved.Value is not {} reserved || !SharedDeckSync.Enabled(card.Owner.RunState))return deck;
        if(deck?.Pile?.Type!=PileType.Deck || deck.HasBeenRemovedFromState)return null;
        string id=SharedDeckSync.Identity.Get(deck);
        return id.Length>0&&reserved.Contains(id)?null:deck;
    }
    internal static Task RemoveChosen(CardModel card,bool skipVisuals)
    {
        if(Reserved.Value is {} reserved && SharedDeckSync.Enabled(card.Owner.RunState) && card.DeckVersion is {} deck)
        {
            string id=SharedDeckSync.Identity.Get(deck);
            if(id.Length>0)reserved.Add(id);
        }
        return CardPileCmd.RemoveFromCombat(card,skipVisuals);
    }
}

[HarmonyPatch(typeof(ThievingHopper),"ThieveryMove")]
internal static class SharedTheftScope
{
    private static void Prefix(out HashSet<string>? __state)
    {__state=SharedTheft.Reserved.Value;SharedTheft.Reserved.Value=new(StringComparer.Ordinal);}
    private static void Finalizer(HashSet<string>? __state)=>SharedTheft.Reserved.Value=__state;
}

[HarmonyPatch]
internal static class SharedTheftSelectionPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var getter=AccessTools.PropertyGetter(typeof(CardModel),nameof(CardModel.DeckVersion));
        foreach(var type in typeof(ThievingHopper).GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic))
            foreach(var method in type.GetMethods(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
                if(!method.IsAbstract && !method.ContainsGenericParameters && CompatibilityScan.CallsAny(method,new[]{getter}))yield return method;
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var getter=AccessTools.PropertyGetter(typeof(CardModel),nameof(CardModel.DeckVersion));
        foreach(var i in instructions)
        {if(i.Calls(getter)){i.opcode=OpCodes.Call;i.operand=AccessTools.Method(typeof(SharedTheft),nameof(SharedTheft.EligibleDeck));}yield return i;}
    }
}

[HarmonyPatch]
internal static class SharedTheftReservePatch
{
    private static MethodBase TargetMethod()=>AccessTools.Method(AccessTools.Method(typeof(ThievingHopper),"ThieveryMove")
        .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType,"MoveNext");
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var remove=AccessTools.Method(typeof(CardPileCmd),nameof(CardPileCmd.RemoveFromCombat),new[]{typeof(CardModel),typeof(bool)});
        int found=0;
        foreach(var i in instructions)
        {if(i.Calls(remove)){i.opcode=OpCodes.Call;i.operand=AccessTools.Method(typeof(SharedTheft),nameof(SharedTheft.RemoveChosen));found++;}yield return i;}
        if(found!=1)throw new InvalidOperationException("无法定位偷窃草蜢的选牌移除点。");
    }
}

