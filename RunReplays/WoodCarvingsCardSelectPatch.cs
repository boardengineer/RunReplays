using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using ICardSelector = MegaCrit.Sts2.Core.TestSupport.ICardSelector;

namespace RunReplays;

/// <summary>
/// Records and replays deck card selections made during events that call
/// CardSelectCmd.FromDeckGeneric (Wood Carvings Bird / Torus options) or
/// CardSelectCmd.FromDeckForEnchantment (Wood Carvings Snake option).
///
/// Command format:  "SelectDeckCard {deckIndex}"
/// The index is the 0-based position in the card list shown to the player.
///
/// Recording: a Pending flag is set on entry to FromDeckGeneric /
/// FromDeckForEnchantment; NCardGridSelectionScreen.CardsSelected records the
/// deck index of each chosen card when the flag is set.
///
/// Replay: a ReplayDeckCardSelector is pushed onto the CardSelectCmd selector
/// stack before From* runs, so the game uses replay data instead of opening UI.
/// </summary>
internal static class DeckCardSelectContext
{
    internal static bool Pending;
}

/// <summary>
/// Buffers "SelectDeckCard {n}" commands that arise from deck card selections
/// triggered by card effects during combat (e.g. Seeker Strike).
///
/// The buffer is necessary because NCardGridSelectionScreen.CardsSelected resolves
/// (and RecordAsync fires) while the parent PlayCardAction is still executing, so
/// recording immediately would place SelectDeckCard before PlayCardAction in the
/// minimal log.  Instead the command is held here and flushed by PlayerActionBuffer
/// once AfterActionExecuted fires for the PlayCardAction.
///
/// For event-triggered selections (Wood Carvings) there is no PlayCardAction, so
/// EventOptionChosenLogPatch flushes the buffer before recording the next event
/// option, preserving the correct ordering there as well.
/// </summary>
internal static class CardEffectDeckSelectContext
{
    private static readonly List<string> _buffered = new();

    internal static void Buffer(string cmd) => _buffered.Add(cmd);

    internal static void FlushIfPending()
    {
        if (_buffered.Count == 0)
            return;

        foreach (string cmd in _buffered)
            PlayerActionBuffer.RecordMinimalOnly(cmd);

        _buffered.Clear();
    }
}

// ── FromDeckGeneric ────────────────────────────────────────────────────────────

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckGeneric))]
public static class FromDeckGenericPatch
{
    internal static IDisposable? _pendingScope;

    [HarmonyPrefix]
    public static void Prefix()
    {
        _pendingScope?.Dispose();
        _pendingScope = null;

        if (ReplayEngine.IsActive)
        {
            // SkipToSelectDeckCard drains any interleaved auto-processed commands
            // (e.g. status effects from a combat card play) that sit before the
            // SelectDeckCard command, then confirms it is at the queue front.
            // Also handle UpgradeCard commands: the SMITH rest-site option goes
            // through CardSelectCmd during replay (not NDeckUpgradeSelectScreen).
            if (ReplayEngine.SkipToSelectDeckCard() || ReplayEngine.PeekUpgradeCard(out _))
            {
                _pendingScope = CardSelectCmd.PushSelector(new ReplayDeckCardSelector());
                PlayerActionBuffer.LogToDevConsole(
                    "[WoodCarvingsPatch] Pushed ReplayDeckCardSelector for FromDeckGeneric.");
            }
        }
        else
        {
            DeckCardSelectContext.Pending = true;
        }
    }
}

// ── FromDeckForEnchantment ─────────────────────────────────────────────────────

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForEnchantment),
    new[] { typeof(Player), typeof(EnchantmentModel), typeof(int), typeof(CardSelectorPrefs) })]
public static class FromDeckForEnchantmentPatch
{
    internal static IDisposable? _pendingScope;

    [HarmonyPrefix]
    public static void Prefix()
    {
        _pendingScope?.Dispose();
        _pendingScope = null;

        if (ReplayEngine.IsActive)
        {
            if (ReplayEngine.SkipToSelectDeckCard() || ReplayEngine.PeekUpgradeCard(out _))
            {
                _pendingScope = CardSelectCmd.PushSelector(new ReplayDeckCardSelector());
                PlayerActionBuffer.LogToDevConsole(
                    "[WoodCarvingsPatch] Pushed ReplayDeckCardSelector for FromDeckForEnchantment.");
            }
        }
        else
        {
            DeckCardSelectContext.Pending = true;
        }
    }
}

// ── FromDeckForEnchantment (with filter) ──────────────────────────────────────

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForEnchantment),
    new[] { typeof(Player), typeof(EnchantmentModel), typeof(int), typeof(Func<CardModel, bool>), typeof(CardSelectorPrefs) })]
public static class FromDeckForEnchantmentWithFilterPatch
{
    internal static IDisposable? _pendingScope;

    [HarmonyPrefix]
    public static void Prefix()
    {
        _pendingScope?.Dispose();
        _pendingScope = null;

        if (ReplayEngine.IsActive)
        {
            if (ReplayEngine.SkipToSelectDeckCard() || ReplayEngine.PeekUpgradeCard(out _))
            {
                _pendingScope = CardSelectCmd.PushSelector(new ReplayDeckCardSelector());
                PlayerActionBuffer.LogToDevConsole(
                    "[SelfHelpBookPatch] Pushed ReplayDeckCardSelector for FromDeckForEnchantment (with filter).");
            }
        }
        else
        {
            DeckCardSelectContext.Pending = true;
        }
    }
}

// ── FromDeckForTransformation (New Leaf relic) ───────────────────────────────

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForTransformation))]
public static class FromDeckForTransformationPatch
{
    internal static IDisposable? _pendingScope;

    [HarmonyPrefix]
    public static void Prefix()
    {
        _pendingScope?.Dispose();
        _pendingScope = null;

        if (ReplayEngine.IsActive)
        {
            if (ReplayEngine.SkipToSelectDeckCard())
            {
                _pendingScope = CardSelectCmd.PushSelector(new ReplayDeckCardSelector());
                PlayerActionBuffer.LogToDevConsole(
                    "[DeckTransformPatch] Pushed ReplayDeckCardSelector for FromDeckForTransformation.");
            }
        }
        else
        {
            DeckCardSelectContext.Pending = true;
        }
    }
}

// ── Recording: NCardGridSelectionScreen.CardsSelected ─────────────────────────

/// <summary>
/// Intercepts NCardGridSelectionScreen.CardsSelected when DeckCardSelectContext.Pending
/// is set. Skips NDeckUpgradeSelectScreen instances (handled by NDeckUpgradeSelectScreenLogPatch).
/// Awaits the result task and records the deck index of each selected card.
/// </summary>
[HarmonyPatch(typeof(NCardGridSelectionScreen), nameof(NCardGridSelectionScreen.CardsSelected))]
public static class DeckCardSelectRecordPatch
{
    private static readonly FieldInfo? CardsField =
        typeof(NCardGridSelectionScreen).GetField(
            "_cards", BindingFlags.NonPublic | BindingFlags.Instance);

    [HarmonyPostfix]
    public static void Postfix(NCardGridSelectionScreen __instance, Task<IEnumerable<CardModel>> __result)
    {
        if (__instance is NDeckUpgradeSelectScreen)
            return;

        if (!DeckCardSelectContext.Pending)
            return;

        DeckCardSelectContext.Pending = false;

        IReadOnlyList<CardModel>? deckList =
            CardsField?.GetValue(__instance) as IReadOnlyList<CardModel>;

        TaskHelper.RunSafely(RecordAsync(__result, deckList));
    }

    private static async Task RecordAsync(
        Task<IEnumerable<CardModel>> task,
        IReadOnlyList<CardModel>? deckList)
    {
        IEnumerable<CardModel> selected = await task;
        List<CardModel> cardList = selected.ToList();

        string titles = string.Join(", ", cardList.Select(c => $"'{c.Title}'"));
        PlayerActionBuffer.RecordVerboseOnly($"[DeckCardSelect] Selected: [{titles}]");

        foreach (CardModel card in cardList)
        {
            int index = deckList == null ? -1 : deckList.ToList().IndexOf(card);

            // Buffer the minimal-log command rather than recording it immediately.
            // If this selection was triggered by a PlayCardAction (combat card effect,
            // e.g. Seeker Strike), the buffer is flushed by PlayerActionBuffer after
            // AfterActionExecuted fires for that action, ensuring correct log order.
            // If triggered by an event option (Wood Carvings), EventOptionChosenLogPatch
            // flushes the buffer before recording the next ChooseEventOption.
            CardEffectDeckSelectContext.Buffer($"SelectDeckCard {index}");
            PlayerActionBuffer.LogToDevConsole(
                $"[DeckCardSelectPatch] Buffered SelectDeckCard {index} ('{card.Title}').");
        }
    }
}

// ── Replay selector ────────────────────────────────────────────────────────────

/// <summary>
/// ICardSelector that consumes a SelectDeckCard command and returns the card
/// at the recorded index from the options passed by the game.
/// </summary>
internal sealed class ReplayDeckCardSelector : ICardSelector
{
    public Task<IEnumerable<CardModel>> GetSelectedCards(
        IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        // Consume the scope that was pushed by whichever patch created us.
        var scope = FromDeckGenericPatch._pendingScope
            ?? FromDeckForEnchantmentPatch._pendingScope
            ?? FromDeckForEnchantmentWithFilterPatch._pendingScope
            ?? FromDeckForTransformationPatch._pendingScope;
        FromDeckGenericPatch._pendingScope                 = null;
        FromDeckForEnchantmentPatch._pendingScope          = null;
        FromDeckForEnchantmentWithFilterPatch._pendingScope = null;
        FromDeckForTransformationPatch._pendingScope       = null;
        scope?.Dispose();

        var optionList = options.ToList();

        if (!ReplayEngine.ConsumeSelectDeckCard(out int index))
        {
            // Fallback: SMITH rest-site upgrades record "UpgradeCard {index}" but
            // during replay the flow goes through CardSelectCmd, not ShowScreen.
            if (ReplayEngine.ConsumeUpgradeCard(out index))
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[ReplayDeckCardSelector] Consumed UpgradeCard {index} (SMITH upgrade via CardSelectCmd).");
            }
            else
            {
                PlayerActionBuffer.LogToDevConsole(
                    "[ReplayDeckCardSelector] No SelectDeckCard/UpgradeCard command — returning first available card(s).");
                return Task.FromResult<IEnumerable<CardModel>>(
                    optionList.Take(Math.Max(1, minSelect)).ToList());
            }
        }

        if (index >= 0 && index < optionList.Count)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[ReplayDeckCardSelector] Selected '{optionList[index].Title}' at index {index}.");
            return Task.FromResult<IEnumerable<CardModel>>(new[] { optionList[index] });
        }

        PlayerActionBuffer.LogToDevConsole(
            $"[ReplayDeckCardSelector] Index {index} out of range (count={optionList.Count}) — falling back.");
        return Task.FromResult<IEnumerable<CardModel>>(
            optionList.Take(Math.Max(1, minSelect)).ToList());
    }

    public CardModel? GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
        => null;
}
