using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;

namespace SharedHandPrototype;

[HarmonyPatch(typeof(NPlayerHand), "StartCardPlay")]
internal static class StackPlayChoice
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(NPlayerHand __instance, ref NHandCardHolder holder)
    {
        HandStacks.PickAvailable(__instance,ref holder);
        if(SelectionHints.LockedForLocal(holder.CardModel))
        { ManualFeedback.Locked(holder.CardModel); return false; }
        if(!TeammatePlayLimit.BlockedForLocal(holder.CardModel)) return true;
        TeammatePlayLimit.SpeakLocal(holder.CardModel);
        return false;
    }
}

