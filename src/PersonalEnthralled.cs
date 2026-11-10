using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;

namespace SharedHandPrototype;

// Curse ownership is temporarily rebound for teammate previews. Its restriction
// belongs to the actor whose PHYSICAL hand contains it, never that rebound owner.
[HarmonyPatch(typeof(Enthralled),nameof(Enthralled.ShouldPlay))]
internal static class PersonalEnthralled
{
    private static bool Prefix(Enthralled __instance,CardModel card,AutoPlayType autoPlayType,ref bool __result)
    {
        if(!SharedHandSemantics.Active)return true;
        __result=card is Enthralled||autoPlayType!=AutoPlayType.None||
            card.Owner.PlayerCombatState?.Hand.Cards.Contains(__instance)!=true;
        return false;
    }
}

