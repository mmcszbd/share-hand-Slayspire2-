using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;

namespace SharedHandPrototype;

// A hand card with no draw identity is available without teammate quota.
// Shivs still display the physical holder's color: native Phantom Blades
// uses that owner even for generated cards with no draw identity.
// Suppress both our colored border and native playable/red/gold highlights,
// including native drag flashes and the separate vertical-hand hover preview.
internal static class UnassignedCardGlow
{
    private static readonly FieldInfo Flash=AccessTools.Field(typeof(NHandCardHolder),"_flash");
    private static readonly FieldInfo FlashTween=AccessTools.Field(typeof(NHandCardHolder),"_flashTween");
    internal static bool Quiet(CardModel? card)=>Entry.Ready && card?.Pile?.Type==PileType.Hand &&
        SharedRules.IsHandSharingParty(card) && DrawIdentity.Drawer(card)==null && !card.Tags.Contains(CardTag.Shiv);
    internal static bool Quiet(NCardHighlight glow)
    {
        for(Node? node=glow.GetParent();node!=null;node=node.GetParent())
            if(node is NCard card)return Quiet(card.Model);
        return false;
    }
    internal static void Hide(NHandCardHolder holder)
    {
        holder.CardNode?.CardHighlight?.AnimHideInstantly();
        if(FlashTween.GetValue(holder) is Tween tween && GodotObject.IsInstanceValid(tween))tween.Kill();
        if(Flash.GetValue(holder) is CanvasItem flash && GodotObject.IsInstanceValid(flash))
        {var color=flash.Modulate;color.A=0;flash.Modulate=color;}
    }
}

[HarmonyPatch]
internal static class UnassignedHighlightPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(NCardHighlight),nameof(NCardHighlight.AnimShow));
        yield return AccessTools.Method(typeof(NCardHighlight),nameof(NCardHighlight.AnimFlash));
    }
    private static bool Prefix(NCardHighlight __instance)
    {
        if(!UnassignedCardGlow.Quiet(__instance))return true;
        __instance.AnimHideInstantly();return false;
    }
}

[HarmonyPatch(typeof(NHandCardHolder),nameof(NHandCardHolder.Flash))]
internal static class UnassignedHolderFlashPatch
{
    private static bool Prefix(NHandCardHolder __instance)
    {
        if(!UnassignedCardGlow.Quiet(__instance.CardNode?.Model))return true;
        UnassignedCardGlow.Hide(__instance);return false;
    }
}

[HarmonyPatch(typeof(NCard),nameof(NCard.UpdateVisuals))]
internal static class UnassignedCardPreviewGlowPatch
{
    private static void Postfix(NCard __instance)
    {
        // Native UpdateVisuals returns before _Ready. Shared-hand previews also
        // use that path while the node is being attached, so honor the same guard.
        if (__instance.IsNodeReady() && UnassignedCardGlow.Quiet(__instance.Model))
            __instance.CardHighlight?.AnimHideInstantly();
    }
}

