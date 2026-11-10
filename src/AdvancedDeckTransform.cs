using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Random;

namespace SharedHandPrototype;

// Preserve the original call so other mods' transform hooks still run.
// The async execution context carries this scope through native awaits.
[HarmonyPatch(typeof(CardCmd),nameof(CardCmd.Transform),new[]{typeof(IEnumerable<CardTransformation>),typeof(Rng),typeof(CardPreviewStyle)})]
internal static class AdvancedDeckTransform
{
    private static readonly AsyncLocal<bool> InTransform=new();
    internal static bool SuppressAcquisition=>InTransform.Value;
    private static void Prefix(out bool __state)
    {
        __state=InTransform.Value;
        if(RuleSession.Current.Advanced is {Enabled:true,DeckSync:AdvancedDeckSync.AcquisitionsOnly})InTransform.Value=true;
    }
    private static void Postfix(bool __state)=>InTransform.Value=__state;
    private static Exception? Finalizer(Exception? __exception,bool __state){InTransform.Value=__state;return __exception;}
}
