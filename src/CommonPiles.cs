using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace SharedHandPrototype;

internal static class CommonPiles
{
    private static CombatState? _state;
    private static CardPile? _draw, _discard, _exhaust;
    private static bool _shareDraw,_shareDiscard,_shareExhaust;
    private static readonly Dictionary<Player,CardPile[]> EmptyStock=new();
    private static readonly HashSet<string> OpeningIdentities=new(StringComparer.Ordinal);
    internal static bool Active => _state?.Players.Count is >= 2 and <= PartyLimits.MaxPlayers &&
        ReferenceEquals(CombatManager.Instance.DebugOnlyGetState(), _state) &&
        RuleSession.Ready && (_shareDraw||_shareDiscard||_shareExhaust);
    internal static bool SharesDraw=>Active&&_shareDraw;
    internal static bool SharesDiscard=>Active&&_shareDiscard;
    internal static bool SharesExhaust=>Active&&_shareExhaust;
    private static IEnumerable<CardPile> Stock()
    {
        if(_shareDraw&&_draw!=null)yield return _draw;
        if(_shareDiscard&&_discard!=null)yield return _discard;
        if(_shareExhaust&&_exhaust!=null)yield return _exhaust;
    }
    internal static void Begin(CombatState state)
    {
        CommonDeaths.Begin();
        PrivateStockTransfers.ResetChoices();
        TeammatePlayLimit.Begin(state);
        EmptyStock.Clear();
        OpeningIdentities.Clear();
        RuleSession.Tick();
        SharedDeckSync.Initialize(state.RunState);
        var custom=RuleSession.Current.Advanced;
        _shareDraw=custom?.Enabled==true?custom.SharedDraw:RuleSession.Current.PrivateHands || RuleSession.Current.Piles==DrawPileMode.Common;
        _shareDiscard=custom?.Enabled==true?custom.SharedDiscard:RuleSession.Current.PrivateHands || RuleSession.Current.Piles==DrawPileMode.Common;
        _shareExhaust=custom?.Enabled==true?custom.SharedExhaust:RuleSession.Current.PrivateHands;
        _state = (_shareDraw||_shareDiscard||_shareExhaust) && state.Players.Count is >= 2 and <= PartyLimits.MaxPlayers ? state : null;
        _draw = _discard = _exhaust = null;
    }
    internal static void Connect(PlayerCombatState pcs, Player player)
    {
        if (_state == null || !_state.Players.Contains(player)) return;
        var physical = pcs.AllPiles.ToArray();
        if (player == _state.Players[0])
        {
            _draw = pcs.DrawPile; _discard = pcs.DiscardPile; _exhaust=pcs.ExhaustPile;
            return;
        }
        if (_draw == null) throw new InvalidOperationException("公共牌堆初始化顺序错误。");
        // Patch the actual backing references too: native auto-property reads
        // can be JIT-inlined and evade a getter patch. AllPiles keeps this
        // player's empty physical storage, so IDs/hooks/teardown visit the
        // shared pools once, through the first player, without double effects.
        var bindings=new List<(string,CardPile?)>();
        if(_shareDraw)bindings.Add(("DrawPile",_draw));
        if(_shareDiscard)bindings.Add(("DiscardPile",_discard));
        if(_shareExhaust)bindings.Add(("ExhaustPile",_exhaust));
        foreach (var (name, pile) in bindings)
            AccessTools.Field(typeof(PlayerCombatState), "<" + name + ">k__BackingField").SetValue(pcs, pile);
        AccessTools.Field(typeof(PlayerCombatState), "_piles").SetValue(pcs, physical);
    }
    internal static CardPile? Find(CardModel card)
    {
        if (!Active) return null;
        foreach (var pile in Stock())
            if (pile?.Cards.Contains(card) == true) return pile;
        return null;
    }
    internal static IEnumerable<CardModel> OwnedCards(PlayerCombatState pcs, IEnumerable<CardModel> original)
    {
        if (!Active) return original;
        var owner = (Player)AccessTools.Field(typeof(PlayerCombatState), "_player").GetValue(pcs)!;
        var all=original.Concat(Stock().SelectMany(p=>p.Cards)).Distinct();
        if(RuleSession.Current.Advanced?.Enabled==true)return all;
        return _shareExhaust?all:all.Where(c => c.Owner == owner);
    }
    internal static void EnumerateThrough(Player living)
    {
        if(!Active)return;
        // Native hook traversal skips dead players before inspecting AllPiles.
        // Move the single traversal of common storage to a living player too;
        // retaining the first player's old traversal silently disables cards.
        foreach(var player in _state!.Players)
        {
            if(player.PlayerCombatState is not {} pcs)continue;
            if(!EmptyStock.TryGetValue(player,out var empty))
                EmptyStock[player]=empty=new[]{new CardPile(PileType.Draw),new CardPile(PileType.Discard),new CardPile(PileType.Exhaust)};
            var stock=player==living?new[]{_draw!,_discard!,_exhaust!}:empty;
            AccessTools.Field(typeof(PlayerCombatState),"_piles").SetValue(pcs,new[]{pcs.Hand,
                _shareDraw?stock[0]:pcs.DrawPile,_shareDiscard?stock[1]:pcs.DiscardPile,
                _shareExhaust?stock[2]:pcs.ExhaustPile,pcs.PlayPile});
        }
    }
    internal static void PrepareHandAdd(CardModel card, CardPile target)
    {
        if (!Active || target.Type != PileType.Hand || Find(card) == null) return;
        CommonDeaths.Remember(card);
        var receiver = _state!.Players.FirstOrDefault(p => p.PlayerCombatState?.Hand == target);
        if (receiver != null && card.Owner != receiver) card.GiveToAnotherPlayer(receiver);
    }
    internal static bool CompatibleOwners(Player a, Player b, CardPile target) => a == b ||
        (Active && (_shareDraw&&target == _draw || _shareDiscard&&target == _discard || _shareExhaust&&target==_exhaust) &&
        _state!.Players.Contains(a) && _state.Players.Contains(b));
    internal static IEnumerable<CardModel> SharedCards()=>Stock().SelectMany(p=>p.Cards).Distinct();
    internal static void PopulateUnique(Player player,MegaCrit.Sts2.Core.Random.Rng rng,CombatState state)
    {
        foreach(var original in player.Deck.Cards.ToArray())
        {
            string id=SharedDeckSync.Identity.Get(original);
            if(!string.IsNullOrEmpty(id)&&!OpeningIdentities.Add(id))continue;
            var clone=state.CloneCard(original);clone.DeckVersion=original;
            player.PlayerCombatState!.DrawPile.AddInternal(clone);
        }
        player.PlayerCombatState!.DrawPile.RandomizeOrderInternal(player,rng,state);
    }
    internal static async Task<IReadOnlyList<CardPileAddResult>> AddMixed(CardModel[] cards, CardPile target,
        CardPilePosition position, AbstractModel? clonedBy, bool skipVisuals, bool changingOwners)
    {
        var result = new List<CardPileAddResult>();
        // Contiguous single-card batches preserve shuffled/top insertion order,
        // unlike grouping all cards of one owner together.
        foreach (var card in cards)
            result.AddRange(await CardPileCmd.Add(new[] { card }, target, position, clonedBy, skipVisuals, changingOwners));
        return result;
    }
}

[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SetUpCombat))]
internal static class CommonPileSetupPatch
{
    private static void Prefix(CombatState state) => CommonPiles.Begin(state);
}
[HarmonyPatch(typeof(PlayerCombatState), MethodType.Constructor, new[] { typeof(Player) })]
internal static class CommonPileConnectPatch
{
    private static void Postfix(PlayerCombatState __instance, Player player) => CommonPiles.Connect(__instance, player);
}
[HarmonyPatch(typeof(Player), nameof(Player.PopulateCombatState))]
internal static class CommonPilePopulatePatch
{
    private static bool Prefix(Player __instance,MegaCrit.Sts2.Core.Random.Rng rng,CombatState state)
    {
        if(RuleSession.Current.Advanced is {Enabled:true} custom)
        {
            if(custom.UniqueMirroredCards&&CommonPiles.SharesDraw)
            {CommonPiles.PopulateUnique(__instance,rng,state);return false;}
            return true;
        }
        return !CommonPiles.Active || !RuleSession.Current.ShareDeck || RuleSession.Current.IndependentCopies || __instance == state.Players[0];
    }
}
[HarmonyPatch(typeof(PlayerCombatState), "get_AllCards")]
internal static class CommonOwnedCardsPatch
{
    private static void Postfix(PlayerCombatState __instance, ref IEnumerable<CardModel> __result) =>
        __result = CommonPiles.OwnedCards(__instance, __result);
}
[HarmonyPatch(typeof(CardPileCmd), nameof(CardPileCmd.Add), new[] { typeof(CardModel), typeof(CardPile), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool) })]
internal static class CommonDrawOwnershipPatch
{
    private static void Prefix(CardModel card, CardPile newPile) { CommonDeaths.Remember(card); CommonPiles.PrepareHandAdd(card, newPile); }
}
[HarmonyPatch(typeof(CardPileCmd),nameof(CardPileCmd.Shuffle))]
internal static class AdvancedSharedDiscardShufflePatch
{
    private static void Prefix(Player player)
    {
        if(RuleSession.Current.Advanced is not {Enabled:true} || !CommonPiles.SharesDiscard ||
           CommonPiles.SharesDraw || CombatManager.Instance.IsOverOrEnding)return;
        // The player performing the shuffle takes the shared discard into
        // their personal draw pile. Remember origin before changing owner.
        foreach(var card in player.PlayerCombatState!.DiscardPile.Cards.ToArray())
            if(card.Owner!=player){CommonDeaths.Remember(card);card.GiveToAnotherPlayer(player);}
    }
}
[HarmonyPatch]
internal static class CommonNativeBatchPatch
{
    private static readonly Type Machine = AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.Add),
        new[] { typeof(IEnumerable<CardModel>), typeof(CardPile), typeof(CardPilePosition), typeof(AbstractModel), typeof(bool), typeof(bool) })
        .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
    private static MethodBase TargetMethod() => AccessTools.Method(Machine, "MoveNext");
    private static void Prefix(object __instance)
    {
        if (!CommonPiles.Active || (int)AccessTools.Field(Machine, "<>1__state").GetValue(__instance)! != -1) return;
        var target = (CardPile)AccessTools.Field(Machine, "newPile").GetValue(__instance)!;
        foreach (var card in (IEnumerable<CardModel>)AccessTools.Field(Machine, "cards").GetValue(__instance)!)
        { CommonDeaths.Remember(card); CommonPiles.PrepareHandAdd(card, target); }
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var list = instructions.ToList();
        bool patched = false;
        for (int i = 0; i < list.Count; i++)
        {
            if (i + 1 < list.Count && list[i + 1].opcode == OpCodes.Ldstr &&
                Equals(list[i + 1].operand, "Tried to add cards with different owners to the same pile!") &&
                (list[i].opcode == OpCodes.Beq || list[i].opcode == OpCodes.Beq_S))
            {
                var load = new CodeInstruction(OpCodes.Ldarg_0); load.labels.AddRange(list[i].labels); list[i].labels.Clear();
                yield return load;
                yield return new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(Machine, "newPile"));
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CommonPiles), nameof(CommonPiles.CompatibleOwners)));
                list[i].opcode = OpCodes.Brtrue;
                patched = true;
            }
            yield return list[i];
        }
        if (!patched) throw new InvalidOperationException("原版批量入堆校验发生变化，公共牌堆适配不能安全加载。");
    }
}




