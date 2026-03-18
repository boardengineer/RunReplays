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
using RunReplays.Utils;

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

    private static RestSiteSynchronizer? _activeSynchronizer;

    [HarmonyPostfix]
    public static void Postfix(RestSiteSynchronizer __instance)
    {
        RngCheckpointLogger.Log("RestSite (BeginRestSite)");

        if (ReplayEngine.IsActive)
            ReplayDispatcher.SignalReady(ReplayDispatcher.ReadyState.RestSite);

        if (!ReplayEngine.PeekRestSiteOption(out string optionId))
            return;

        _activeSynchronizer = __instance;

        PlayerActionBuffer.LogToDevConsole(
            $"[RestSiteReplayPatch] BeginRestSite — stored synchronizer for option '{optionId}'.");
        ReplayDispatcher.DispatchNow();
    }

    /// <summary>Called by ReplayDispatcher to trigger rest site option selection.</summary>
    internal static void DispatchFromEngine()
    {
        if (_activeSynchronizer == null)
            return;
        if (!ReplayEngine.PeekRestSiteOption(out string optionId))
            return;
        TaskHelper.RunSafely(WaitForRoomThenSelect(_activeSynchronizer, optionId));
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
        if (!success)
            return;

        // Check for another pending rest site option (e.g. Miniature Tent grants
        // two rest site choices).  If found, select it before notifying the room,
        // so the proceed button only appears after all options are consumed.
        if (ReplayEngine.PeekRestSiteOption(out string nextOptionId))
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[RestSiteReplayPatch] Another rest site option pending: '{nextOptionId}' — continuing chain.");
            Callable.From(() =>
            {
                NRestSiteRoom.Instance?.AfterSelectingOption(option);
                TaskHelper.RunSafely(WaitForRoomThenSelect(sync, nextOptionId));
            }).CallDeferred();
        }
        else
        {
            Callable.From(() => NRestSiteRoom.Instance?.AfterSelectingOption(option)).CallDeferred();
        }
    }
}
