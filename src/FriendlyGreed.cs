using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace SharedHandPrototype;

// Optional YouJin adapter. Keep its UI, sidecar choices, resting requirement,
// curses and acquisition hooks; resolve transfers by the selected object,
// never by a same-name lookup or a list index changed by another thief.
internal static class FriendlyGreed
{
    private sealed class Picks
    {
        internal object? Room;
        internal readonly Dictionary<ulong,(ulong victim,int index,CardModel? card)> Cards=new();
        internal readonly Dictionary<ulong,(ulong victim,int index,RelicModel? relic)> Relics=new();
    }
    private sealed class Verdict
    {
        internal readonly Type Type;
        internal readonly FieldInfo Choices,Chosen,Resolved;
        internal readonly FieldInfo? Resolving;
        internal readonly MethodInfo Guilt;
        internal Verdict(Type type)
        {
            Type=type;
            Choices=AccessTools.Field(type,"Choices")??throw new MissingFieldException(type.FullName,"Choices");
            Chosen=AccessTools.Field(type,"Chosen")??throw new MissingFieldException(type.FullName,"Chosen");
            Resolved=AccessTools.Field(type,"_resolved")??throw new MissingFieldException(type.FullName,"_resolved");
            Resolving=AccessTools.Field(type,"_resolving");
            Guilt=AccessTools.Method(type,"GiveGuiltAsync",new[]{typeof(Player),typeof(ulong)})??throw new MissingMethodException(type.FullName,"GiveGuiltAsync");
            if(Choices.FieldType!=typeof(Dictionary<ulong,(ulong,int)>)||Chosen.FieldType!=typeof(Dictionary<ulong,RestSiteOption>))
                throw new NotSupportedException("YouJin verdict schema changed.");
        }
        internal Dictionary<ulong,(ulong victim,int index)> Selections=>(Dictionary<ulong,(ulong,int)>)Choices.GetValue(null)!;
        internal Dictionary<ulong,RestSiteOption> Options=>(Dictionary<ulong,RestSiteOption>)Chosen.GetValue(null)!;
    }
    private static readonly ConditionalWeakTable<IRunState,Picks> Saved=new();
    private static Verdict? CardVerdict,RelicVerdict;
    private static PropertyInfo? Enabled;
    private static bool installed;
    internal static void Scan(Type[] types)
    {
        if(installed)return;
        var settings=types.FirstOrDefault(t=>t.FullName=="YouJin.Scripts.YouJinSettings");
        if(settings==null)return;
        installed=true;
        FriendlySmoke.Scan(types);
        CompatibilityScan.Attempt("friendly-greedy",settings.FullName!,()=>
        {
            Enabled=AccessTools.Property(settings,"GreedyEnabled")??throw new MissingMemberException();
            CardVerdict=new(types.Single(t=>t.FullName=="YouJin.Scripts.StealCardVerdict"));
            RelicVerdict=new(types.Single(t=>t.FullName=="YouJin.Scripts.StealRelicVerdict"));
            var patcher=new Harmony(Entry.ModId+".friendly-greedy");
            foreach(var pair in new[]{(CardVerdict,"CaptureCard","ResolveAsync","ResolveCards"),(RelicVerdict,"CaptureRelic","ResolveIfAnyAsync","ResolveRelics")})
            {
                var note=AccessTools.Method(pair.Item1.Type,"NoteChoice",new[]{typeof(ulong),typeof(ulong),typeof(int)})??throw new MissingMethodException();
                var resolve=AccessTools.Method(pair.Item1.Type,pair.Item3,Type.EmptyTypes)??throw new MissingMethodException();
                if(resolve.ReturnType!=typeof(Task))throw new NotSupportedException("YouJin transfer signature changed.");
                patcher.Patch(note,postfix:new HarmonyMethod(typeof(FriendlyGreed),pair.Item2));
                patcher.Patch(resolve,prefix:new HarmonyMethod(typeof(FriendlyGreed),pair.Item4));
            }
            FriendlyGreedTransport.Install(patcher,types);
            patcher.Patch(AccessTools.Method(types.Single(t=>t.FullName=="YouJin.Scripts.StealCardRestSiteModel"),"ChooseCardFromDeckUpgradeUiAsync"),
                prefix:new HarmonyMethod(typeof(FriendlyGreed),nameof(ChooseCard)));
        });
    }
    internal static RunState? Active()
    {
        var run=RunManager.Instance?.DebugOnlyGetState();
        return RuleSession.Ready&&run?.Players.Count is >=2 and <=PartyLimits.MaxPlayers&&Enabled?.GetValue(null) is true?run:null;
    }
    internal static void NoteChoice(bool relic,ulong thief,ulong victim,int index)=>
        AccessTools.Method((relic?RelicVerdict:CardVerdict)!.Type,"NoteChoice").Invoke(null,new object[]{thief,victim,index});
    private static bool ChooseCard(Player victim,ref Task<CardModel?> __result)
    {if(Active()==null)return true;__result=SelectCard(victim);return false;}
    private static async Task<CardModel?> SelectCard(Player victim)
    {
        if(victim.Deck.Cards.Count==0)return null;
        // Theft is a selection, not an upgrade: max-upgraded cards and curses
        // must remain selectable, and no upgrade preview should be performed.
        var prefs=new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt,1){Cancelable=true,RequireManualConfirmation=true};
        var screen=NDeckCardSelectScreen.Create(victim.Deck.Cards.ToArray(),prefs);
        NOverlayStack.Instance.Push(screen);
        var selected=(await screen.CardsSelected()).FirstOrDefault();
        return selected!=null&&victim.Deck.Cards.Contains(selected)?selected:null;
    }
    private static Picks For(IRunState run)
    {
        var saved=Saved.GetOrCreateValue(run);
        if(!ReferenceEquals(saved.Room,NRestSiteRoom.Instance))
        {saved.Room=NRestSiteRoom.Instance;saved.Cards.Clear();saved.Relics.Clear();}
        return saved;
    }
    private static void CaptureCard(ulong __0,ulong __1,int __2)
    {
        if(Active() is not {} run)return;
        var picks=For(run).Cards;
        if(picks.TryGetValue(__0,out var old)&&old.victim==__1&&old.index==__2)return;
        var deck=run.Players.FirstOrDefault(p=>p.NetId==__1)?.Deck.Cards;
        picks[__0]=(__1,__2,deck!=null&&__2>=0&&__2<deck.Count?deck[__2]:null);
    }
    private static void CaptureRelic(ulong __0,ulong __1,int __2)
    {
        if(Active() is not {} run)return;
        var picks=For(run).Relics;
        if(picks.TryGetValue(__0,out var old)&&old.victim==__1&&old.index==__2)return;
        var relics=run.Players.FirstOrDefault(p=>p.NetId==__1)?.Relics;
        picks[__0]=(__1,__2,relics!=null&&__2>=0&&__2<relics.Count?relics[__2]:null);
    }
    private static bool Ready(Verdict verdict,RunState run)=>
        verdict.Resolved.GetValue(null) is false&&verdict.Options.Count>=run.Players.Count;
    private static bool ResolveCards(ref Task __result)
    {
        if(Active() is not {} run)return true;
        __result=Ready(CardVerdict!,run)?Transfer(run,CardVerdict!,false):Task.CompletedTask;return false;
    }
    private static bool ResolveRelics(ref Task __result)
    {
        if(Active() is not {} run)return true;
        __result=Ready(RelicVerdict!,run)?Transfer(run,RelicVerdict!,true):Task.CompletedTask;return false;
    }
    private static bool Rested(RestSiteOption? option)=>option is HealRestSiteOption or MendRestSiteOption||option?.OptionId=="YOUJIN_DEEPSLEEP";
    private static async Task Transfer(RunState run,Verdict verdict,bool relic)
    {
        // Mark before the first await: native NoteChosen calls relic resolution
        // twice, and acquisition hooks can themselves pause for a choice.
        verdict.Resolved.SetValue(null,true);
        var batch=verdict.Selections.OrderBy(x=>x.Key).ToArray();
        var options=new Dictionary<ulong,RestSiteOption>(verdict.Options);
        var picks=For(run);
        try
        {
            foreach(var item in batch)
            {
                var thief=run.Players.FirstOrDefault(p=>p.NetId==item.Key);
                var victim=run.Players.FirstOrDefault(p=>p.NetId==item.Value.victim);
                if(thief==null||victim==null||thief==victim)continue;
                if(!Rested(options.GetValueOrDefault(victim.NetId)))
                {await (Task)verdict.Guilt.Invoke(null,new object[]{thief,thief.NetId})!;continue;}
                if(relic)
                {
                    if(!picks.Relics.TryGetValue(thief.NetId,out var pick)||pick.victim!=victim.NetId||pick.index!=item.Value.index||
                        pick.relic is not {} selected||!victim.Relics.Contains(selected)||selected.IsMelted)continue;
                    var fresh=selected.CanonicalInstance.ToMutable();
                    await RelicCmd.Remove(selected);
                    await RelicCmd.Obtain(fresh,thief);
                    Diagnostics.Record("greedy-relic-transfer",thief,selected.Id.Entry);
                }
                else
                {
                    if(!picks.Cards.TryGetValue(thief.NetId,out var pick)||pick.victim!=victim.NetId||pick.index!=item.Value.index||
                        pick.card is not {} selected||!victim.Deck.Cards.Contains(selected))continue;
                    // Every player already owns this logical card in mirrored
                    // decks. Removing and reacquiring it would replace its ID,
                    // provenance and selection references for the whole party.
                    if(SharedDeckSync.Enabled(run))
                    {Diagnostics.Record("greedy-shared-card",thief,selected.Id.Entry);continue;}
                    victim.Deck.RemoveInternal(selected);
                    run.RemoveCard(selected);
                    run.AddCard(selected,thief);
                    thief.Deck.AddInternal(selected);
                    victim.Deck.InvokeContentsChanged();thief.Deck.InvokeCardAddFinished();
                    Diagnostics.Record("greedy-card-transfer",thief,selected.Id.Entry);
                }
            }
        }
        finally
        {
            verdict.Resolving?.SetValue(null,false);
            foreach(var item in batch)if(relic)picks.Relics.Remove(item.Key);else picks.Cards.Remove(item.Key);
        }
    }
}

