using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace RunReplays.Patch;
using RunReplays;

/// <summary>
/// Harmony prefix on RelicSelectCmd.FromChooseARelicScreen that logs to the
/// dev console when a relic selection screen is opened, including the player
/// and the list of offered relics.
/// </summary>
[HarmonyPatch(typeof(RelicSelectCmd), nameof(RelicSelectCmd.FromChooseARelicScreen))]
public static class RelicSelectCmdLogPatch
{
    [HarmonyPrefix]
    public static void Prefix(Player player, IReadOnlyList<RelicModel> relics)
    {
        string relicList = relics.Count > 0
            ? string.Join(", ", relics.Select(r => $"'{r.Title}'"))
            : "(none)";
        PlayerActionBuffer.LogToDevConsole(
            $"[RelicSelectCmd] FromChooseARelicScreen — player={player.NetId} relics=[{relicList}]");
    }
}
