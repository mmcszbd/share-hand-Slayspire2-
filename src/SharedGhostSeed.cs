using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Rooms;

namespace SharedHandPrototype;

internal static class SharedGhostSeed
{
    private static readonly HashSet<CardModel> ExhaustedWithSeed = new();
    internal static void Begin()=>ExhaustedWithSeed.Clear();
    internal static void Arrive(CardPile pile,CardModel card)
    {if(pile.Type!=PileType.Exhaust&&pile.Type!=PileType.Play)ExhaustedWithSeed.Remove(card);}
    internal static bool AffectsHand(ICombatState state,CardModel card)
    {
        if(!SharedRules.IsHandSharingParty(state)||card.Pile?.Type!=PileType.Hand||card.Rarity!=CardRarity.Basic||
            (!card.Tags.Contains(CardTag.Strike)&&!card.Tags.Contains(CardTag.Defend)))return false;
        var drawer=DrawIdentity.Drawer(card);
        return state.Players.Any(p=>(drawer==null||p.NetId==drawer)&&p.Creature.IsAlive&&p.Relics.Any(r=>r is GhostSeed));
    }
    internal static void Exhausting(CardModel card)
    {if(card.CombatState is {} state&&AffectsHand(state,card))ExhaustedWithSeed.Add(card);}
    internal static bool Preserved(CardModel card)=>ExhaustedWithSeed.Contains(card)&&card.Pile?.Type is PileType.Exhaust or PileType.Play;
}

[HarmonyPatch(typeof(CardPile),nameof(CardPile.RemoveInternal))]
internal static class SharedGhostSeedExhaustPatch
{
    private static void Prefix(CardPile __instance,CardModel card)
    {if(__instance.Type==PileType.Hand&&__instance.Cards.Contains(card)&&!CardEvaluation.Active)SharedGhostSeed.Exhausting(card);}
}

// Shared-hand Seed follows the current draw-issued identity. It must never
// permanently stamp the combat copy merely because it started in the owner's
// deck: that copy can later be drawn by someone else from the common stock.
[HarmonyPatch]
internal static class SharedGhostSeedStampPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(GhostSeed),nameof(GhostSeed.AfterRoomEntered));
        yield return AccessTools.Method(typeof(GhostSeed),nameof(GhostSeed.AfterCardEnteredCombat));
    }
    private static bool Prefix(object[] __args,ref Task __result)
    {
        if(__args[0] is AbstractRoom room && room is not CombatRoom)return true;
        var state=(__args[0] as CardModel)?.CombatState??CombatManager.Instance.DebugOnlyGetState();
        if(!SharedRules.IsHandSharingParty(state))return true;
        __result=Task.CompletedTask;
        return false;
    }
}

[HarmonyPatch(typeof(Hook),nameof(Hook.ModifyKeywordsInCombat))]
internal static class SharedGhostSeedKeywordsPatch
{
    private static void Postfix(ICombatState combatState,CardModel card,ISet<CardKeyword> keywords)
    {
        if(!SharedRules.IsHandSharingParty(combatState))return;
        // Unassigned hand cards are available to the whole party. Any living
        // holder's Seed affects them; an assigned card consults only its drawer.
        // Preserve the keyword in Exhaust for native Eidolon and similar reads.
        if(SharedGhostSeed.AffectsHand(combatState,card)||SharedGhostSeed.Preserved(card))
            keywords.Add(CardKeyword.Ethereal);
    }
}

