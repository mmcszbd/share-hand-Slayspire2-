using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;

namespace SharedHandPrototype;

// Keep native transformation/RNG/hooks and replace the ownership assumption at
// the boundary. Keep both ends of the view alive until native shine swaps the
// real NCard.Model; a periodic hand refresh must not free or duplicate it.
internal static class SharedCombatTransforms
{
    internal sealed class Lease
    {
        internal readonly (CardModel oldCard,CardModel newCard)[] Pairs;
        internal bool Released;
        internal Lease((CardModel,CardModel)[] pairs)
        {
            Pairs=pairs;
            foreach(var pair in pairs){Originals[pair.Item1]=this;Replacements[pair.Item2]=pair.Item1;}
        }
    }
    private static readonly Dictionary<CardModel,Lease> Originals=new();
    private static readonly Dictionary<CardModel,CardModel> Replacements=new();
    internal static bool KeepOriginal(CardModel card)=>Originals.ContainsKey(card);
    internal static bool WaitingReplacement(CardModel card)=>Replacements.ContainsKey(card);
    internal static bool Animate(CardModel card)
    {
        if(LocalContext.IsMine(card))return true;
        return Replacements.TryGetValue(card,out var original)&&
            NPlayerHand.Instance?.GetCardHolder(original)?.CardNode is NCard node&&
            GodotObject.IsInstanceValid(node)&&node.IsVisibleInTree();
    }
    internal static void Release(Lease? lease)
    {
        if(lease==null||lease.Released)return;
        lease.Released=true;
        foreach(var (oldCard,newCard) in lease.Pairs)
        {
            Originals.Remove(oldCard);Replacements.Remove(newCard);
            // If a native effect interrupted the animation, rebuild only from
            // actual backend state. Never resurrect the removed original card.
            var hand=NPlayerHand.Instance;
            if(hand==null||!hand.IsNodeReady())continue;
            if(oldCard.Pile?.Type!=PileType.Hand&&hand.GetCardHolder(oldCard)!=null)hand.Remove(oldCard);
            if(newCard.Pile?.Type==PileType.Hand&&hand.GetCardHolder(newCard)==null&&!SharedSelection.InSelectionView(newCard))
            {
                var node=NCard.Create(newCard);hand.Add(node);node.UpdateVisuals(PileType.Hand,CardPreviewMode.Normal);
            }
        }
        SharedPanel.MarkDirty();
    }
    internal static async Task<IEnumerable<CardPileAddResult>> Finish(Task<IEnumerable<CardPileAddResult>> task,Lease lease)
    {try{return await task;}finally{Release(lease);}}
}

[HarmonyPatch(typeof(CardCmd),nameof(CardCmd.Transform),new[]{typeof(IEnumerable<CardTransformation>),typeof(MegaCrit.Sts2.Core.Random.Rng),typeof(CardPreviewStyle)})]
internal static class SharedTransformBoundary
{
    private static void Prefix(ref IEnumerable<CardTransformation> transformations,MegaCrit.Sts2.Core.Random.Rng? rng,out SharedCombatTransforms.Lease? __state)
    {
        __state=null;
        if(!SharedHandSemantics.Active||CombatManager.Instance.IsOverOrEnding)return;
        var originals=transformations.ToArray();
        if(originals.Length==0||originals.Any(t=>t.Original.Pile?.IsCombatPile!=true))return;
        // Validate the whole batch before native code removes its first card.
        if(originals.Select(t=>t.Original).Distinct().Count()!=originals.Length)
            throw new InvalidOperationException("A combat transformation batch contains the same card twice.");
        foreach(var t in originals){t.Original.AssertMutable();if(!t.Original.IsTransformable)throw new InvalidOperationException("Card is not transformable.");}
        var pairs=new List<(CardModel,CardModel)>();
        var prepared=new List<CardTransformation>();
        foreach(var t in originals)
        {
            var replacement=t.GetReplacement(rng)??throw new InvalidOperationException("Transformation has no replacement.");
            replacement.AssertMutable();
            if(replacement.Pile!=null)throw new InvalidOperationException("Transformation replacement is already in a pile.");
            if(replacement.Owner!=t.Original.Owner)replacement.GiveToAnotherPlayer(t.Original.Owner);
            pairs.Add((t.Original,replacement));prepared.Add(new CardTransformation(t.Original,replacement));
        }
        __state=new SharedCombatTransforms.Lease(pairs.ToArray());transformations=prepared;
    }
    private static void Postfix(ref Task<IEnumerable<CardPileAddResult>> __result,SharedCombatTransforms.Lease? __state)
    {if(__state!=null)__result=SharedCombatTransforms.Finish(__result,__state);}
    private static void Finalizer(Exception? __exception,SharedCombatTransforms.Lease? __state)
    {if(__exception!=null)SharedCombatTransforms.Release(__state);}
}

[HarmonyPatch]
internal static class SharedNativeTransformVisuals
{
    private static MethodBase TargetMethod()=>AccessTools.Method(typeof(CardCmd),nameof(CardCmd.Transform),
        new[]{typeof(IEnumerable<CardTransformation>),typeof(MegaCrit.Sts2.Core.Random.Rng),typeof(CardPreviewStyle)})
        .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType.GetMethod("MoveNext",BindingFlags.NonPublic|BindingFlags.Instance)!;
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original=AccessTools.Method(typeof(LocalContext),nameof(LocalContext.IsMine),new[]{typeof(CardModel)});
        foreach(var instruction in instructions)
        {
            if(instruction.Calls(original)){instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(SharedCombatTransforms),nameof(SharedCombatTransforms.Animate));}
            yield return instruction;
        }
    }
}

