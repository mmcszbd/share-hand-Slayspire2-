using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace SharedHandPrototype;

internal static class ConsoleCompatibility
{
    [ThreadStatic] internal static int NetworkExecution;
}

[HarmonyPatch(typeof(DevConsole), nameof(DevConsole.ProcessNetCommand))]
internal static class NetworkConsoleExecutionPatch
{
    private static void Prefix() => ConsoleCompatibility.NetworkExecution++;
    private static void Finalizer() => ConsoleCompatibility.NetworkExecution--;
}

// Some debug menus directly invoke a command's Process method rather than the
// DevConsole.ProcessCommand router. Route these two card mutation commands
// through the game's ordered ConsoleCmdGameAction before creating any cards.
[HarmonyPatch]
internal static class DirectCardConsolePatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        yield return AccessTools.Method(typeof(CardConsoleCmd), nameof(CardConsoleCmd.Process));
        yield return AccessTools.Method(typeof(RemoveCardConsoleCmd), nameof(RemoveCardConsoleCmd.Process));
    }
    private static bool Prefix(AbstractConsoleCmd __instance, Player? issuingPlayer, string[] args, ref CmdResult __result)
    {
        if (ConsoleCompatibility.NetworkExecution != 0 || issuingPlayer == null || !RuleSession.SupportedParty) return true;
        var net = RunManager.Instance.NetService;
        if (net.Type is not (NetGameType.Host or NetGameType.Client) || !PuppetCompat.MayAct(net.NetId, issuingPlayer.NetId)) return true;
        var text = __instance.CmdName + " " + string.Join(" ", args);
        if (RunManager.Instance.ActionExecutor.CurrentlyRunningAction is ConsoleCmdGameAction running &&
            running.Player == issuingPlayer && string.Equals(running.Cmd.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(issuingPlayer, text, CombatManager.Instance.IsInProgress));
        __result = new CmdResult(success: true, "已进入联机控制台行动队列：" + text);
        return false;
    }
}




