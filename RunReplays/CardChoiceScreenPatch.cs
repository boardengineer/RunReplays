using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace RunReplays;

/// <summary>
/// Records and replays card selections made via CardSelectCmd.FromChooseACardScreen
/// (e.g. Skill Potion, Power Potion, Lead Paperweight relic).
///
/// Command format:  "SelectCardFromScreen {index}"
/// The index is the 0-based position in the card list offered to the player.
/// -1 means the player skipped (canSkip = true and no card chosen).
///
/// Recording: the Postfix wraps the returned Task so that when the player's
/// selection completes, the chosen card's index is buffered.  A deferred flush
/// is also scheduled so that relic-triggered selections (which have no
/// subsequent action to flush the buffer) are still recorded.  For
/// potion / card-play actions the normal AfterActionExecuted flush fires first,
/// making the deferred flush a harmless no-op.
///
/// Replay: a ReplayChooseACardSelector is pushed onto the CardSelectCmd selector
/// stack before FromChooseACardScreen runs, so the game uses replay data instead
/// of opening the selection UI.
/// </summary>

// ── Replay / record entry point ───────────────────────────────────────────────

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromChooseACardScreen))]
public static class FromChooseACardScreenPatch
{
    internal static IDisposable? _pendingScope;

    // Captured in Prefix so the Postfix wrapper can compute the selected index.
    private static List<CardModel>? _recordingCards;

    [HarmonyPrefix]
    public static void Prefix(IReadOnlyList<CardModel> cards)
    {
        _pendingScope?.Dispose();
        _pendingScope = null;

        if (ReplayEngine.IsActive)
        {
            // Try front of queue first; if not there, drain interleaved commands
            // (e.g. relic-triggered card choices where auto-processed actions sit
            // between TakeRelicReward and SelectCardFromScreen).
            if (ReplayEngine.PeekSelectCardFromScreen(out _)
                || ReplayEngine.SkipToSelectCardFromScreen())
            {
                _pendingScope = CardSelectCmd.PushSelector(new ReplayChooseACardSelector());
                PlayerActionBuffer.LogToDevConsole(
                    "[CardChoiceScreenPatch] Pushed ReplayChooseACardSelector for FromChooseACardScreen.");
            }
        }
        else
        {
            _recordingCards = cards.ToList();
            PlayerActionBuffer.LogToDevConsole(
                $"[CardChoiceScreenPatch] Prefix: captured {_recordingCards.Count} card(s) for recording.");
        }
    }

    /// <summary>
    /// Wraps the Task returned by FromChooseACardScreen so that when the
    /// player's selection completes we can compute the index and buffer it.
    /// </summary>
    [HarmonyPostfix]
    public static void Postfix(ref Task<CardModel> __result)
    {
        if (ReplayEngine.IsActive || _recordingCards == null)
            return;

        var cardList = _recordingCards;
        _recordingCards = null;

        var original = __result;
        __result = WrapAndRecord(original, cardList);
    }

    private static async Task<CardModel> WrapAndRecord(Task<CardModel> original, List<CardModel> cardList)
    {
        var selected = await original;

        int index = -1;

        if (selected != null)
        {
            for (int i = 0; i < cardList.Count; i++)
            {
                if (ReferenceEquals(cardList[i], selected))
                {
                    index = i;
                    break;
                }
            }

            // Fallback: match by title if reference equality fails.
            if (index < 0)
            {
                var title = selected.Title;
                for (int i = 0; i < cardList.Count; i++)
                {
                    if (cardList[i].Title == title)
                    {
                        index = i;
                        break;
                    }
                }
            }
        }

        CardChoiceScreenSyncPatch.Buffer($"SelectCardFromScreen {index}");
        PlayerActionBuffer.LogToDevConsole(
            $"[CardChoiceScreenPatch] Buffered: SelectCardFromScreen {index}");

        // Schedule a deferred flush for relic-triggered selections where no
        // subsequent game action would flush the buffer.  For potion / card-play
        // actions the AfterActionExecuted flush fires first, making this a no-op.
        Callable.From(() => CardChoiceScreenSyncPatch.FlushIfPending()).CallDeferred();

        return selected!;
    }
}

// ── Recording buffer ─────────────────────────────────────────────────────────

/// <summary>
/// Holds a pending "SelectCardFromScreen" command until PlayerActionBuffer
/// flushes it after the triggering action is recorded.
/// </summary>
public static class CardChoiceScreenSyncPatch
{
    private static string? _pending;

    /// <summary>
    /// Buffers a command for later flushing.
    /// Called by FromChooseACardScreenPatch's async wrapper.
    /// </summary>
    internal static void Buffer(string command)
    {
        _pending = command;
    }

    /// <summary>
    /// Called by PlayerActionBuffer after a triggering action is recorded,
    /// or by a deferred flush scheduled by the async wrapper.
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
