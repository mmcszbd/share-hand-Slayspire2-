using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace SharedHandPrototype;

// A blade's creator remains its forging owner when a shared pile changes the
// temporary card holder. Tracking is per combat card, never by card name.
internal static class SharedForge
{
    private sealed class Origin { internal Player Player=null!; }
    private static readonly ConditionalWeakTable<CardModel,Origin> Owners=new();
    internal static void Remember(CardPile pile,CardModel card)
    {
        if(card is not SovereignBlade||!pile.IsCombatPile||card.Owner==null||CardEvaluation.Active)return;
        if(!SharedRules.IsSupportedParty(card))return;
        Owners.GetValue(card,c=>new Origin{Player=SharedRules.PhysicalHolder(c)});
    }
    private static Player Owner(CardModel card)=>Owners.TryGetValue(card,out var origin)?origin.Player:card.Owner;
    internal static IEnumerable<SovereignBlade> Find(Player player,bool includeExhausted)
    {
        var state=player.Creature.CombatState;
        if(state==null)return System.Array.Empty<SovereignBlade>();
        var blades=state.Players.Where(p=>p.PlayerCombatState!=null)
            .SelectMany(p=>p.PlayerCombatState!.AllPiles).Distinct().SelectMany(p=>p.Cards).Distinct()
            .OfType<SovereignBlade>().Where(c=>!c.IsDupe&&(includeExhausted||c.Pile?.Type!=PileType.Exhaust));
        if(!RuleSession.Current.UnifiedForge)return blades.Where(c=>Owner(c)==player).ToArray();
        // The earliest combat card is the shared original. Native duplicate
        // cards keep IsDupe and are excluded by the original forging rule.
        return blades.OrderBy(c=>c.Pile?.Type==PileType.Exhaust?1:0)
            .ThenBy(c=>NetCombatCardDb.Instance.TryGetCardId(c,out var id)?id:uint.MaxValue).Take(1).ToArray();
    }
}

[HarmonyPatch(typeof(CardPile),nameof(CardPile.AddInternal))]
internal static class SharedForgeRememberPatch
{
    private static void Postfix(CardPile __instance,CardModel card)=>SharedForge.Remember(__instance,card);
}

[HarmonyPatch(typeof(ForgeCmd),"GetSovereignBlades")]
internal static class SharedForgeLookupPatch
{
    private static bool Prefix(Player player,bool includeExhausted,ref IEnumerable<SovereignBlade> __result)
    {
        if(!RuleSession.Ready||!SharedRules.IsSupportedParty(player.Creature.CombatState))return true;
        __result=SharedForge.Find(player,includeExhausted);return false;
    }
}
