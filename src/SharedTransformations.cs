using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

// A synchronized deck contains foreign character cards whose native Pool stays
// foreign even though the selecting player owns their permanent mirror. Use
// that player's character pool for ordinary character-card transformations.
// Preserve explicit replacement options and native colorless/curse/token rules.
[HarmonyPatch(typeof(CardFactory), nameof(CardFactory.GetDefaultTransformationOptions))]
internal static class SharedTransformPoolPatch
{
    private static void Postfix(CardModel original, bool isInCombat, ref IEnumerable<CardModel> __result)
    {
        var player = original.Owner;
        if (player == null || original.Pile?.Type != PileType.Deck || !SharedDeckSync.Enabled(player.RunState) ||
            original.Type == CardType.Quest || original.Rarity is CardRarity.Event or CardRarity.Ancient or CardRarity.Token ||
            original.Pool == player.Character.CardPool ||
            !player.RunState.Players.Any(p => p.Character.CardPool == original.Pool)) return;
        var options = player.Character.CardPool.GetUnlockedCards(player.UnlockState, player.RunState.CardMultiplayerConstraint);
        __result = (CardModel[])AccessTools.Method(typeof(CardFactory), "GetFilteredTransformationOptions")
            .Invoke(null, new object[] { original, options, isInCombat })!;
    }
}



