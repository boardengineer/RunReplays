using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace RunReplays.Patch;
using RunReplays;

/// <summary>
/// Records and replays card selections made via CardSelectCmd.FromSimpleGrid
/// (e.g. Seeker Strike, which shows a subset of draw-pile cards on a simple grid).
///
/// Command format:  "SelectSimpleCard {index}"
/// The index is the 0-based position in the card list shown to the player.
///
/// Recording: a Pending flag is set on entry to FromSimpleGrid; a SyncLocalChoice
/// postfix buffers the index-based result when the flag is set, and
/// PlayerActionBuffer flushes it after the parent PlayCardAction is logged.
///
/// Replay: a ReplaySimpleCardSelector is pushed onto the CardSelectCmd selector
/// stack before FromSimpleGrid runs, so the game uses replay data instead of
/// opening the selection UI.
/// </summary>
internal static class SimpleGridContext
{
    internal static bool Pending;
}

// ── Replay / record entry point ───────────────────────────────────────────────

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromSimpleGrid))]
public static class FromSimpleGridPatch
{
    internal static IDisposable? _pendingScope;

    [HarmonyPrefix]
    public static void Prefix()
    {
        _pendingScope?.Dispose();
        _pendingScope = null;
    }
}

// ── Recording: intercept SyncLocalChoice ──────────────────────────────────────

/// <summary>
/// Intercepts PlayerChoiceSynchronizer.SyncLocalChoice when SimpleGridContext.Pending
/// is set, buffers "SelectSimpleCard {index}", and exposes FlushIfPending so
/// PlayerActionBuffer can write it in order after the parent PlayCardAction.
/// </summary>
[HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.SyncLocalChoice))]
public static class SimpleGridSyncPatch
{
    [HarmonyPostfix]
    public static void Postfix(Player player, uint choiceId, PlayerChoiceResult result)
    {
        if (ReplayEngine.IsActive)
            return;

        if (!SimpleGridContext.Pending)
            return;

        SimpleGridContext.Pending = false;

        if (result.ChoiceType != PlayerChoiceType.Index)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[SimpleGridSyncPatch] Unexpected ChoiceType {result.ChoiceType} — ignoring.");
            return;
        }

        int index;
        try
        {
            index = result.AsIndex();
        }
        catch (Exception ex)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[SimpleGridSyncPatch] AsIndex() threw {ex.GetType().Name}: {ex.Message} — defaulting to -1.");
            index = -1;
        }

        string command = $"SelectSimpleCard {index}";
        PlayerActionBuffer.LogToDevConsole($"[SimpleGridSyncPatch] Recording: {command}");
        PlayerActionBuffer.Record(command);
    }

    internal static void FlushIfPending()
    {
    }
}