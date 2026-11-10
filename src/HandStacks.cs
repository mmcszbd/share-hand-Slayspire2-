using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Collections;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Context;

namespace SharedHandPrototype;

// Collapse views only. All original holders, card models, network IDs and
// backend hands remain intact. Native ActiveHolders excludes the hidden backs,
// so keyboard/controller focus follows exactly the visible representatives.
internal static class HandStacks
{
    internal sealed record Stack(string Key,int Home,CardModel[] Cards,int? Total=null)
    {internal int Quantity=>Total??Cards.Length;}
    internal sealed class Frame
    {
        internal readonly Dictionary<CardModel,HandLayouts.Slot> Slots=new();
        internal readonly HashSet<CardModel> Presented=new();
        internal readonly Dictionary<CardModel,Stack> Heads=new();
        internal readonly Dictionary<CardModel,long> Order=new();
    }
    private static readonly HashSet<NCardHolder> Hidden=new();
    private static readonly Dictionary<NCardHolder,StackAdornment> Adornments=new();
    private static NPlayerHand? _hand;
    private static StackLink? _link;
    private static StackBackdrop? _backdrop;
    private static readonly StackPositions Positions=new();
    private static CombatState? _state;
    private static Frame? _frame;
    private static int _mode=-1;
    private static readonly JsonSerializerOptions StateJson=new(){IncludeFields=true};
    private static object? SavedState(CardModel card)
    {
        var properties=card.ToSerializable().Props;
        // This is a fresh save snapshot. Storage identity does not change what
        // a card does; retaining it would prevent synchronized starter copies
        // from ever stacking. Preserve every other mod's saved property.
        properties?.strings?.RemoveAll(p=>p.name==SharedDeckSync.Identity.Name);
        return properties;
    }
    internal static string? Key(CardModel card)
    {
        if(!HandSharingMode.Shared||!SharedHandConfig.StackBasics)return null;
        string? category=null;
        if(card.IsBasicStrikeOrDefend)
        {
            if(card.Tags.Contains(CardTag.Strike))category="basic:strike";
            else if(card.Tags.Contains(CardTag.Defend))category="basic:defend";
        }
        category??=SharedHandConfig.StackSameName?"name:"+(card.TitleLocString.Exists()?card.TitleLocString.GetFormattedText():card.Title):null;
        if(category==null)return null;
        try
        {
            var actor=card.CombatState==null?null:LocalContext.GetMe(card.CombatState);
            string State()=>JsonSerializer.Serialize(new{
                card.CurrentUpgradeLevel,energy=card.EnergyCost.GetWithModifiers(CostModifiers.All),localEnergy=card.EnergyCost.GetWithModifiers(CostModifiers.Local),card.EnergyCost.CostsX,
                star=card.GetStarCostWithModifiers(),card.BaseStarCost,card.HasStarCostX,card.ExhaustOnNextPlay,card.ShouldRetainThisTurn,card.IsSlyThisTurn,
                keywords=card.Keywords.OrderBy(k=>k).ToArray(),tags=card.Tags.OrderBy(t=>t).ToArray(),
                vars=card.DynamicVars.OrderBy(v=>v.Key).Select(v=>new{v.Key,v.Value.BaseValue,v.Value.EnchantedValue}).ToArray(),
                props=SavedState(card),enchantment=card.Enchantment?.ToSerializable(),
                affliction=card.Affliction==null?null:new{type=card.Affliction.Id.ToString(),card.Affliction.Amount,state=ExtraState(card.Affliction,typeof(AfflictionModel))},
                temporaryCost=ExtraState(card.EnergyCost,typeof(object),"_card"),
                temporaryStars=Value(AccessTools.Field(typeof(CardModel),"_temporaryStarCosts").GetValue(card)),
                extra=ExtraState(card,typeof(CardModel)),description=card.GetDescriptionForPile(PileType.Hand)
            },StateJson);
            return category+"|"+(actor==null?State():CardEvaluation.As(card,actor,State));
        }
        // Unknown mod state is kept separate rather than hiding differences.
        catch(Exception){return null;}
    }
    private static object? Value(object? value,int depth=0)
    {
        if(value==null)return null;
        var type=value.GetType();
        if(type.IsPrimitive||type.IsEnum||value is string or decimal)return value;
        if(depth>4)throw new NotSupportedException();
        if(value is IEnumerable list)return list.Cast<object?>().Select(v=>Value(v,depth+1)).ToArray();
        if(type.IsValueType||type.Namespace=="MegaCrit.Sts2.Core.Entities.Cards")return type.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).ToDictionary(f=>f.Name,f=>Value(f.GetValue(value),depth+1));
        throw new NotSupportedException();
    }
    private static object ExtraState(object value,Type stop,string? skip=null)
    {
        var result=new Dictionary<string,object?>();
        for(var type=value.GetType();type!=null&&type!=stop;type=type.BaseType)
            foreach(var field in type.GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly))
                if(field.Name!=skip)result[type.FullName+":"+field.Name]=Value(field.GetValue(value));
        return result;
    }
    internal static Frame Build(CardModel[][] hands,Vector2 size,HandDesign design,bool selection=false)
        =>new StackPositions().Build(hands,size,design);
    internal static void Reveal(NCardHolder holder)
    {
        if(Hidden.Remove(holder)&&GodotObject.IsInstanceValid(holder))holder.Show();
    }
    internal static void Restore()
    {
        foreach(var holder in Hidden.ToArray())Reveal(holder);
        foreach(var adornment in Adornments.Values)if(GodotObject.IsInstanceValid(adornment))adornment.Hide();
        if(GodotObject.IsInstanceValid(_link))_link!.Hide();
        if(GodotObject.IsInstanceValid(_backdrop))_backdrop!.Hide();
    }
    internal static void Prepare(NPlayerHand hand)
    {
        if(_hand==hand)return;
        Restore();Hidden.Clear();Adornments.Clear();Positions.Clear();_state=null;_frame=null;_mode=-1;_hand=hand;_link=null;_backdrop=null;
    }
    internal static void Selection(NPlayerHand hand){Prepare(hand);Positions.Hold();}
    internal static Frame Plan(NPlayerHand hand,CombatState state,Vector2 size)
    {
        Prepare(hand);
        int mode=(SharedHandConfig.StackBasics?1:0)+(SharedHandConfig.StackSameName?2:0);
        if(_state!=state||_mode!=mode){Positions.Clear();_state=state;_mode=mode;}
        // Decorative siblings use InternalMode.Back, so native GetIndex and
        // child enumeration always see only real card holders, even during Add.
        var local=LocalContext.GetMe(state);
        // A dragged/queued holder has a different parent. It must never become
        // the hidden head of the remaining cards while native targeting awaits.
        var available=hand.CardHolderContainer.GetChildren().OfType<NHandCardHolder>()
            .Where(h=>h.CardNode?.Model!=null&&!h.IsQueuedForDeletion()&&(!hand.IsInCardSelection||SharedSelection.Allowed(h.CardModel))).Select(h=>h.CardModel).ToHashSet();
        var hands=state.Players.OrderBy(p=>p==local?0:1).Select(p=>p.PlayerCombatState!.Hand.Cards.ToArray()).ToArray();
        var frame=Positions.Build(hands,size,LayoutDesign.ForPlayers(LayoutDesign.Current,state.Players.Count),available);_frame=frame;
        if(!hand.IsInCardSelection && local != null)
        foreach(var pair in frame.Heads.ToArray())
        {
            if(!Unavailable(pair.Key,local)) continue;
            var next=pair.Value.Cards.FirstOrDefault(c=>!Unavailable(c,local));
            if(next==null) continue;
            frame.Presented.Remove(pair.Key); frame.Heads.Remove(pair.Key);
            frame.Presented.Add(next);
            frame.Heads[next]=pair.Value with { Cards=new[]{next}.Concat(pair.Value.Cards.Where(c=>c!=next)).ToArray() };
        }
        // Targeting and queueing only detach views. Keep counting every
        // backend hand entry until the action actually moves the card.
        // The selection tray deliberately has its own separate counters.
        if(hand.IsInCardSelection)foreach(var pair in frame.Heads.ToArray())
            frame.Heads[pair.Key]=pair.Value with{Total=pair.Value.Cards.Length};
        if(!SharedHandConfig.StackBasics)Restore();
        else foreach(var holder in hand.CardHolderContainer.GetChildren().OfType<NHandCardHolder>())
        {
            if(!frame.Slots.ContainsKey(holder.CardModel))continue;
            if(frame.Presented.Contains(holder.CardModel))Reveal(holder);
            else{Hidden.Add(holder);holder.Hide();}
        }
        SortScene(hand,frame);
        return frame;
    }
    // Godot resolves overlapping controls using scene order, independently
    // of ActiveHolders and the coordinates assigned by our custom layout.
    // Keep the actual holder nodes in the same order as the visible seats.
    // Preserve relative order for hidden cards in the same stack and for any
    // temporarily unplanned holder. Internal decorative children are excluded.
    internal static void SortScene(NPlayerHand hand,Frame frame)
    {
        var container=hand.CardHolderContainer;
        var holders=container.GetChildren().OfType<NHandCardHolder>().ToArray();
        var sorted=SceneOrder(frame,holders);
        if(holders.SequenceEqual(sorted))return;
        SortHolderNodes(container,sorted);
    }
    private static NHandCardHolder[] SceneOrder(Frame frame,IEnumerable<NHandCardHolder> holders)
        =>holders.OrderBy(h=>frame.Slots.TryGetValue(h.CardModel,out var slot)?-slot.Layer:0)
            .ThenBy(h=>frame.Order.GetValueOrDefault(h.CardModel,long.MaxValue)).ToArray();
    internal static IReadOnlyList<NHandCardHolder> SceneOrdered(NPlayerHand hand,IReadOnlyList<NHandCardHolder> holders)
        =>hand==_hand&&_frame!=null?SceneOrder(_frame,holders):holders;
    internal static void SortHolderNodes(Node container,NHandCardHolder[] sorted)
    {
        // Preserve positions of any ordinary children added by other mods.
        var seats=container.GetChildren().Select((node,index)=>(node,index))
            .Where(x=>x.node is NHandCardHolder).Select(x=>x.index).ToArray();
        if(seats.Length!=sorted.Length)return;
        for(int i=0;i<sorted.Length;i++)container.MoveChild(sorted[i],seats[i]);
    }
    private static bool Unavailable(CardModel card,Player actor) =>
        SelectionHints.IsLocked(card,actor) || TeammatePlayLimit.Blocked(card,actor) || SharedSmog.Blocked(card,actor);
    internal static void PickAvailable(NPlayerHand hand,ref NHandCardHolder holder)
    {
        if(hand.IsInCardSelection || !SharedHandConfig.StackBasics || !HandLayouts.Active(hand) || _frame == null ||
            holder.CardModel.CombatState is not {} state || LocalContext.GetMe(state) is not {} actor ||
            !Unavailable(holder.CardModel,actor)) return;
        var model=holder.CardModel;
        var stack=_frame.Heads.Values.FirstOrDefault(s=>s.Cards.Contains(model));
        if(stack==null) return;
        var next=stack.Cards.Where(c=>!Unavailable(c,actor)).Select(c=>hand.GetCardHolder(c)).OfType<NHandCardHolder>()
            .FirstOrDefault(h=>h.GetParent()==hand.CardHolderContainer&&!h.IsQueuedForDeletion());
        if(next==null) return;
        // A heartbeat can land between the last layout frame and the click.
        // Resolve that click against live locks too, retaining the same seat.
        Reveal(next); holder=next;
    }
    internal static void Decorate(NPlayerHand hand,CombatState state,Frame frame,Vector2 size,Func<Vector2,Vector2> atRest)
    {
        foreach(var pair in Adornments.ToArray())
        {
            if(!GodotObject.IsInstanceValid(pair.Key)||!GodotObject.IsInstanceValid(pair.Value)){Adornments.Remove(pair.Key);continue;}
            pair.Value.Hide();
        }
        if(!GodotObject.IsInstanceValid(_backdrop))
        {_backdrop=new StackBackdrop{Name="SharedStackBackdrop",MouseFilter=Control.MouseFilterEnum.Ignore,ShowBehindParent=true};hand.CardHolderContainer.AddChild(_backdrop,false,Node.InternalMode.Back);}
        _backdrop!.Begin(state.Players.SelectMany(p=>p.PlayerCombatState!.Hand.Cards).ToHashSet());
        if(SharedHandConfig.StackBasics)
        foreach(var pair in frame.Heads)
        {
            if(hand.GetCardHolder(pair.Key) is not NHandCardHolder holder||holder.GetParent()!=hand.CardHolderContainer||!holder.Visible)continue;
            Adorn(holder,pair.Value);
            _backdrop.Add(holder,pair.Value,Adornments[holder].Counter,null);
        }
        if(hand.IsInCardSelection&&AccessTools.Field(typeof(NPlayerHand),"_selectedHandCardContainer").GetValue(hand) is NSelectedHandCardContainer selected)Selected(selected);
        _backdrop.Finish();
        if(!GodotObject.IsInstanceValid(_link))
        {_link=new StackLink{Name="SharedStackLink",MouseFilter=Control.MouseFilterEnum.Ignore,ShowBehindParent=true};hand.CardHolderContainer.AddChild(_link,false,Node.InternalMode.Back);}
        _link!.Clear();_link.Hide();
        if(!SharedHandConfig.StackBasics||hand.IsInCardSelection||hand.InCardPlay)return;
        var local=LocalContext.GetMe(state);var members=state.Players.OrderBy(p=>p==local?0:1).ToArray();
        var design=LayoutDesign.ForPlayers(LayoutDesign.Current,state.Players.Count);
        for(int g=1;g<members.Length;g++)
        {
            int zone=LayoutDesign.Root(design,g);
            if(zone==0||members[g].PlayerCombatState!.Hand.IsEmpty||frame.Presented.Any(c=>frame.Slots[c].Group==zone))continue;
            if(!members[g].PlayerCombatState!.Hand.Cards.Any(c=>frame.Slots.TryGetValue(c,out var s)&&s.Group==0))continue;
            var from=design.Zones[zone];var to=design.Zones[0];
            var a=new Vector2(from.X*size.X,Math.Clamp(from.Y*size.Y,100,size.Y-60));
            var b=new Vector2(to.X*size.X,Math.Clamp(to.Y*size.Y,100,size.Y-60));
            _link.Add(atRest(a),atRest(b),members[g].Character.MapDrawingColor);_link.Show();
        }
    }
    internal static IReadOnlyList<NHandCardHolder> Ordered(NPlayerHand hand,IReadOnlyList<NHandCardHolder> holders)
        =>hand==_hand&&SharedHandConfig.StackBasics&&_frame!=null?holders.OrderBy(h=>_frame.Order.GetValueOrDefault(h.CardModel,long.MaxValue)).ToArray():holders;
    // In an expanded group shortcuts belong exclusively to the front row.
    // Always use display order: scene order deliberately places rear cards first.
    internal static IReadOnlyList<NHandCardHolder> Shortcuts(NPlayerHand hand)
        =>HandLayouts.Active(hand)&&hand==_hand&&_frame!=null
            ?hand.ActiveHolders.Where(h=>_frame.Slots.TryGetValue(h.CardModel,out var s)&&s.Group==0&&
                s.Layer==0).OrderBy(h=>_frame.Order.GetValueOrDefault(h.CardModel,long.MaxValue)).ToArray():hand.ActiveHolders;
    internal static bool InPrimary(CardModel card)=>_frame?.Slots.TryGetValue(card,out var s)==true&&s.Group==0;
    internal static void Departure(CardModel card)
    {
        Positions.Hold();
        if(_hand?.GetCardHolder(card) is NHandCardHolder holder)
        {
            Reveal(holder);
            if(Adornments.TryGetValue(holder,out var adornment)&&GodotObject.IsInstanceValid(adornment))adornment.Hide();
        }
    }
    private static void Adorn(NCardHolder holder,Stack stack)
    {
        if(!Adornments.TryGetValue(holder,out var adornment)||!GodotObject.IsInstanceValid(adornment)||adornment.IsQueuedForDeletion())
        {adornment=new StackAdornment();holder.CardNode!.AddChild(adornment);holder.CardNode.MoveChild(adornment,0);Adornments[holder]=adornment;}
        adornment.Set(stack);adornment.Visible=stack.Quantity>1;
    }
    internal static void ReleaseNode(NCard card)
    {
        // Native NCard nodes are pooled. Custom children must not survive and
        // reappear on another physical card or on a decorative next-card view.
        foreach(var adornment in card.GetChildren().OfType<StackAdornment>().ToArray())
        {
            adornment.Hide();adornment.QueueFree();
            foreach(var key in Adornments.Where(p=>p.Value==adornment).Select(p=>p.Key).ToArray())Adornments.Remove(key);
        }
    }
    internal static bool Selected(NSelectedHandCardContainer container)
    {
        var holders=container.Holders.Where(h=>h.CardNode?.Model!=null&&!h.IsQueuedForDeletion()).ToArray();
        if(!SharedHandConfig.StackBasics||!HandLayouts.Active(container.Hand))
        {foreach(var h in holders)Reveal(h);return false;}
        var groups=new List<List<NSelectedHandCardHolder>>();var lookup=new Dictionary<string,List<NSelectedHandCardHolder>>();
        foreach(var holder in holders)
        {
            string? key=Key(holder.CardModel);
            if(key==null){groups.Add(new(){holder});continue;}
            if(!lookup.TryGetValue(key,out var list)){lookup[key]=list=new();groups.Add(list);}
            list.Add(holder);
        }
        float pitch=holders.Length==0?180:Math.Max(120,holders[0].Size.X);
        pitch=Math.Min(pitch,container.GetViewportRect().Size.X*.7f/Math.Max(1,groups.Count));
        var heads=groups.Select(g=>g[0]).ToArray();
        for(int i=0;i<groups.Count;i++)foreach(var holder in groups[i])
        {
            holder.Position=new Vector2((i-(groups.Count-1)*.5f)*pitch,0);
            if(holder==heads[i])Reveal(holder);else{Hidden.Add(holder);holder.Hide();}
            if(Adornments.TryGetValue(holder,out var old)&&GodotObject.IsInstanceValid(old))old.Hide();
            holder.FocusNeighborLeft=heads[(i+heads.Length-1)%heads.Length].GetPath();holder.FocusNeighborRight=heads[(i+1)%heads.Length].GetPath();
        }
        foreach(var group in groups.Where(g=>g.Count>1))Adorn(group[0],new(Key(group[0].CardModel)!,0,group.Select(h=>h.CardModel).ToArray()));
        if(GodotObject.IsInstanceValid(_backdrop))foreach(var group in groups.Where(g=>g.Count>1))
            _backdrop!.Add(group[0],new(Key(group[0].CardModel)!,0,group.Select(h=>h.CardModel).ToArray()),Adornments[group[0]].Counter,null);
        return true;
    }
    private sealed partial class StackAdornment : Control
    {
        private readonly Label _badge;
        internal Label Counter=>_badge;
        private SceneTree? _tree;
        private int _count;
        private ulong _increaseAt;
        private float _lastFlash;
        private static readonly SystemFont CounterFont=new(){FontNames=new[]{"Comic Sans MS","Arial Rounded MT Bold","Arial"},FontWeight=700};
        internal StackAdornment()
        {
            Name="SharedStackAdornment";MouseFilter=MouseFilterEnum.Ignore;ShowBehindParent=true;
            _badge=new Label{Name="SharedStackCount",MouseFilter=MouseFilterEnum.Ignore};
            _badge.AddThemeFontOverride("font",CounterFont);
            _badge.AddThemeColorOverride("font_color",Colors.White);
            _badge.AddThemeColorOverride("font_outline_color",Colors.Black);_badge.AddThemeConstantOverride("outline_size",9);
            _badge.AddThemeStyleboxOverride("normal",new StyleBoxEmpty());
            _badge.AddThemeConstantOverride("shadow_outline_size",12);
            _badge.AddThemeConstantOverride("shadow_offset_x",0);_badge.AddThemeConstantOverride("shadow_offset_y",0);
            AddChild(_badge);
            // This project compiles without Godot's source generator. Subscribe
            // to native signals rather than relying on generated virtual dispatch.
            TreeEntered+=()=>{_tree=GetTree();_tree.ProcessFrame+=Animate;};
            TreeExiting+=()=>{if(_tree!=null)_tree.ProcessFrame-=Animate;_tree=null;};
        }
        internal void Set(Stack stack)
        {
            if(_count!=stack.Quantity)
            {
                if(stack.Quantity>_count&&(_count>0||stack.Quantity>1&&stack.Cards.Any(StackDrawAnimation.Recent)))_increaseAt=Time.GetTicksMsec();
                _count=stack.Quantity;_badge.Text="x"+_count;
                // Grow gently with quantity, without an enormous badge at the 99-card cap.
                _badge.AddThemeFontSizeOverride("font_size",(int)MathF.Round(40+7*MathF.Log2(Math.Max(2,_count))));
                _badge.ResetSize();
            }
            FollowCard();_badge.ZIndex=0;
        }
        private void FollowCard()
        {
            // Parent directly to NCard: native tweens can update after _Process,
            // so copying its position each frame would lag a rendered frame.
            float angle=GetGlobalTransformWithCanvas().Rotation;
            // Anchor the text's bottom-right just above/left of the cost orb.
            // The anchor follows the card, including native selection tweens;
            // Keep a small playful tilt, growing outwards from the cost corner.
            float tilt=Mathf.DegToRad(-12)-angle;
            float pulse=_count>5?1+.025f*MathF.Sin(Time.GetTicksMsec()*.0026f):1;
            float t=_increaseAt==0?1:Math.Clamp((Time.GetTicksMsec()-_increaseAt)/480f,0,1);
            float flash=MathF.Sin(Mathf.Pi*t);
            pulse*=1+.32f*flash;
            if(flash>0||_lastFlash>0)_badge.AddThemeColorOverride("font_shadow_color",new Color(1,1,1,flash*.9f));
            _lastFlash=flash;
            _badge.Scale=Vector2.One*pulse;
            _badge.Position=new Vector2(-150,-208)-(_badge.Size*pulse).Rotated(tilt);_badge.Rotation=tilt;
        }
        private void Animate(){if(IsVisibleInTree())FollowCard();}
    }
    private sealed partial class StackLink : Control
    {
        private SceneTree? _tree;
        private readonly List<(Vector2 a,Vector2 b,Color color)> _segments=new();
        internal int Count=>_segments.Count;
        internal StackLink()
        {
            Draw+=Paint;
            TreeEntered+=()=>{_tree=GetTree();_tree.ProcessFrame+=Animate;};
            TreeExiting+=()=>{if(_tree!=null)_tree.ProcessFrame-=Animate;_tree=null;};
        }
        internal void Clear(){_segments.Clear();QueueRedraw();}
        internal void Add(Vector2 a,Vector2 b,Color color){_segments.Add((a,b,color));QueueRedraw();}
        private void Animate(){if(IsVisibleInTree())QueueRedraw();}
        private void Paint()
        {
            double seconds=Time.GetTicksMsec()/1000d;
            foreach(var segment in _segments)
            {
            var (_a,_b,color)=segment;color.A=.3f+.18f*(float)Math.Sin(seconds*1.6);
            var corner=new Vector2(_a.X,_b.Y);
            float first=_a.DistanceTo(corner),total=first+corner.DistanceTo(_b);
            Vector2 Along(float distance)=>distance<=first?_a.MoveToward(corner,distance):corner.MoveToward(_b,distance-first);
            for(float d=(float)(seconds*12%20)-20;d<total;d+=20)
            {
                float start=Math.Max(0,d),end=Math.Min(total,d+11);if(end<=start)continue;
                if(start<first&&end>first){DrawLine(Along(start),corner,color,2,true);DrawLine(corner,Along(end),color,2,true);}
                else DrawLine(Along(start),Along(end),color,2,true);
            }
            }
        }
    }
}

[HarmonyPatch(typeof(NPlayerHand),"StartCardPlay")]
internal static class StackNativePlayPatch
{
    private static void Prefix(NHandCardHolder holder)=>HandStacks.Departure(holder.CardModel);
    private static void Postfix(NPlayerHand __instance,NHandCardHolder holder)
    {
        if(SharedHandConfig.StackBasics&&HandLayouts.Active(__instance)&&__instance.InCardPlay)
            holder.ZIndex=__instance.CardHolderContainer.ZIndex+2;
        if(HandLayouts.Active(__instance)&&!HandStacks.InPrimary(holder.CardModel))holder.SetIndexLabel(0);
    }
    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach(var instruction in instructions)
        {
            if(instruction.operand is MethodInfo method&&method.Name==nameof(Node.GetIndex)&&method.DeclaringType==typeof(Node))
            {
                instruction.opcode=System.Reflection.Emit.OpCodes.Call;
                instruction.operand=AccessTools.Method(typeof(StackNativePlayPatch),nameof(VisibleIndex));
            }
            yield return instruction;
        }
    }
    internal static int VisibleIndex(Node holder,bool includeInternal)
    {
        var hand=NPlayerHand.Instance;
        return holder is NHandCardHolder card&&HandLayouts.Active(hand)?Math.Max(0,HandStacks.Shortcuts(hand!).ToList().Contains(card)?HandStacks.Shortcuts(hand!).ToList().IndexOf(card):hand!.ActiveHolders.ToList().IndexOf(card)):holder.GetIndex(includeInternal);
    }
}

[HarmonyPatch(typeof(NPlayerHand),"OnHolderFocused")]
internal static class StackFocusedShortcutIndexPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach(var instruction in instructions)
        {
            if(instruction.operand is MethodInfo method&&method.Name==nameof(Node.GetIndex)&&method.DeclaringType==typeof(Node))
            {instruction.opcode=System.Reflection.Emit.OpCodes.Call;instruction.operand=AccessTools.Method(typeof(StackFocusedShortcutIndexPatch),nameof(Index));}
            yield return instruction;
        }
    }
    private static int Index(Node holder,bool includeInternal)=>holder is NHandCardHolder h&&HandLayouts.Active(NPlayerHand.Instance)
        ?Math.Max(0,NPlayerHand.Instance!.ActiveHolders.ToList().IndexOf(h)):holder.GetIndex(includeInternal);
}

[HarmonyPatch(typeof(NPlayerHand),"get_ActiveHolders")]
internal static class StackVisibleOrderPatch
{
    private static void Postfix(NPlayerHand __instance,ref IReadOnlyList<NHandCardHolder> __result)=>__result=HandStacks.Ordered(__instance,__result);
}

[HarmonyPatch(typeof(NCard),nameof(NCard.OnFreedToPool))]
internal static class StackPooledNodePatch
{
    private static void Prefix(NCard __instance)=>HandStacks.ReleaseNode(__instance);
}

[HarmonyPatch(typeof(NPlayerHand),"_UnhandledInput")]
internal static class StackDraggingShortcutPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach(var instruction in instructions)
        {
            if(instruction.operand is MethodInfo getter&&getter.DeclaringType==typeof(NPlayerHand)&&getter.Name=="get_ActiveHolders")
            {instruction.opcode=System.Reflection.Emit.OpCodes.Call;instruction.operand=AccessTools.Method(typeof(HandStacks),nameof(HandStacks.Shortcuts));}
            if(instruction.operand is MethodInfo method&&method.DeclaringType==typeof(List<NHandCardHolder>)&&method.Name=="Insert")
            {
                instruction.opcode=System.Reflection.Emit.OpCodes.Call;
                instruction.operand=AccessTools.Method(typeof(StackDraggingShortcutPatch),nameof(Insert));
            }
            yield return instruction;
        }
    }
    private static void Insert(List<NHandCardHolder> holders,int index,NHandCardHolder item)
    {
        var hand=NPlayerHand.Instance;
        if(HandLayouts.Active(hand)&&
            AccessTools.Field(typeof(NPlayerHand),"_currentCardPlay").GetValue(hand) is MegaCrit.Sts2.Core.Nodes.Combat.NCardPlay play)
        {
            var card=play.Holder.CardModel;if(!HandStacks.InPrimary(card))return;var key=HandStacks.Key(card);
            if(key!=null&&holders.Any(h=>HandStacks.Key(h.CardModel)==key))return;
        }
        holders.Insert(Math.Clamp(index,0,holders.Count),item);
    }
}

[HarmonyPatch(typeof(NSelectedHandCardContainer),"RefreshHolderPositions")]
internal static class StackSelectedLayoutPatch
{
    private static bool Prefix(NSelectedHandCardContainer __instance)=>!HandStacks.Selected(__instance);
}

