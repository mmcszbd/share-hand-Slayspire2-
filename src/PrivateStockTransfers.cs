using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

// Native retrieval assumes a pile's cards already belong to its user. Shared
// stock breaks that assumption. Resolve implicit hand destinations from the
// card, potion, relic, or power that selected them; explicit destinations stay native.
internal static class PrivateStockTransfers
{
    private static readonly Harmony Patcher=new(Entry.ModId+".private-stock-recipient");
    private static readonly Dictionary<MethodBase,MethodInfo> Calls=new()
    {
        [AccessTools.Method(typeof(CardPileCmd),nameof(CardPileCmd.Add),new[]{typeof(CardModel),typeof(PileType),typeof(CardPilePosition),typeof(AbstractModel),typeof(bool)})]=AccessTools.Method(typeof(PrivateStockTransfers),nameof(AddOne)),
        [AccessTools.Method(typeof(CardPileCmd),nameof(CardPileCmd.Add),new[]{typeof(IEnumerable<CardModel>),typeof(PileType),typeof(CardPilePosition),typeof(AbstractModel),typeof(bool)})]=AccessTools.Method(typeof(PrivateStockTransfers),nameof(AddMany))
    };
    private static FieldInfo? SourceField(Type type)=>AccessTools.Field(type,"<>4__this");
    private static bool IsEffect(Type type)=>typeof(CardModel).IsAssignableFrom(type)||typeof(PotionModel).IsAssignableFrom(type)||typeof(RelicModel).IsAssignableFrom(type)||typeof(PowerModel).IsAssignableFrom(type);
    internal static void Scan(Type[] types)
    {
        foreach(var type in types)
        {
            bool direct=IsEffect(type),captured=SourceField(type) is {} field&&IsEffect(field.FieldType);
            if(!direct&&!captured)continue;
            foreach(var method in type.GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
                CompatibilityScan.Attempt("stock-recipient",CompatibilityScan.Name(method),()=>
                {
                    if(method.IsAbstract||method.ContainsGenericParameters||!CompatibilityScan.CallsAny(method,Calls.Keys))return;
                    Patcher.Patch(method,transpiler:new HarmonyMethod(typeof(PrivateStockTransfers),nameof(Transpiler)));
                });
        }
    }
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
    {
        foreach(var instruction in instructions)
        {
            if(instruction.operand is MethodBase call&&Calls.TryGetValue(call,out var replacement))
            {
                var load=new CodeInstruction(OpCodes.Ldarg_0);load.labels.AddRange(instruction.labels);instruction.labels.Clear();load.blocks.AddRange(instruction.blocks);instruction.blocks.Clear();yield return load;
                if(!IsEffect(__originalMethod.DeclaringType!))yield return new CodeInstruction(OpCodes.Ldfld,SourceField(__originalMethod.DeclaringType!)!);
                instruction.opcode=OpCodes.Call;instruction.operand=replacement;
            }
            yield return instruction;
        }
    }
    private sealed record Selected(Player Player,CardPile Pile);
    private static ConditionalWeakTable<AbstractModel,Dictionary<CardModel,Selected>> Choices=new();
    internal static void ResetChoices()=>Choices=new();
    internal static async Task<IEnumerable<CardModel>> RememberChoice(Task<IEnumerable<CardModel>> task,AbstractModel source,CardPile pile,Player chooser)
    {
        var chosen=(await task).ToArray();
        if(CommonPiles.Active)
            foreach(var card in chosen.Where(c=>ReferenceEquals(c.Pile,pile)))
                Choices.GetOrCreateValue(source)[card]=new Selected(chooser,pile);
        return chosen;
    }
    private static Player? ChosenRecipient(AbstractModel source,CardModel card)
    {
        if(!Choices.TryGetValue(source,out var choices)||!choices.Remove(card,out var choice))return null;
        return ReferenceEquals(card.Pile,choice.Pile)?choice.Player:null;
    }
    private static Player? Recipient(AbstractModel source)=>source switch {CardModel card=>card.Owner,PotionModel potion=>potion.Owner,RelicModel relic=>relic.Owner,PowerModel power=>power.Owner.Player,_=>null};
    private static bool SharedStockHand(CardModel card,PileType type)=>type==PileType.Hand&&CommonPiles.Find(card)!=null;
    private static Player? Destination(CardModel card,AbstractModel source)=>ChosenRecipient(source,card)??
        ((HandSharingMode.Private||source is RelicModel or PowerModel)?Recipient(source):null);
    internal static Task<CardPileAddResult> AddOne(CardModel card,PileType type,CardPilePosition position,AbstractModel? clonedBy,bool skipVisuals,AbstractModel source)
    {
        if(SharedStockHand(card,type)&&Destination(card,source) is {} player)
            return CardPileCmd.Add(card,player.PlayerCombatState!.Hand,position,clonedBy,skipVisuals);
        return CardPileCmd.Add(card,type,position,clonedBy,skipVisuals);
    }
    internal static Task<IReadOnlyList<CardPileAddResult>> AddMany(IEnumerable<CardModel> cards,PileType type,CardPilePosition position,AbstractModel? clonedBy,bool skipVisuals,AbstractModel source)
    {
        // Preserve vanilla's exact enumeration and timing outside shared stock,
        // particularly in singleplayer and independently owned piles.
        if(!CommonPiles.Active||type!=PileType.Hand)return CardPileCmd.Add(cards,type,position,clonedBy,skipVisuals);
        var snapshot=cards.ToArray();
        if(snapshot.Length==0||!snapshot.All(c=>SharedStockHand(c,type)))return CardPileCmd.Add(snapshot,type,position,clonedBy,skipVisuals);
        var receivers=snapshot.Select(c=>Destination(c,source)??c.Owner).ToArray();
        return AddInOrder(snapshot,receivers,position,clonedBy,skipVisuals);
    }
    private static async Task<IReadOnlyList<CardPileAddResult>> AddInOrder(CardModel[] cards,Player[] receivers,CardPilePosition position,AbstractModel? clonedBy,bool skipVisuals)
    {
        var results=new List<CardPileAddResult>();
        for(int start=0;start<cards.Length;)
        {
            int end=start+1;
            while(end<cards.Length&&receivers[end]==receivers[start])end++;
            results.AddRange(await CardPileCmd.Add(cards.Skip(start).Take(end-start).ToArray(),receivers[start].PlayerCombatState!.Hand,position,clonedBy,skipVisuals,true));
            start=end;
        }
        return results;
    }
}

[HarmonyPatch(typeof(CardSelectCmd),nameof(CardSelectCmd.FromCombatPile),new[]{typeof(PlayerChoiceContext),typeof(CardPile),typeof(Player),typeof(CardSelectorPrefs),typeof(Func<CardModel,bool>)})]
internal static class SharedStockChoiceRecipientPatch
{
    private static void Postfix(PlayerChoiceContext context,CardPile pile,Player player,ref Task<IEnumerable<CardModel>> __result)
    {
        if(CommonPiles.Active&&context.LastInvolvedModel is {} source)
            __result=PrivateStockTransfers.RememberChoice(__result,source,pile,player);
    }
}

