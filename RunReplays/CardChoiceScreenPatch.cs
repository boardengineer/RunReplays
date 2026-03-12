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

namespace RunReplays;

/// <summary>
/// Records and replays card selections made via CardSelectCmd.FromChooseACardScreen
/// (e.g. Skill Potion, Power Potion).
///
/// Command format:  "SelectCardFromScreen {index}"
/// The index is the 0-based position in the card list offered to the player.
/// -1 means the player skipped (canSkip = true and no card chosen).
///
/// Recording: a Pending flag is set on entry to FromChooseACardScreen;
/// a SyncLocalChoice postfix buffers the index-based result when the flag is
/// set, and PlayerActionBuffer flushes it after the triggering action is logged.
///
/// Replay: a ReplayChooseACardSelector is pushed onto the CardSelectCmd selector
/// stack before FromChooseACardScreen runs, so the game uses replay data instead
/// of opening the selection UI.
/// </summary>
internal static class CardChoiceScreenContext
{
    internal static bool Pending;
}

// ── Replay / record entry point ───────────────────────────────────────────────

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen))]
public static class FromChooseACardScreenPatch
{
    internal static IDisposable? _pendingScope;

    [HarmonyPrefix]
    public static void Prefix()
    {
        _pendingScope?.Dispose();
        _pendingScope = null;

        if (ReplayEngine.IsActive)
        {
            if (ReplayEngine.PeekSelectCardFromScreen(out _))
            {
                _pendingScope = CardSelectCmd.PushSelector(new ReplayChooseACardSelector());
                PlayerActionBuffer.LogToDevConsole(
                    "[CardChoiceScreenPatch] Pushed ReplayChooseACardSelector for FromChooseACardScreen.");
            }
        }
        else
        {
            CardChoiceScreenContext.Pending = true;
        }
    }
}

// ── Recording: intercept SyncLocalChoice for index-based card choices ─────────

/// <summary>
/// Intercepts PlayerChoiceSynchronizer.SyncLocalChoice when
/// CardChoiceScreenContext.Pending is set, buffers "SelectCardFromScreen {index}",
/// and exposes FlushIfPending so PlayerActionBuffer can write it in order.
/// </summary>
[HarmonyPatch(typeof(PlayerChoiceSynchronizer), nameof(PlayerChoiceSynchronizer.SyncLocalChoice))]
public static class CardChoiceScreenSyncPatch
{
    private static string? _pending;

    [HarmonyPostfix]
    public static void Postfix(Player player, uint choiceId, PlayerChoiceResult result)
    {
        if (ReplayEngine.IsActive)
            return;

        if (!CardChoiceScreenContext.Pending)
            return;

        CardChoiceScreenContext.Pending = false;

        int index;
        try
        {
            index = result.AsIndex();
        }
        catch (Exception ex)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[CardChoiceScreenPatch] AsIndex() threw {ex.GetType().Name}: {ex.Message} — defaulting to -1.");
            index = -1;
        }

        _pending = $"SelectCardFromScreen {index}";
        PlayerActionBuffer.LogToDevConsole($"[CardChoiceScreenPatch] Buffered: {_pending}");
    }

    /// <summary>
    /// Called by PlayerActionBuffer after a UsePotionAction or PlayCardAction is
    /// recorded. Flushes the pending SelectCardFromScreen command so it follows
    /// the triggering action in the log.
    /// </summary>
    internal static void FlushIfPending()
    {
        if (_pending == null)
            return;

        string command = _pending;
        _pending = null;
        PlayerActionBuffer.LogToDevConsole($"[CardChoiceScreenPatch] Flushing: {command}");
        PlayerActionBuffer.Record(command);
    }
}

// ── Replay selector ───────────────────────────────────────────────────────────

/// <summary>
/// ICardSelector that consumes a SelectCardFromScreen command and returns the
/// card at the recorded 0-based index. Returns empty when index is -1 (skip).
/// </summary>
internal sealed class ReplayChooseACardSelector : ICardSelector
{
    public Task<IEnumerable<CardModel>> GetSelectedCards(
        IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        var scope = FromChooseACardScreenPatch._pendingScope;
        FromChooseACardScreenPatch._pendingScope = null;
        scope?.Dispose();

        var optionList = options.ToList();

        if (!ReplayEngine.ConsumeSelectCardFromScreen(out int index))
        {
            PlayerActionBuffer.LogToDevConsole(
                "[ReplayChooseACardSelector] No SelectCardFromScreen command — returning empty.");
            return Task.FromResult(Enumerable.Empty<CardModel>());
        }

        if (index >= 0 && index < optionList.Count)
        {
            var card = optionList[index];
            PlayerActionBuffer.LogToDevConsole(
                $"[ReplayChooseACardSelector] Selected '{card.Title}' at index {index}.");
            return Task.FromResult<IEnumerable<CardModel>>(new[] { card });
        }

        // index == -1: player skipped (canSkip = true).
        PlayerActionBuffer.LogToDevConsole(
            $"[ReplayChooseACardSelector] Index {index} — returning empty (skip or out of range).");
        return Task.FromResult(Enumerable.Empty<CardModel>());
    }

    public CardModel? GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
        => null;
}
