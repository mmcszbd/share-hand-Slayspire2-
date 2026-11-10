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

namespace SharedHandPrototype;

// The effect source explicitly supplies its actor. Do not infer this globally
// from choice-context history: Hellraiser can react inside someone else's draw
// effect, and must still belong to the player who actually drew the Strike.
internal static class SharedEffectAutoPlay
{
    internal static Task Play(PlayerChoiceContext choiceContext, CardModel card, Creature? target,
        AutoPlayType type, bool skipXCapture, bool skipCardPileVisuals, CardModel? source) =>
        ForPlayer(choiceContext, card, target, type, skipXCapture, skipCardPileVisuals, source?.Owner);

    internal static Task ForPlayer(PlayerChoiceContext choiceContext, CardModel card, Creature? target,
        AutoPlayType type, bool skipXCapture, bool skipCardPileVisuals, Player? actor)
    {
        if ((!SharedHandSemantics.Active && !HandSharingMode.Private) || actor == null || actor == card.Owner ||
            card.Pile?.IsCombatPile != true || actor.Creature.IsDead)
            return CardCmd.AutoPlay(choiceContext, card, target, type, skipXCapture, skipCardPileVisuals);
        return SharedSlyActor.Execute(card, actor,
            () => CardCmd.AutoPlay(choiceContext, card, target, type, skipXCapture, skipCardPileVisuals));
    }

    internal static List<CodeInstruction> LoadSource(MethodBase method)
    {
        var code = new List<CodeInstruction>();
        if (!method.IsStatic && method.DeclaringType is { } declaring)
        {
            if (typeof(CardModel).IsAssignableFrom(declaring))
                code.Add(new CodeInstruction(OpCodes.Ldarg_0));
            else if (AccessTools.Field(declaring, "<>4__this") is { } field &&
                     typeof(CardModel).IsAssignableFrom(field.FieldType))
            {
                code.Add(new CodeInstruction(OpCodes.Ldarg_0));
                code.Add(new CodeInstruction(OpCodes.Ldfld, field));
            }
        }
        // Dynamic card values use compiler-generated CanonicalVars lambdas.
        // Those closures do not capture `this`, but vanilla passes the actual
        // card as an argument. Use that card rather than dropping the shared
        // scope for calculations such as Perfected Strike's damage.
        if(code.Count==0 && method.Name.StartsWith("<get_CanonicalVars>",StringComparison.Ordinal))
        {
            var parameters=method.GetParameters();
            var cardParameters=parameters.Select((p,i)=>(parameter:p,index:i))
                .Where(x=>typeof(CardModel).IsAssignableFrom(x.parameter.ParameterType)).ToArray();
            if(cardParameters.Length==1)
                code.Add(new CodeInstruction(OpCodes.Ldarg,(short)(cardParameters[0].index+(method.IsStatic?0:1))));
        }
        // Unfamiliar mod compiler/helper shapes preserve native behavior;
        // never guess an actor from a UI-local player or mutable context.
        if (code.Count == 0) code.Add(new CodeInstruction(OpCodes.Ldnull));
        return code;
    }
}

[HarmonyPatch]
internal static class SharedDrawPileAutoPlayPatch
{
    private static readonly Type Machine = AccessTools.Method(typeof(CardPileCmd), nameof(CardPileCmd.AutoPlayFromDrawPile))
        .GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
    private static MethodBase TargetMethod() => AccessTools.Method(Machine, "MoveNext");
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var autoPlay = AccessTools.Method(typeof(CardCmd), nameof(CardCmd.AutoPlay));
        var player = AccessTools.Field(Machine, "player");
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(autoPlay))
            {
                var load = new CodeInstruction(OpCodes.Ldarg_0);
                load.labels.AddRange(instruction.labels); instruction.labels.Clear();
                load.blocks.AddRange(instruction.blocks); instruction.blocks.Clear();
                yield return load;
                yield return new CodeInstruction(OpCodes.Ldfld, player);
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.Method(typeof(SharedEffectAutoPlay), nameof(SharedEffectAutoPlay.ForPlayer));
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("无法定位原版牌堆自动出牌入口。");
    }
}

