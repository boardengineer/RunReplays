using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RunReplays.Patches.Record;
using RunReplays.Commands;
using RunReplays.Utils;

[HarmonyPatch(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.CardsSelected))]
public static class DeckCardSelectRecordPatch
{
    [HarmonyPostfix]
    public static void Postfix(NCardGridSelectionScreen __instance, Task<IEnumerable<CardModel>> __result)
    {
        if (__instance is NDeckUpgradeSelectScreen)
            return;

        TaskHelper.RunSafely(RecordAsync(__result, __instance));
    }

    private static async Task RecordAsync(
        Task<IEnumerable<CardModel>> task,
        NCardGridSelectionScreen screen)
    {
        var selected = await task;
        var cardList = selected.ToList();

        var titles = string.Join(", ", cardList.Select(c => $"'{c.Title}'"));
        PlayerActionBuffer.RecordVerboseOnly($"[DeckCardSelect] Selected: [{titles}]");

        // Resolve the selectable list the same way replay will.  This handles
        // combat-pile screens (e.g. Seeker Strike picking from the draw pile)
        // whose _cards field stays empty — the list is rebuilt from the live
        // pile + filter instead.
        var selectable = CardGridScreenCapture.GetSelectableCards(screen)?.ToList();

        // Collect all selected indices into a single command so that
        // multi-card selections (e.g. Morphic Grove) are recorded atomically.
        var indices = new List<int>(cardList.Count);
        foreach (var card in cardList)
        {
            var index = selectable?.IndexOf(card) ?? -1;
            DiagnosticLog.Write("DeckCardSelect",
                $"'{card.Title}' → index {index} on {screen.GetType().Name} " +
                $"({selectable?.Count.ToString() ?? "no"} selectable cards)");
            indices.Add(index);
        }

        // A -1 index makes replay retry forever — leave a loud trace and drop
        // the command instead of poisoning the log.
        if (indices.Any(i => i < 0))
        {
            DiagnosticLog.WriteAndEcho("DeckCardSelect",
                $"Unresolvable selection on {screen.GetType().Name} — NOT recorded ({titles}).");
            return;
        }

        string command = new SelectGridCardCommand(indices.ToArray()).ToString();
        if (DeckRemovalState.PendingRemoval)
        {
            DeckRemovalState.PendingRemoval = false;
            PlayerActionBuffer.Record(command);
        }
        else
        {
            PlayerActionBuffer.RecordMinimalOnly(command);
        }

        PlayerActionBuffer.LogToDevConsole(
            $"[DeckCardSelectPatch] Recorded {command} ({titles}).");
    }
}
