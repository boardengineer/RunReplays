using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Rooms;

namespace RunReplays.Patches.Record;
using RunReplays;
using RunReplays.Commands;

/// <summary>
/// Records OpenChest when the chest is opened and TakeChestRelic when
/// the relic is picked.
/// </summary>

[HarmonyPatch(typeof(NTreasureRoom), "OpenChest")]
public static class ChestOpenRecordPatch
{
    [HarmonyPrefix]
    public static void Prefix(NTreasureRoom __instance)
    {
        if (ReplayEngine.IsActive) return;

        var cmd = new OpenChestCommand();
        PlayerActionBuffer.Record(cmd.ToString());
    }
}

[HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.PickRelicLocally))]
public static class TreasureRoomRecordPatch
{
    // Param type changed from `int` to `int?` in the game update —
    // Harmony raises InvalidProgramException when the ref-vs-value signature
    // diverges, so the type must match exactly.
    [HarmonyPrefix]
    public static void Prefix(TreasureRoomRelicSynchronizer __instance, int? index)
    {
        if (ReplayEngine.IsActive) return;

        IReadOnlyList<RelicModel>? relics = __instance.CurrentRelics;
        string? relicTitle = null;
        if (relics != null && index.HasValue && index.Value >= 0 && index.Value < relics.Count)
            relicTitle = relics[index.Value].Title.GetFormattedText();

        var cmd = new TakeChestRelicCommand { Comment = relicTitle };
        PlayerActionBuffer.Record(cmd.ToLogString());
    }
}
