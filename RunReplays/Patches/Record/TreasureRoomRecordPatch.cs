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
    [HarmonyPrefix]
    public static void Prefix(TreasureRoomRelicSynchronizer __instance, int index)
    {
        if (ReplayEngine.IsActive) return;

        IReadOnlyList<RelicModel>? relics = __instance.CurrentRelics;
        string? relicTitle = null;
        if (relics != null && index >= 0 && index < relics.Count)
            relicTitle = relics[index].Title.GetFormattedText();

        var cmd = new TakeChestRelicCommand { Comment = relicTitle };
        PlayerActionBuffer.Record(cmd.ToLogString());
    }
}
