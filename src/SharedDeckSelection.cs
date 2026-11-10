using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.CardSelection;
using System.Threading.Tasks;

namespace SharedHandPrototype;

// These native screens snapshot the deck once. Mirrored removal or upgrade
// can invalidate that snapshot while another player is still choosing.
internal static class SharedDeckSelection
{
    private sealed class Watch
    {
        internal readonly NCardGridSelectionScreen Screen;
        internal readonly Dictionary<CardModel,int> Initial;
        private readonly SceneTree _tree;
        private ulong _last;
        internal Watch(NCardGridSelectionScreen screen)
        {
            Screen=screen;Initial=Cards(screen).ToDictionary(c=>c,c=>c.CurrentUpgradeLevel);_tree=screen.GetTree();
            _tree.ProcessFrame+=Tick;
            screen.TreeExiting+=()=>{_tree.ProcessFrame-=Tick;Watches.Remove(screen);};
        }
        internal bool Valid(CardModel card)=>Initial.TryGetValue(card,out int level)&&
            !card.HasBeenRemovedFromState&&card.Owner.Deck.Cards.Contains(card)&&
            (Screen is not NDeckUpgradeSelectScreen||card.IsUpgradable&&card.CurrentUpgradeLevel==level);
        private void Tick(){if(Time.GetTicksMsec()-_last<80)return;_last=Time.GetTicksMsec();Refresh();}
        internal void Refresh()
        {
            if(!GodotObject.IsInstanceValid(Screen)||Screen.IsQueuedForDeletion())return;
            var old=Cards(Screen);var next=old.Where(Valid).ToArray();
            if(next.Length==old.Count)return;
            var selected=(HashSet<CardModel>)AccessTools.Field(Screen.GetType(),"_selectedCards").GetValue(Screen)!;
            if(selected.Count>0)AccessTools.Method(Screen.GetType(),"CancelSelection").Invoke(Screen,new object?[]{null});
            AccessTools.Field(typeof(NCardGridSelectionScreen),"_cards").SetValue(Screen,next);
            var grid=(NCardGrid)AccessTools.Field(typeof(NCardGridSelectionScreen),"_grid").GetValue(Screen)!;
            grid.SetCards(next,PileType.None,new(){MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary.SortingOrders.Ascending});
            if(next.Length==0)
            {
                var completion=(System.Threading.Tasks.TaskCompletionSource<IEnumerable<CardModel>>)AccessTools.Field(typeof(NCardGridSelectionScreen),"_completionSource").GetValue(Screen)!;
                completion.TrySetResult(Array.Empty<CardModel>());NOverlayStack.Instance.Remove(Screen);
            }
        }
    }
    private static readonly Dictionary<NCardGridSelectionScreen,Watch> Watches=new();
    internal static CardModel[] SelectedCards()=>Watches.Values.Where(w=>GodotObject.IsInstanceValid(w.Screen)&&w.Screen.IsVisibleInTree()&&!w.Screen.IsQueuedForDeletion())
        .SelectMany(w=>((HashSet<CardModel>)AccessTools.Field(w.Screen.GetType(),"_selectedCards").GetValue(w.Screen)!).Where(w.Valid)).Distinct().ToArray();
    internal static NCardGridSelectionScreen[] Screens()=>Watches.Keys.Where(s=>GodotObject.IsInstanceValid(s)&&s.IsVisibleInTree()&&!s.IsQueuedForDeletion()).ToArray();
    private static IReadOnlyList<CardModel> Cards(NCardGridSelectionScreen screen)=>(IReadOnlyList<CardModel>)AccessTools.Field(typeof(NCardGridSelectionScreen),"_cards").GetValue(screen)!;
    internal static void Open(NCardGridSelectionScreen screen)
    {
        var cards=Cards(screen);
        if(cards.Count==0||!SharedDeckSync.Enabled(cards[0].Owner.RunState)||Watches.ContainsKey(screen))return;
        Watches[screen]=new Watch(screen);
    }
    internal static bool Click(NCardGridSelectionScreen screen,CardModel card)
    {
        if(!Watches.TryGetValue(screen,out var watch))return true;
        bool valid=watch.Valid(card);watch.Refresh();return valid&&!screen.IsQueuedForDeletion();
    }
    internal static bool Confirm(NCardGridSelectionScreen screen)
    {
        if(!Watches.TryGetValue(screen,out var watch))return true;
        watch.Refresh();
        return !screen.IsQueuedForDeletion()&&((HashSet<CardModel>)AccessTools.Field(screen.GetType(),"_selectedCards").GetValue(screen)!).Count>0;
    }
}
[HarmonyPatch]
internal static class SharedDeckSelectionOpenPatch
{
    private static IEnumerable<MethodBase> TargetMethods()=>new[]{AccessTools.Method(typeof(NDeckUpgradeSelectScreen),"_Ready"),AccessTools.Method(typeof(NDeckCardSelectScreen),"_Ready")};
    private static void Postfix(NCardGridSelectionScreen __instance)=>SharedDeckSelection.Open(__instance);
}
[HarmonyPatch]
internal static class SharedDeckSelectionClickPatch
{
    private static IEnumerable<MethodBase> TargetMethods()=>new[]{AccessTools.Method(typeof(NDeckUpgradeSelectScreen),"OnCardClicked"),AccessTools.Method(typeof(NDeckCardSelectScreen),"OnCardClicked")};
    private static bool Prefix(NCardGridSelectionScreen __instance,CardModel card)=>SharedDeckSelection.Click(__instance,card);
}
[HarmonyPatch]
internal static class SharedDeckSelectionConfirmPatch
{
    private static IEnumerable<MethodBase> TargetMethods()=>new[]{AccessTools.Method(typeof(NDeckUpgradeSelectScreen),"ConfirmSelection"),AccessTools.Method(typeof(NDeckCardSelectScreen),"ConfirmSelection")};
    private static bool Prefix(NCardGridSelectionScreen __instance)=>SharedDeckSelection.Confirm(__instance);
}

// Recheck after the synchronized choice returns, before the merchant removes
// gold. A stale result may already have left the local preview screen.
[HarmonyPatch(typeof(CardSelectCmd),nameof(CardSelectCmd.FromDeckForRemoval))]
internal static class SharedDeckRemovalResultPatch
{
    private static void Postfix(Player player,ref Task<IEnumerable<CardModel>> __result)
    {if(SharedDeckSync.Enabled(player.RunState))__result=Valid(__result,player);}
    private static async Task<IEnumerable<CardModel>> Valid(Task<IEnumerable<CardModel>> task,Player player)
        =>(await task).Where(c=>!c.HasBeenRemovedFromState&&player.Deck.Cards.Contains(c)).ToArray();
}

