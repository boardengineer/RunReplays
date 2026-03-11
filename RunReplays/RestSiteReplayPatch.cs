using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace RunReplays;

/// <summary>
/// Harmony postfix on RestSiteSynchronizer.BeginRestSite that, when a replay
/// is active and the next command is a ChooseRestSiteOption entry, waits for
/// NRestSiteRoom.Instance to become non-null (i.e. the room node has entered
/// the scene tree after asset loading) and then calls ChooseLocalOption on the
/// matching option.
///
/// BeginRestSite is the earliest point at which option IDs are known.
/// NRestSiteRoom._Ready fires later (after PreloadManager.LoadRoomRestSite
/// completes), so polling Instance is more reliable than hooking _Ready.
///
/// After ChooseLocalOption completes (including any sub-screens like the upgrade
/// picker for SMITH), AfterSelectingOption is called on the room via CallDeferred.
/// This triggers AfterSelectingOptionAsync → ShowProceedButton →
/// NMapScreen.SetTravelEnabled(true) ← MapChoiceReplayPatch.
///
/// Calling ChooseLocalOption directly bypasses NRestSiteButton.SelectOption,
/// which is the path that normally calls AfterSelectingOption.  We restore that
/// call explicitly once the option's async work is complete.
///
/// For SMITH options, OnSelect awaits NDeckUpgradeSelectScreen; the existing
/// UpgradeCardReplayPatch consumes the UpgradeCard command automatically, so
/// ChooseLocalOption's task resolves after the upgrade is done.
/// </summary>
[HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.BeginRestSite))]
public static class RestSiteReplayPatch
{
    private const int MaxRetries = 20;

    [HarmonyPostfix]
    public static void Postfix(RestSiteSynchronizer __instance)
    {
        if (!ReplayEngine.PeekRestSiteOption(out string optionId))
            return;

        PlayerActionBuffer.LogToDevConsole(
            $"[RestSiteReplayPatch] BeginRestSite — waiting for room, then auto-selecting '{optionId}'.");
        TaskHelper.RunSafely(WaitForRoomThenSelect(__instance, optionId));
    }

    private static async Task WaitForRoomThenSelect(
        RestSiteSynchronizer sync, string optionId, int retriesLeft = MaxRetries)
    {
        if (!ReplayEngine.IsActive)
            return;

        if (NRestSiteRoom.Instance == null)
        {
            if (retriesLeft > 0)
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[RestSiteReplayPatch] NRestSiteRoom.Instance not yet available — retrying in 100 ms ({retriesLeft} left).");
                await Task.Delay(100);
                await WaitForRoomThenSelect(sync, optionId, retriesLeft - 1);
            }
            else
            {
                PlayerActionBuffer.LogToDevConsole(
                    "[RestSiteReplayPatch] NRestSiteRoom.Instance never became available — aborting.");
            }
            return;
        }

        IReadOnlyList<RestSiteOption> options = sync.GetLocalOptions();

        int index = -1;
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].OptionId == optionId)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            string available = options.Count > 0
                ? string.Join(", ", options.Select(o => $"'{o.OptionId}'"))
                : "(none)";
            PlayerActionBuffer.LogToDevConsole(
                $"[RestSiteReplayPatch] Option '{optionId}' not found (available: [{available}]) — aborting.");
            return;
        }

        RestSiteOption selectedOption = options[index];
        ReplayRunner.ExecuteRestSiteOption(out _);
        PlayerActionBuffer.LogToDevConsole(
            $"[RestSiteReplayPatch] Auto-selected rest site option '{optionId}' at index {index}.");
        await SelectAndNotifyRoom(sync, index, selectedOption);
    }

    private static async Task SelectAndNotifyRoom(
        RestSiteSynchronizer sync, int index, RestSiteOption option)
    {
        bool success = await sync.ChooseLocalOption(index);
        PlayerActionBuffer.LogToDevConsole(
            $"[RestSiteReplayPatch] ChooseLocalOption returned {success} — notifying room.");
        if (success)
            Callable.From(() => NRestSiteRoom.Instance?.AfterSelectingOption(option)).CallDeferred();
    }
}
