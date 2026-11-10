using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace SharedHandPrototype;

// Read optional framework state at committed cost changes, never mutate it.
// This covers all cards and any fixed secondary resource, without a character dependency.
internal static class FreeCostAudit
{
    private static MethodInfo? _free;
    private static bool _paymentInstalled;
    internal static void InstallPaymentAudit(Assembly[] assemblies)
    {
        if(_paymentInstalled)return;
        var resolver=assemblies.Select(a=>a.GetType("STS2RitsuLib.Combat.SecondaryResources.SecondaryResourcePaymentResolver",false)).FirstOrDefault(t=>t!=null);
        var plan=assemblies.Select(a=>a.GetType("STS2RitsuLib.Combat.SecondaryResources.SecondaryResourcePaymentPlan",false)).FirstOrDefault(t=>t!=null);
        if(resolver==null||plan==null)return;
        try
        {
            var method=AccessTools.Method(resolver,"Commit",new[]{plan,typeof(AbstractModel)});
            if(method==null){Diagnostics.Record("secondary-audit-unavailable",detail:"Commit signature not found");return;}
            new Harmony(Entry.ModId+".secondary-audit").Patch(method,prefix:new HarmonyMethod(typeof(FreeCostAudit),nameof(PaymentPlan)));
            _paymentInstalled=true;
            Diagnostics.Record("secondary-audit-ready");
        }
        catch(Exception ex){Diagnostics.Record("secondary-audit-install-error",detail:ex.GetType().Name);}
    }
    private static void PaymentPlan(object __0)
    {
        try
        {
            var type=__0.GetType();
            var card=type.GetProperty("Card")?.GetValue(__0) as CardModel;
            var player=type.GetProperty("Player")?.GetValue(__0) as Player;
            if(card==null||!SharedRules.IsSupportedParty(card)||CardEvaluation.Active)return;
            string index=NetCombatCardDb.Instance.TryGetCardId(card,out var id)?id.ToString():"none";
            var lines=type.GetProperty("Lines")?.GetValue(__0) as IEnumerable;
            if(lines==null)return;
            foreach(var line in lines)
            {
                if(line==null)continue;
                var t=line.GetType();
                string V(string n)=>t.GetProperty(n)?.GetValue(line)?.ToString()??"unknown";
                Diagnostics.Record("secondary-payment-plan",player,"card="+card.Id.Entry+";index="+index+
                    ";holder="+Diagnostics.Role(SharedRules.PhysicalHolder(card))+";owner="+Diagnostics.Role(card.Owner)+
                    ";resource="+V("ResourceId")+";use="+V("UseId")+";kind="+V("Kind")+
                    ";cost="+V("Cost")+";available="+V("AmountAvailable")+";spend="+V("AmountToSpend")+
                    ";free="+V("IsFree")+";activated="+V("Activated")+";value="+V("Value"));
            }
        }
        catch(Exception ex){Diagnostics.Record("secondary-plan-audit-error",detail:ex.GetType().Name);}
    }
    internal static void Record(string kind, CardModel card)
    {
        try
        {
            if (!SharedRules.IsSupportedParty(card) || CardEvaluation.Active) return;
            _free ??= AppDomain.CurrentDomain.GetAssemblies()
                .Select(a=>a.GetType("STS2RitsuLib.Cards.FreePlay.FreePlayBindingRegistry",false))
                .Where(t=>t!=null).Select(t=>AccessTools.Method(t,"IsCardFreeForUpcomingPlay",new[]{typeof(CardModel)}))
                .FirstOrDefault(m=>m!=null);
            string free=_free==null?"unavailable":_free.Invoke(null,new object[]{card})?.ToString()??"unknown";
            string id=NetCombatCardDb.Instance.TryGetCardId(card,out var index)?index.ToString():"none";
            Diagnostics.Record(kind,card.Owner,"card="+card.Id.Entry+";index="+id+
                ";holder="+Diagnostics.Role(SharedRules.PhysicalHolder(card))+";pile="+card.Pile?.Type+
                ";energy="+card.EnergyCost.GetWithModifiers(CostModifiers.All)+";secondaryFree="+free);
        }
        catch(Exception ex){Diagnostics.Record("free-cost-audit-error",detail:ex.GetType().Name);}
    }
}

[HarmonyPatch(typeof(CardModel),nameof(CardModel.SetToFreeThisTurn))]
internal static class FreeCostMarkedAudit
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(CardModel __instance)=>FreeCostAudit.Record("free-cost-marked",__instance);
}

[HarmonyPatch(typeof(CardModel),"SpendResources")]
internal static class FreeCostPaymentAudit
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(CardModel __instance)=>FreeCostAudit.Record("free-cost-payment",__instance);
}
