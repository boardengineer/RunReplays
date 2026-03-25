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

namespace RunReplays.Patches;
using RunReplays;
using RunReplays.Commands;

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

        if (!ReplayEngine.IsActive)
        {
            _recordingCards = cards.ToList();
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
            return selected;
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

        var cmd = new SelectCardFromScreenCommand(index);
        PlayerActionBuffer.LogToDevConsole(
            $"[CardChoiceScreenPatch] Recording: {cmd}");
        PlayerActionBuffer.Record(cmd.ToString());

        return selected!;
    }
}