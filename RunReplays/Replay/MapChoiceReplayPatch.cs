using System.Collections.Generic;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using RunReplays.Commands;
using RunReplays.Utils;

namespace RunReplays;

/// <summary>
/// Harmony postfix on NMapScreen.SetTravelEnabled that, when a replay is active
/// and the next command is a ChooseMapNode entry, defers an automatic map node
/// selection to the next Godot frame.
///
/// SetTravelEnabled is the hook because it is called with enabled=true at the
/// exact moment the map becomes interactive — both from our own
/// StartingBonusReplayPatch and from ProceedFromTerminalRewardsScreen for
/// combat rooms.  We confirm IsTravelEnabled is true after the call (the method
/// ANDs the flag with Hook.ShouldProceedToNextMapPoint) before scheduling.
///
/// _mapPointDictionary is private so it is accessed via reflection once at
/// class-load time.  OnMapPointSelectedLocally is public and handles vote
/// dispatch to MapSelectionSynchronizer just as a real click would.
/// </summary>
[HarmonyPatch(typeof(NMapScreen), nameof(NMapScreen.SetTravelEnabled))]
public static class MapChoiceReplayPatch
{
    private static readonly FieldInfo? MapPointDictionaryField =
        typeof(NMapScreen).GetField(
            "_mapPointDictionary",
            BindingFlags.NonPublic | BindingFlags.Instance);

    [HarmonyPostfix]
    public static void Postfix(NMapScreen __instance, bool enabled)
    {
        if (!enabled || !__instance.IsTravelEnabled)
            return;

        ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.Map);

        PlayerActionBuffer.LogToDevConsole("[RunReplays] Map is now interactive.");
        RngCheckpointLogger.Log("MapInteractive (SetTravelEnabled)");

        MapMoveCommand._activeScreen = __instance;

        if (!ReplayEngine.PeekMapNode(out int col, out int row))
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] ReplayEngine failed to peek map node.");
            return;
        }

        ReplayDispatcher.DispatchNow();
    }

    internal static void AutoSelectMapNode(NMapScreen screen, int col, int row)
    {
        if (MapPointDictionaryField?.GetValue(screen) is not Dictionary<MapCoord, NMapPoint> dict)
        {
            PlayerActionBuffer.LogToDevConsole("[RunReplays] MapChoice: could not access map point dictionary.");
            return;
        }

        var coord = new MapCoord(col, row);
        if (!dict.TryGetValue(coord, out NMapPoint? point))
        {
            PlayerActionBuffer.LogToDevConsole($"[RunReplays] MapChoice: map point col={col} row={row} not found in dictionary.");
            return;
        }

        CardPlayReplayPatch.InvalidateStaleTimers();
        screen.OnMapPointSelectedLocally(point);
    }
    
    // internal static boolean MoveT
}
