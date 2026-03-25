using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;

namespace RunReplays.Patch;
using RunReplays;

/// <summary>
/// Harmony prefix on TreasureRoomRelicSynchronizer.PickRelicLocally that
/// records the relic selected from a treasure chest into the action buffer.
///
/// PickRelicLocally(int index) is the single entry point for the local player's
/// relic selection; CurrentRelics is still populated at prefix time, so we can
/// resolve the index to a relic title without reflection.
///
/// PlayerActionBuffer.Record is a no-op during replay, so there is no risk of
/// double-recording when TreasureRoomReplayPatch drives this same call.
/// </summary>
[HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.PickRelicLocally))]
public static class TreasureRoomRecordPatch
{
    [HarmonyPrefix]
    public static void Prefix(TreasureRoomRelicSynchronizer __instance, int index)
    {
        IReadOnlyList<RelicModel>? relics = __instance.CurrentRelics;

        if (relics == null || index < 0 || index >= relics.Count)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[TreasureRoomRecordPatch] PickRelicLocally index {index} out of range (count={relics?.Count ?? -1}).");
            return;
        }

        string relicTitle = relics[index].Title.GetFormattedText();
        PlayerActionBuffer.LogToDevConsole(
            $"[TreasureRoomRecordPatch] Recording TakeChestRelic '{relicTitle}' (index={index}).");
        PlayerActionBuffer.Record($"TakeChestRelic {relicTitle}");
    }
}
