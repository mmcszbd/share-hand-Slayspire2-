using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;

namespace SharedHandPrototype;
internal static class DeckContributors
{
    private const SortingOrders ContributorSort=(SortingOrders)10068;
    private sealed class Filter {internal Player? Selected;internal OptionButton Choices=null!;}
    private static readonly ConditionalWeakTable<NCardGrid,Filter> Filters=new();
    internal static ulong Provider(CardModel card)
    {
        string identity=SharedDeckSync.Identity.Get(card.DeckVersion??card);
        var prefix=identity.Split(':')[0];
        return ulong.TryParse(prefix,NumberStyles.None,CultureInfo.InvariantCulture,out var id)&&card.Owner.RunState.Players.Any(p=>p.NetId==id)?id:card.Owner.NetId;
    }
    internal static void Add(NDeckViewScreen screen)
    {
        var player=(Player)AccessTools.Field(typeof(NDeckViewScreen),"_player").GetValue(screen)!;
        if(player.RunState.Players.Count is not (>=2 and <=PartyLimits.MaxPlayers)||!RuleSession.Current.ShareDeck)return;
        var grid=(NCardGrid)AccessTools.Field(typeof(NDeckViewScreen),"_grid").GetValue(screen)!;
        if(Filters.TryGetValue(grid,out _))return;
        var filter=new Filter();Filters.Add(grid,filter);
        var algorithms=(Dictionary<SortingOrders,Func<CardModel,CardModel,int>>)AccessTools.Property(typeof(NCardGrid),"SortingAlgorithms").GetValue(grid)!;
        algorithms[ContributorSort]=(a,b)=>(Provider(a)==filter.Selected?.NetId?0:1).CompareTo(Provider(b)==filter.Selected?.NetId?0:1);
        var sorter=screen.GetNode<Control>("%AlphabeticalSorter");
        var choices=new OptionButton{Name="SharedDeckContributor",CustomMinimumSize=new Vector2(248,52),Size=new Vector2(248,52)};
        filter.Choices=choices;choices.AddItem("共享牌组");
        var players=player.RunState.Players.ToArray();
        foreach(var p in players)choices.AddItem(p.Character.Title.GetFormattedText()+(players.Count(q=>q.Character.Id==p.Character.Id)>1?"（P"+(Array.IndexOf(players,p)+1)+"）":""));
        choices.AddThemeFontSizeOverride("font_size",26);
        choices.ItemSelected+=index=>{filter.Selected=index==0?null:players[(int)index-1];AccessTools.Method(typeof(NDeckViewScreen),"DisplayCards").Invoke(screen,null);};
        // Let the original sort buttons and the added selector share the same
        // strip. Their anchors are untouched; size/positions only shrink if
        // the new selector would otherwise extend beyond the screen edge.
        sorter.GetParent().AddChild(choices);
        void Place()
        {
            if(!GodotObject.IsInstanceValid(choices)||!GodotObject.IsInstanceValid(sorter))return;
            choices.Position=new Vector2(sorter.Position.X+sorter.Size.X+14,sorter.Position.Y+(sorter.Size.Y-52)/2);
            float overflow=choices.GetGlobalRect().End.X-(screen.GetViewportRect().Size.X-28);
            if(overflow>0)
            {
                foreach(string name in new[]{"%ObtainedSorter","%CardTypeSorter","%CostSorter","%AlphabeticalSorter"})
                {var button=screen.GetNode<Control>(name);button.Position-=new Vector2(overflow,0);}
                choices.Position-=new Vector2(overflow,0);
            }
        }
        Callable.From(Place).CallDeferred();
    }
    internal static void Sort(NCardGrid grid,ref List<SortingOrders> priority)
    {
        if(!Filters.TryGetValue(grid,out var filter)||filter.Selected==null)return;
        priority=new List<SortingOrders>(priority);priority.Insert(0,ContributorSort);
    }
}
[HarmonyPatch(typeof(NDeckViewScreen),"_Ready")]
internal static class DeckContributorUiPatch {private static void Postfix(NDeckViewScreen __instance)=>DeckContributors.Add(__instance);}
[HarmonyPatch(typeof(NCardGrid),nameof(NCardGrid.SetCards))]
internal static class DeckContributorSortingPatch
{private static void Prefix(NCardGrid __instance,ref List<SortingOrders> sortingPriority)=>DeckContributors.Sort(__instance,ref sortingPriority);}

