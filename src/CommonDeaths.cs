using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.TestSupport;

namespace SharedHandPrototype;

// A common-pile card's current owner is its last drawer, not its contributor.
// Keep the original logical deck identity across draws and ownership changes.
internal static class CommonDeaths
{
    private static readonly Dictionary<CardModel,ulong> Sources=new();
    private static readonly HashSet<ulong> Dead=new();
    internal static void Begin(){Sources.Clear();Dead.Clear();}
    internal static ulong Source(CardModel card)
    {
        if(Sources.TryGetValue(card,out var source))return source;
        // Save validation constructs cards before they have a live owner or
        // RunState. Death provenance is combat-only metadata; never force a
        // deserialized deck card through the combat ownership path.
        var owner=card.Owner;
        var run=owner?.RunState;
        if(owner==null||run==null)return 0;
        string identity=SharedDeckSync.Identity.Get(card.DeckVersion??card);
        int separator=identity.IndexOf(':');
        if(separator>0&&ulong.TryParse(identity.Substring(0,separator),NumberStyles.None,CultureInfo.InvariantCulture,out source)&&
            run.Players.Any(p=>p.NetId==source))return Sources[card]=source;
        if(card.CloneOf is CardModel original)return Sources[card]=Source(original);
        var physical=SharedRules.PhysicalHolder(card);
        return Sources[card]=physical?.NetId??owner.NetId;
    }
    internal static void Remember(CardModel card)
    {
        if(!CommonPiles.Active||CardEvaluation.Active)return;
        // During RunState.FromSerializable the card is temporarily inserted
        // into a deck before its player is attached. Ignore that insertion;
        // the first real combat pile transition will establish provenance.
        if(card.Owner?.RunState==null)return;
        _=Source(card);
    }
    internal static bool Retired(CardModel card)=>CommonPiles.Active&&Dead.Contains(Source(card));
    internal static bool Handle(CombatId? id,Player player)=>CommonPiles.Active&&id==CombatManager.Instance.CurrentCombatId&&
        player.Creature.IsDead&&player.PlayerCombatState!=null&&
        CombatManager.Instance.DebugOnlyGetState() is CombatState state&&state.Players.Contains(player)&&state.Players.Any(p=>p.Creature.IsAlive);
    internal static async Task Remove(Player player)
    {
        Dead.Add(player.NetId);
        var pcs=player.PlayerCombatState!;
        var living=player.RunState.Players.First(p=>p.Creature.IsAlive);
        // Status cards generated for the deceased belong to that recipient,
        // never to a shared storage carrier or the person later drawing them.
        var personal=pcs.Hand.Cards.Concat(pcs.PlayPile.Cards).ToArray();
        var rescued=pcs.Hand.Cards.Where(c=>!Retired(c)&&c.Type!=CardType.Status).ToArray();
        foreach(var card in rescued)Rescue(card,living,player);
        CommonPiles.EnumerateThrough(living);
        foreach(var card in pcs.DrawPile.Cards.Concat(pcs.DiscardPile.Cards).Where(c=>c.Owner.Creature.IsDead&&!Retired(c)).ToArray())
        {
            var old=card.Pile;
            // A survivor's card can be in the dead player's independent draw
            // pile after a shared discard was shuffled there. Transfer the
            // physical card as well as ownership so it cannot be stranded.
            if(old!=null && !ReferenceEquals(old,CardPile.Get(old.Type,living)))
                SharedRules.MoveSameCard(card,living,old.Type);
            else card.GiveToAnotherPlayer(living);
        }
        // Include ALL personal hands: a deceased contributor's Strike/Defend
        // may be a hidden physical card inside a survivor's visible stack.
        var removed=personal.Except(rescued)
            .Concat(player.RunState.Players.SelectMany(p=>p.PlayerCombatState!.Hand.Cards).Where(Retired))
            .Concat(pcs.DrawPile.Cards.Where(Retired)).Concat(pcs.DiscardPile.Cards.Where(Retired))
            .Concat(player.RunState.Players.SelectMany(p=>p.PlayerCombatState!.ExhaustPile.Cards).Where(Retired))
            .Where(c=>c.Pile?.IsCombatPile==true).Distinct().ToArray();
        var visuals=removed.Select(DeathVisuals.Take).Where(v=>v!=null).ToArray();
        // Backend removal and original hooks are awaited; cosmetic animations
        // run separately, so a tween can never strand the enemy turn.
        await CardPileCmd.RemoveFromCombat(removed,skipVisuals:true);
        foreach(var view in visuals)DeathVisuals.Dissolve(view!);
        Diagnostics.Record("death-cleanup",player,removed.Length+" retired; "+rescued.Length+" returned");
    }
    private static void Rescue(CardModel card,Player living,Player dead)
    {
        var view=DeathVisuals.Take(card);
        SharedRules.MoveSameCard(card,living,PileType.Discard);
        if(view==null)return;
        view.UpdateVisuals(PileType.Discard,CardPreviewMode.Normal);view.CardHighlight.AnimHideInstantly();
        var fly=NCardFlyVfx.Create(view,PileType.Discard,false,dead.Character.TrailPath);
        if(fly!=null)
        {
            NCombatRoom.Instance.Ui.AddChild(fly);
            AccessTools.Field(typeof(NCardFlyVfx),"_duration").SetValue(fly,DeathVisuals.Duration*.75f);
            AccessTools.Field(typeof(NCardFlyVfx),"_speed").SetValue(fly,1f);
            AccessTools.Field(typeof(NCardFlyVfx),"_accel").SetValue(fly,0f);
            fly.AddChild(new DeathTrailBlend(view,fly,dead.Character.TrailPath,living.Character.TrailPath));
        }
        else view.QueueFree();
    }
    internal static async Task<IReadOnlyList<CardPileAddResult>> Prune(Task<IReadOnlyList<CardPileAddResult>> task)
    {
        var result=await task;
        // A dead contributor's card already held by a survivor may finish its
        // current use, but cannot reenter any reusable common stock afterward.
        var retired=result.Where(r=>r.success).Select(r=>r.cardAdded).Distinct()
            .Where(c=>Retired(c)&&c.Pile?.Type is PileType.Draw or PileType.Discard or PileType.Exhaust).ToArray();
        if(retired.Length>0)await CardPileCmd.RemoveFromCombat(retired,skipVisuals:true);
        return result;
    }
}

[HarmonyPatch(typeof(CombatManager),nameof(CombatManager.RemoveDeadPlayerCardsFromCombat))]
internal static class CommonDeathCleanupPatch
{
    private static bool Prefix(CombatId? combatId,Player player,ref Task __result)
    {
        if(!CommonDeaths.Handle(combatId,player))return true;
        __result=CommonDeaths.Remove(player);return false;
    }
}
[HarmonyPatch(typeof(CardPile),nameof(CardPile.AddInternal))]
internal static class CommonCardSourcePatch
{
    private static void Prefix(CardModel card)=>CommonDeaths.Remember(card);
}
[HarmonyPatch(typeof(CardPileCmd),nameof(CardPileCmd.Add),new[]{typeof(IEnumerable<CardModel>),typeof(CardPile),typeof(CardPilePosition),typeof(AbstractModel),typeof(bool),typeof(bool)})]
internal static class CommonDeadCardReturnPatch
{
    private static void Postfix(ref Task<IReadOnlyList<CardPileAddResult>> __result)
    {if(CommonPiles.Active)__result=CommonDeaths.Prune(__result);}
}

[HarmonyPatch(typeof(CardPileCmd),nameof(CardPileCmd.RemoveFromCombat),new[]{typeof(IEnumerable<CardModel>),typeof(bool)})]
internal static class CommonDeathRemovalVisualPatch
{
    private static void Prefix(IEnumerable<CardModel> cards,bool skipVisuals)
    {
        if(skipVisuals||TestMode.IsOn||!HandLayouts.Active(NPlayerHand.Instance))return;
        foreach(var card in cards)if(card.Pile?.Type==PileType.Hand)HandStacks.Departure(card);
    }
}

