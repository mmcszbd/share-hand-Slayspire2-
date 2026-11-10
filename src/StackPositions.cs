using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

// A stack's seat belongs to the whole pile, never to its current top card.
// Keep all backend hand entries here, even while a holder is being dragged or
// temporarily moved into the selection tray. Only available views are shown.
internal sealed class StackPositions
{
    private sealed class Seat
    {
        internal string? Key;
        internal int Home;
        internal long Order;
        internal bool Stacked;
        internal HashSet<CardModel> Cards=new();

    }
    private readonly List<Seat> _seats=new();
    private readonly Dictionary<CardModel,long> _arrival=new();
    private long _next,_nextSeat;

    internal void Clear(){_seats.Clear();_arrival.Clear();_next=0;_nextSeat=0;}
    // Logical representatives and their order are stable while a stack loses
    // individual cards. Absolute geometry must not be cached: when an entire
    // group leaves, the remaining groups need the native compact fan again.
    internal void Hold() { }
    internal HandStacks.Frame Build(CardModel[][] hands,Vector2 size,HandDesign design,HashSet<CardModel>? available=null)
    {
        var result=new HandStacks.Frame();
        if(!SharedHandConfig.StackBasics)
        {
            Clear();
            foreach(var pair in LayoutDesign.Place(hands,size,design))
            {result.Slots[pair.Key]=pair.Value;result.Presented.Add(pair.Key);result.Order[pair.Key]=result.Order.Count;}
            return result;
        }
        var live=hands.SelectMany(h=>h).ToHashSet();
        var groups=new List<(string? key,int home,List<CardModel> cards)>();
        var keyed=new Dictionary<string,int>();
        for(int home=0;home<hands.Length;home++)foreach(var card in hands[home])
        {
            if(!_arrival.ContainsKey(card))_arrival[card]=_next++;
            string? key=HandStacks.Key(card);
            // Independent player zones must not lose their cards into a stack
            // in a different zone. Joined players still share the same stack.
            int root=LayoutDesign.Root(design,home);
            bool perPlayer=SharedHandConfig.HandExpansion&&root<design.Zones.Length&&design.Zones[root].Expand&&LayoutDesign.Members(design,root).Length>1;
            if(key!=null)key=(perPlayer?home:root)+"|"+key;
            if(key!=null&&keyed.TryGetValue(key,out int index))groups[index].cards.Add(card);
            else{if(key!=null)keyed[key]=groups.Count;groups.Add((key,home,new(){card}));}
        }
        var current=new List<(Seat seat,CardModel[] all,CardModel[] visible)>();
        var used=new HashSet<Seat>();
        // If an upgrade or enchantment splits a pile, keep the unchanged
        // majority in its old seat instead of giving that seat to the changed top.
        if(_seats.Any(s=>s.Stacked&&groups.Count(g=>g.cards.Any(s.Cards.Contains))>1))Hold();
        foreach(var group in groups.OrderByDescending(g=>g.key!=null&&_seats.Any(s=>s.Key==g.key))
            .ThenByDescending(g=>_seats.Select(s=>g.cards.Count(s.Cards.Contains)).DefaultIfEmpty().Max())
            .ThenBy(g=>g.cards.Min(c=>_arrival[c])))
        {
            var cards=group.cards.OrderBy(c=>_arrival[c]).ToArray();
            var seat=_seats.FirstOrDefault(s=>!used.Contains(s)&&group.key!=null&&s.Key==group.key)
                ??_seats.Where(s=>!used.Contains(s)&&cards.Any(s.Cards.Contains)).OrderByDescending(s=>cards.Count(s.Cards.Contains)).FirstOrDefault()
                ??new Seat{Home=group.home,Order=_nextSeat++};
            used.Add(seat);seat.Key=group.key;
            // A seat can survive a group split or a physical hand transfer.
            // Its position must follow the current group, not its old owner.
            seat.Home=group.home;
            bool stacked=group.key!=null&&(cards.Length>1||group.key.Contains("|basic:",StringComparison.Ordinal));
            seat.Stacked|=stacked;seat.Cards=cards.ToHashSet();
            current.Add((seat,cards,cards.Where(c=>available==null||available.Contains(c)).ToArray()));
        }
        _seats.Clear();_seats.AddRange(current.Select(g=>g.seat));
        var rows=Enumerable.Range(0,hands.Length).Select(_=>new List<CardModel>()).ToArray();
        foreach(var group in current.OrderBy(g=>g.seat.Order))rows[group.seat.Home].Add(group.all[0]);
        var positions=LayoutDesign.Place(rows.Select(r=>r.ToArray()).ToArray(),size,design);
        // The layout merges whole owner rows. Arrival order spans all owners
        // and can interleave those rows after draws/transforms. Number by the
        // actual placed row order while preserving each stack's stable seat.
        var displayOrder=positions.Keys.Select((card,index)=>(card,index)).ToDictionary(p=>p.card,p=>(long)p.index);
        foreach(var group in current.OrderBy(g=>g.seat.Order))
        {
            var seat=group.seat;
            var slot=positions[group.all[0]];
            foreach(var card in group.all){result.Slots[card]=slot;result.Order[card]=displayOrder[group.all[0]];}
            if(group.visible.Length==0)continue;
            var head=group.visible[0];result.Presented.Add(head);
            if(seat.Stacked&&seat.Key!=null)
                result.Heads[head]=new(seat.Key,seat.Home,group.visible,group.all.Length);
        }
        foreach(var card in _arrival.Keys.Where(c=>!live.Contains(c)).ToArray())_arrival.Remove(card);
        return result;
    }
}

