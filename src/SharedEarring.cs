using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace SharedHandPrototype;

// Preserve the native loop, 13-card bound, targets, selector and dialogue.
// Only common combat stock exposes all hands to this relic. Decisions use
// its holder, never the viewer's preview, draw identity or manual-play quota.
[HarmonyPatch]
internal static class SharedEarring
{
    private static readonly Type Machine=AccessTools.Method(typeof(WhisperingEarring),nameof(WhisperingEarring.AfterAutoPrePlayPhaseEnteredLate))
        .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
    private static MethodBase TargetMethod()=>AccessTools.Method(Machine,"MoveNext");
    private static bool Shared=>CommonPiles.SharesDraw&&HandSharingMode.Shared;
    private static readonly Harmony External=new(Entry.ModId+".turn-autoplay");
    internal static void Scan(Type[] types)
    {
        foreach(var type in types.Where(t=>t.FullName=="LexNinja2.LexNinja2Code.Powers.TwoMonksPower"))
        {
            var method=AccessTools.Method(type,nameof(WhisperingEarring.AfterAutoPrePlayPhaseEnteredLate));
            var machine=method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
            if(machine==null)continue;
            NativePlayChecks.Guard(method);
            External.Patch(AccessTools.Method(machine,"MoveNext"),transpiler:new HarmonyMethod(typeof(SharedEarring),nameof(Transpiler)));
        }
    }
    private static Player Actor(AbstractModel source)=>source switch{RelicModel relic=>relic.Owner,PowerModel power=>power.Owner.Player!,_=>throw new InvalidOperationException("自动操控来源没有玩家。")};
    internal static CardModel? Choose(IEnumerable<CardModel> cards,Func<CardModel,bool> predicate,AbstractModel source)
        =>Shared?SharedHandSemantics.All().OrderBy(card=>SharedRules.PhysicalHolder(card)==Actor(source)?0:1)
            .FirstOrDefault(card=>CardEvaluation.As(card,Actor(source),()=>card.CanPlay())):cards.FirstOrDefault(predicate);
    internal static async Task<(int,int)> Spend(CardModel card,AbstractModel source)
    {
        if(!Shared||card.Owner==Actor(source))return await card.SpendResources();
        (int,int) spent=default;
        await SharedSlyActor.Execute(card,Actor(source),async()=>{spent=await card.SpendResources();});
        return spent;
    }
    internal static Task Play(PlayerChoiceContext context,CardModel card,Creature? target,AutoPlayType type,bool skipXCapture,bool skipVisuals,AbstractModel source)
        =>Shared?SharedEffectAutoPlay.ForPlayer(context,card,target,type,skipXCapture,skipVisuals,Actor(source))
                 :CardCmd.AutoPlay(context,card,target,type,skipXCapture,skipVisuals);
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
    {
        int changed=0;
        foreach(var instruction in instructions)
        {
            string? replacement=null;
            if(instruction.operand is MethodInfo method)
            {
                if(method.DeclaringType==typeof(Enumerable)&&method.Name==nameof(Enumerable.FirstOrDefault)&&method.IsGenericMethod&&
                    method.GetGenericArguments().SequenceEqual(new[]{typeof(CardModel)})&&method.GetParameters().Length==2&&
                    method.GetParameters()[1].ParameterType==typeof(Func<CardModel,bool>))replacement=nameof(Choose);
                else if(method==AccessTools.Method(typeof(CardModel),nameof(CardModel.SpendResources)))replacement=nameof(Spend);
                else if(method==AccessTools.Method(typeof(CardCmd),nameof(CardCmd.AutoPlay)))replacement=nameof(Play);
            }
            if(replacement!=null)
            {
                var load=new CodeInstruction(OpCodes.Ldarg_0);load.labels.AddRange(instruction.labels);instruction.labels.Clear();load.blocks.AddRange(instruction.blocks);instruction.blocks.Clear();yield return load;
                yield return new CodeInstruction(OpCodes.Ldfld,AccessTools.Field(__originalMethod.DeclaringType!,"<>4__this"));
                instruction.opcode=OpCodes.Call;instruction.operand=AccessTools.Method(typeof(SharedEarring),replacement);changed++;
            }
            yield return instruction;
        }
        if(changed!=3)throw new InvalidOperationException("无法定位低语耳环的选牌、付款与自动出牌入口。");
    }
}

