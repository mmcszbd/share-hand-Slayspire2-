using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.RichTextTags;

namespace SharedHandPrototype;

// Preserve the native noise, amplitude, colors and accessibility switch.
// Only text explicitly carrying our optional tag receives a slower clock.
[HarmonyPatch(typeof(RichTextJitter), nameof(RichTextJitter._ProcessCustomFX))]
internal static class SlowFeedbackJitter
{
    internal static float Speed(CharFXTransform fx) => fx.Env.TryGetValue("shared_hand_speed",out var speed)
        ? 600f*Math.Clamp((float)speed,.1f,1f) : 600f;
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach(var instruction in instructions)
        {
            if(instruction.opcode==OpCodes.Ldc_R4 && Equals(instruction.operand,600f))
            {
                var load=new CodeInstruction(OpCodes.Ldarg_1);load.labels.AddRange(instruction.labels);
                load.blocks.AddRange(instruction.blocks);
                yield return load;
                yield return new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(SlowFeedbackJitter),nameof(Speed)));
            }
            else yield return instruction;
        }
    }
}

