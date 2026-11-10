using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Cards;

namespace SharedHandPrototype;

// CardPlay.Player is the committed effect actor. Card.Owner can change after
// borrowing, nested effects or restoration; it is not a past play's identity.
[HarmonyPatch(typeof(Pinpoint),nameof(Pinpoint.AfterCardPlayed))]
internal static class SharedPinpoint
{
    private static bool Prefix(Pinpoint __instance,CardPlay cardPlay,ref Task __result)
    {
        if(!SharedRules.IsHandSharingParty(__instance))return true;
        var owner=SharedRules.PhysicalHolder(__instance);
        int before=__instance.EnergyCost.GetWithModifiers(CostModifiers.All);
        bool eligible=cardPlay.Card.Type==CardType.Skill && cardPlay.Player==owner;
        if(eligible)
        {
            __instance.EnergyCost.AddThisTurn(-1);
        }
        Diagnostics.Record("pinpoint-check",cardPlay.Player,
            "source="+cardPlay.Card.Id.Entry+";sourceType="+cardPlay.Card.Type+
            ";sourceOwner="+Diagnostics.Role(cardPlay.Card.Owner)+";pinpointOwner="+Diagnostics.Role(owner)+
            ";pinpointIndex="+(NetCombatCardDb.Instance.TryGetCardId(__instance,out var id)?id.ToString():"none")+
            ";eligible="+eligible+";cost="+before+"->"+
            __instance.EnergyCost.GetWithModifiers(CostModifiers.All));
        __result=Task.CompletedTask;
        return false;
    }
    private static void Postfix(Pinpoint __instance,CardPlay cardPlay)
    {
        if(!HandSharingMode.Private||!SharedRules.IsSupportedParty(__instance)||cardPlay.Card.Type!=CardType.Skill)return;
        Diagnostics.Record("pinpoint-private-check",cardPlay.Player,
            "source="+cardPlay.Card.Id.Entry+";pinpointOwner="+Diagnostics.Role(__instance.Owner)+
            ";cost="+__instance.EnergyCost.GetWithModifiers(CostModifiers.All));
    }
}

