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
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace RunReplays;

/// <summary>
/// Records and replays card selections made via CardSelectCmd.FromChooseACardScreen
/// (e.g. Skill Potion, Power Potion, Lead Paperweight relic, Morphic Grove).
///
/// Command format:  "SelectCardFromScreen {index}"
/// The index is the 0-based position in the card list offered to the player.
/// -1 means the player skipped (canSkip = true and no card chosen).
///
/// When the selection is part of a card reward (e.g. Morphic Grove uses
/// FromChooseACardScreen instead of the normal NCardRewardSelectionScreen),
/// the SelectCardFromScreen recording is suppressed — the TakeCardReward
/// command already captures the selection by card title.
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
/// of opening the selection UI.  When the next command is a TakeCardReward
/// (no SelectCardFromScreen in the queue), a ReplayCardRewardChooseSelector is
/// pushed instead — it picks the card by title and consumes the TakeCardReward.
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

        SelectorStackDebug.Log("FromChooseACardScreen.Prefix called (IsActive=" + ReplayEngine.IsActive + ")");
        if (ReplayEngine.IsActive)
        {
            // Try front of queue first; if not there, drain interleaved commands
            // (e.g. relic-triggered card choices where auto-processed actions sit
            // between TakeRelicReward and SelectCardFromScreen).
            if (ReplayEngine.PeekSelectCardFromScreen(out _)
                || ReplayEngine.SkipToSelectCardFromScreen())
            {
                _pendingScope = CardSelectCmd.PushSelector(new ReplayChooseACardSelector());
                SelectorStackDebug.Log("PUSH FromChooseACardScreen");
                PlayerActionBuffer.LogToDevConsole(
                    "[CardChoiceScreenPatch] Pushed ReplayChooseACardSelector for FromChooseACardScreen.");
            }
            else if (ReplayEngine.PeekCardReward(out string cardTitle, out _))
            {
                // Card reward that uses FromChooseACardScreen (e.g. Morphic Grove).
                // The TakeCardReward command has the card title — use it to select.
                _pendingScope = CardSelectCmd.PushSelector(new ReplayCardRewardChooseSelector(cardTitle));
                SelectorStackDebug.Log("PUSH FromChooseACardScreen (card reward by title)");
                PlayerActionBuffer.LogToDevConsole(
                    $"[CardChoiceScreenPatch] Pushed ReplayCardRewardChooseSelector for card reward '{cardTitle}'.");
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

        // When the selection is part of a card reward, TakeCardReward records
        // the selection — don't also emit SelectCardFromScreen.
        if (BattleRewardPatch.IsProcessingCardReward)
        {
            PlayerActionBuffer.LogToDevConsole(
                "[CardChoiceScreenPatch] Suppressed SelectCardFromScreen (card reward context).");
            return selected!;
        }

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

        string command = $"SelectCardFromScreen {index}";
        PlayerActionBuffer.LogToDevConsole(
            $"[CardChoiceScreenPatch] Recording: {command}");
        PlayerActionBuffer.Record(command);

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
    internal static void FlushIfPending()
    {
    }
}

// ── Replay selectors ─────────────────────────────────────────────────────────

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

/// <summary>
/// ICardSelector for card rewards that route through FromChooseACardScreen
/// (e.g. Morphic Grove).  Picks the card matching the title from the
/// TakeCardReward command and consumes it.
/// </summary>
internal sealed class ReplayCardRewardChooseSelector : ICardSelector
{
    private readonly string _expectedTitle;

    public ReplayCardRewardChooseSelector(string expectedTitle)
        => _expectedTitle = expectedTitle;

    public Task<IEnumerable<CardModel>> GetSelectedCards(
        IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        var scope = FromChooseACardScreenPatch._pendingScope;
        FromChooseACardScreenPatch._pendingScope = null;
        scope?.Dispose();

        var optionList = options.ToList();
        var match = optionList.FirstOrDefault(c => c.Title == _expectedTitle);

        if (match != null)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[ReplayCardRewardChooseSelector] Selected '{match.Title}' by title match.");
            ReplayDispatcher.DispatchNow();
            return Task.FromResult<IEnumerable<CardModel>>(new[] { match });
        }

        PlayerActionBuffer.LogToDevConsole(
            $"[ReplayCardRewardChooseSelector] Card '{_expectedTitle}' not found in options.");
        ReplayDispatcher.DispatchNow();
        return Task.FromResult(Enumerable.Empty<CardModel>());
    }

    public CardModel? GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
        => null;
}
