using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace SharedHandPrototype;

// A synchronized permanent deck contains every character's starters. Prefer
// the recipient's own starter, while preserving the native fallback if it has
// been removed. Query the native mapping so mod registrations still work.
[HarmonyPatch(typeof(ArchaicTooth),"GetTranscendenceStarterCard")]
internal static class SharedArchaicTooth
{
    private static void Postfix(Player player,ref CardModel? __result)
    {
        if(!SharedDeckSync.Enabled(player.RunState))return;
        var map=AccessTools.Property(typeof(ArchaicTooth),"TranscendenceUpgrades").GetValue(null) as IDictionary<ModelId,CardModel>;
        if(map==null)return;
        var starterIds=player.Character.StartingDeck.Select(c=>c.Id).ToHashSet();
        __result=player.Deck.Cards.FirstOrDefault(c=>starterIds.Contains(c.Id)&&map.ContainsKey(c.Id))??__result;
    }
}
