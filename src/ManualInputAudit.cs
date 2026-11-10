using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

// Click-time evidence distinguishes an input/turn guard from a rejected or
// unsent network action. Never poll, change input, or record platform IDs.
[HarmonyPatch(typeof(NPlayerHand),"OnHolderPressed")]
internal static class ManualInputAudit
{
    private static void Prefix(NPlayerHand __instance,NCardHolder holder)
    {
        var card=holder.CardNode?.Model;
        if(card==null||!SharedRules.IsSupportedParty(card))return;
        var actor=LocalContext.GetMe(card.CombatState!);
        Diagnostics.Record("manual-input",actor,card.Id.Entry+";private="+HandSharingMode.Private+
            ";phase="+actor?.PlayerCombatState?.Phase+";disabled="+CombatManager.Instance.PlayerActionsDisabled+
            ";selection="+__instance.IsInCardSelection+";drag="+__instance.InCardPlay+
            ";owner="+Diagnostics.Role(card.Owner)+";localOwner="+(actor==card.Owner)+
            ";pile="+card.Pile?.Type+";holder="+holder.GetType().Name+";ready="+Entry.Ready);
    }
}

[HarmonyPatch(typeof(CardModel),nameof(CardModel.TryManualPlay))]
internal static class ManualPlayResultAudit
{
    private static void Postfix(CardModel __instance,bool __result)
    {
        if(!SharedRules.IsSupportedParty(__instance))return;
        var actor=LocalContext.GetMe(__instance.CombatState!);
        Diagnostics.Record("manual-play-result",actor,__instance.Id.Entry+";private="+HandSharingMode.Private+
            ";result="+__result+";owner="+Diagnostics.Role(__instance.Owner)+
            ";pile="+__instance.Pile?.Type+";ready="+Entry.Ready);
    }
}

