using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.TestSupport;

namespace SharedHandPrototype;

// Change semantic reads in card logic, not physical piles or CardModel.Pile.
// Native movement, network IDs, draw/discard/exhaust ownership stay intact.
internal static class SharedHandSemantics
{
    private static readonly HashSet<Assembly> Scanned = new();
    private static readonly Harmony Patcher = new(Entry.ModId + ".common-hand");
    private static int _assemblyCount;
    internal static int PatchedMethods;
    internal static readonly HashSet<Type> PatchedCardTypes = new();
    private static readonly Dictionary<MethodBase, MethodInfo> Redirect = new()
    {
        [AccessTools.Method(typeof(CardCmd), nameof(CardCmd.AutoPlay))] = AccessTools.Method(typeof(SharedEffectAutoPlay), nameof(SharedEffectAutoPlay.Play)),
        [AccessTools.PropertyGetter(typeof(PlayerCombatState), nameof(PlayerCombatState.AllCards))] = AccessTools.Method(typeof(SharedHandSemantics), nameof(CombatCards)),
        [AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.MaxCardsInHand))] = AccessTools.Method(typeof(SharedHandLimits), nameof(SharedHandLimits.Maximum)),
        [AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.Cards))] = AccessTools.Method(typeof(SharedHandSemantics), nameof(Cards)),
        [AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.IsEmpty))] = AccessTools.Method(typeof(SharedHandSemantics), nameof(IsEmpty)),
        [AccessTools.PropertyGetter(typeof(CardPile), nameof(CardPile.UpgradableCardCount))] = AccessTools.Method(typeof(SharedHandSemantics), nameof(UpgradeCount)),
        [AccessTools.Method(typeof(CardPile), nameof(CardPile.GetCards))] = AccessTools.Method(typeof(SharedHandSemantics), nameof(GetCards))
    };
    internal static bool Active => HandSharingMode.Shared && (Entry.Ready || TestMode.IsOn) &&
        CombatManager.Instance.DebugOnlyGetState()?.Players.Count is >= 2 and <= PartyLimits.MaxPlayers;
    internal static bool PersonalEffectScope => RuleSession.Ready &&
        (RuleSession.Current.Advanced is {Enabled:true} custom ? custom.PersonalHandEffects :
            RuleSession.Current.IndependentCopies && RuleSession.Current.Piles != DrawPileMode.Common);
    internal static IReadOnlyList<CardModel> All()
    {
        var state = CombatManager.Instance.DebugOnlyGetState();
        return state == null ? Array.Empty<CardModel>() : state.Players
            .SelectMany(p => p.PlayerCombatState?.Hand.Cards ?? Array.Empty<CardModel>()).Distinct().ToList();
    }
    internal static bool IsPhysicalHand(CardPile pile) => pile.Type == PileType.Hand &&
        CombatManager.Instance.DebugOnlyGetState()?.Players.Any(p => ReferenceEquals(p.PlayerCombatState?.Hand, pile)) == true;
    internal static IReadOnlyList<CardModel> Cards(CardPile pile) => Active && !PersonalEffectScope && IsPhysicalHand(pile) ? All() : pile.Cards;
    // A card's own "hand" is the shared hand. An explicitly addressed ally's
    // hand remains that ally's hand: expanding it would change target-specific
    // mod mechanics, e.g. accepting our attack when the ally has none.
    internal static IReadOnlyList<CardModel> SourceCards(CardPile pile, CardModel? source) =>
        source == null || pile.Type == PileType.Hand &&
        !ReferenceEquals(source.Owner?.PlayerCombatState?.Hand, pile) ? pile.Cards : Cards(pile);
    internal static bool SourceEmpty(CardPile pile, CardModel? source) => SourceCards(pile, source).Count == 0;
    internal static int SourceUpgradeCount(CardPile pile, CardModel? source) => SourceCards(pile, source).Count(c => c.IsUpgradable);
    internal static IEnumerable<CardModel> SourceGetCards(Player player, PileType[] types, CardModel? source) =>
        types.SelectMany(t => SourceCards(CardPile.Get(t, player)!, source)).Distinct();
    internal static IEnumerable<CardModel> SourceCombatCards(PlayerCombatState player, CardModel? source)
    {
        if(source == null || !ReferenceEquals(source.Owner?.PlayerCombatState, player))return player.AllCards;
        // With independent piles, Apotheosis upgrades only the acting player's
        // cards. Other whole-hand effects can still see the shared hand.
        if(Active && RuleSession.Ready && RuleSession.Current.OnlyHands &&
            RuleSession.Current.IndependentCopies && source is Apotheosis)return player.AllCards;
        return CombatCards(player);
    }
    internal static bool IsEmpty(CardPile pile) => Cards(pile).Count == 0;
    internal static int UpgradeCount(CardPile pile) => Cards(pile).Count(c => c.IsUpgradable);
    internal static IEnumerable<CardModel> GetCards(Player player, params PileType[] types) =>
        types.SelectMany(t => Cards(CardPile.Get(t, player)!)).Distinct();
    // Shared combat storage is traversed once physically, but card effects
    // querying the whole shared combat deck must also see other players'
    // hands and contributions. Leave their pile/type filters intact (notably
    // FlakCannon excludes Exhaust). Engine hook/cleanup traversal stays owned.
    internal static IEnumerable<CardModel> CombatCards(PlayerCombatState player)
    {
        if(!Active || PersonalEffectScope)return player.AllCards;
        if(RuleSession.Current.Advanced is {Enabled:true})
            return player.AllCards.Concat(All()).Concat(CommonPiles.SharedCards()).Distinct();
        if(CommonPiles.Active && CombatManager.Instance.DebugOnlyGetState() is { } state)
            return state.Players.SelectMany(p => p.PlayerCombatState!.AllPiles).Distinct().SelectMany(p => p.Cards).Distinct();
        // Only the hand is shared in this mode. Never include a teammate's
        // draw, discard, exhaust, or play pile through this expansion.
        if(RuleSession.Ready && RuleSession.Current.OnlyHands)return player.AllCards.Concat(All()).Distinct();
        return player.AllCards;
    }

    internal static void Scan()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        if (_assemblyCount == assemblies.Length) return;
        _assemblyCount = assemblies.Length;
        FreeCostAudit.InstallPaymentAudit(assemblies);
        SharedHandFrameworkLimits.Scan(assemblies);
        SharedHandWatcherCompatibility.Scan(assemblies);
        foreach (var assembly in assemblies.Where(a => !a.IsDynamic && !Scanned.Contains(a)).ToArray())
        {
            CompatibilityScan.Attempt("assembly", assembly.GetName().Name!, () =>
            {
                if (assembly != typeof(CardModel).Assembly && !assembly.GetReferencedAssemblies().Any(a =>
                    a.Name == typeof(CardModel).Assembly.GetName().Name || a.Name == "BaseLib")) return;
                var types = CompatibilityScan.Types(assembly);
                SharedModSaveCompatibility.Scan(types);
                PrivateStockTransfers.Scan(types);
                FriendlyGreed.Scan(types);
                SharedEarring.Scan(types);
                SharedReactionHooks.Scan(assembly, types);
                foreach (var type in types)
                    CompatibilityScan.Attempt("hand-type", CompatibilityScan.Name(type), () => ScanType(type));
            });
            // Both successful and isolated failed discoveries are final for this
            // assembly instance. A newly loaded assembly still gets its own pass.
            Scanned.Add(assembly);
        }
    }
    private static void ScanType(Type type)
    {
        bool cardLogic = false;
        for (var owner = type; owner != null; owner = owner.DeclaringType)
            if (owner != typeof(CardModel) && typeof(CardModel).IsAssignableFrom(owner)) { cardLogic = true; break; }
        bool handSelection = type.DeclaringType == typeof(CardSelectCmd) &&
            (type.Name.StartsWith("<FromHand>") || type.Name.StartsWith("<FromHandForUpgrade>"));
        if (!cardLogic && !handSelection) return;
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            CompatibilityScan.Attempt("hand-method", CompatibilityScan.Name(method), () =>
            {
                // This card asks whether its own hand is empty; teammate hands
                // remain independently meaningful, just as for Unceasing Top.
                if(type==typeof(Restlessness)&&method.Name=="get_IsOnlyCardInHand")return;
                if (method.ContainsGenericParameters || method.IsAbstract || !CompatibilityScan.CallsAny(method, Redirect.Keys)) return;
                Patcher.Patch(method, transpiler: new HarmonyMethod(typeof(SharedHandSemantics), nameof(Transpiler)));
                PatchedMethods++;
                if (cardLogic) PatchedCardTypes.Add(type.DeclaringType ?? type);
            });
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        bool cardLogic = false;
        for(var owner=__originalMethod.DeclaringType;owner!=null;owner=owner.DeclaringType)
            if(owner!=typeof(CardModel)&&typeof(CardModel).IsAssignableFrom(owner)){cardLogic=true;break;}
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodBase method && Redirect.TryGetValue(method, out var replacement))
            {
                string? scoped = cardLogic && replacement.DeclaringType==typeof(SharedHandSemantics) ? replacement.Name switch
                {
                    nameof(Cards)=>nameof(SourceCards), nameof(IsEmpty)=>nameof(SourceEmpty),
                    nameof(UpgradeCount)=>nameof(SourceUpgradeCount), nameof(GetCards)=>nameof(SourceGetCards),
                    nameof(CombatCards)=>nameof(SourceCombatCards), _=>null
                } : null;
                // Flak Cannon's status-counting helper is static, so it has no
                // source CardModel to pass. Its explicit Player argument is the
                // actual user; both preview and execution use this same helper.
                if(__originalMethod.DeclaringType==typeof(FlakCannon) && __originalMethod.Name=="GetStatuses" &&
                    replacement.Name==nameof(CombatCards))scoped=null;
                if (replacement.DeclaringType == typeof(SharedEffectAutoPlay) || scoped != null)
                {
                    var source = SharedEffectAutoPlay.LoadSource(__originalMethod);
                    source[0].labels.AddRange(instruction.labels); instruction.labels.Clear();
                    source[0].blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                    foreach (var load in source) yield return load;
                }
                if(scoped!=null)replacement=AccessTools.Method(typeof(SharedHandSemantics),scoped);
                instruction.opcode = OpCodes.Call; instruction.operand = replacement;
            }
            yield return instruction;
        }
    }
}




