using HarmonyLib;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using RunReplays.Utils;

namespace RunReplays.Patches.Record;

/// <summary>
/// Harmony prefix on RunHistoryUtilities.CreateRunHistoryEntry that suppresses
/// history recording for any run started as a replay.
///
/// ReplayEngine.IsReplayRun is set when any of the three replay entry points
/// fire (StartReplay, StartReplayFromFloor, LoadSave) and is cleared on return
/// to the main menu. It remains true even after all replay commands are consumed,
/// so it correctly covers runs where the player keeps playing after the replay ends.
/// </summary>
[HarmonyPatch(typeof(RunHistoryUtilities), nameof(RunHistoryUtilities.CreateRunHistoryEntry))]
public static class SuppressReplayRunHistoryPatch
{
    [HarmonyPrefix]
    public static bool Prefix(SerializableRun run, bool victory, bool isAbandoned)
    {
        if (!ReplayEngine.IsReplayRun) return true;
        DiagnosticLog.Write("History",
            $"Suppressing run history for replay run — seed={run.SerializableRng?.Seed} victory={victory} abandoned={isAbandoned}");
        return false;
    }
}
