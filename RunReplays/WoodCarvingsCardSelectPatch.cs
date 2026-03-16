using System;
using System.Collections.Generic;
using System.IO;
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
internal static class SelectorStackDebug
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SlayTheSpire2", "RunReplays", "selector_stack_debug.log");

    internal static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n"); }
        catch { /* ignore */ }
    }

    internal static void Clear()
    {
        try { File.WriteAllText(LogPath, ""); }
        catch { /* ignore */ }
    }
}

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

        SelectorStackDebug.Log("FromDeckGeneric.Prefix called (IsActive=" + ReplayEngine.IsActive + ")");
        if (ReplayEngine.IsActive)
        {
            if (ReplayEngine.PeekSelectDeckCard(out _))
            {
                _pendingScope = CardSelectCmd.PushSelector(new ReplayDeckCardSelector());
                SelectorStackDebug.Log("PUSH FromDeckGeneric");
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

        SelectorStackDebug.Log("FromDeckForEnchantment.Prefix called (IsActive=" + ReplayEngine.IsActive + ")");
        if (ReplayEngine.IsActive)
        {
            // Skip if the filter overload already pushed a selector (one overload
            // may delegate to the other, causing both Harmony prefixes to fire).
            if (FromDeckForEnchantmentWithFilterPatch._pendingScope != null)
            {
                SelectorStackDebug.Log("SKIP FromDeckForEnchantment (EnchantFilter already pushed)");
            }
            else if (ReplayEngine.PeekSelectDeckCard(out _))
            {
                _pendingScope = CardSelectCmd.PushSelector(new ReplayDeckCardSelector());
                SelectorStackDebug.Log("PUSH FromDeckForEnchantment");
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

        SelectorStackDebug.Log("FromDeckForEnchantmentWithFilter.Prefix called (IsActive=" + ReplayEngine.IsActive + ")");
        if (ReplayEngine.IsActive)
        {
            // Skip if the no-filter overload already pushed a selector (the game
            // routes FromDeckForEnchantment → FromDeckForEnchantment(filter), so
            // both Harmony prefixes fire for a single call).
            if (FromDeckForEnchantmentPatch._pendingScope != null)
            {
                SelectorStackDebug.Log("SKIP FromDeckForEnchantmentWithFilter (Enchant already pushed)");
            }
            else if (ReplayEngine.PeekSelectDeckCard(out _))
            {
                _pendingScope = CardSelectCmd.PushSelector(new ReplayDeckCardSelector());
                SelectorStackDebug.Log("PUSH FromDeckForEnchantmentWithFilter");
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

        SelectorStackDebug.Log("FromDeckForTransformation.Prefix called (IsActive=" + ReplayEngine.IsActive + ")");
        if (ReplayEngine.IsActive)
        {
            if (ReplayEngine.PeekSelectDeckCard(out _))
            {
                _pendingScope = CardSelectCmd.PushSelector(new ReplayDeckCardSelector());
                SelectorStackDebug.Log("PUSH FromDeckForTransformation");
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

// ── FromDeckForUpgrade (SMITH rest-site option) ──────────────────────────────

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForUpgrade))]
public static class FromDeckForUpgradePatch
{
    internal static IDisposable? _pendingScope;

    [HarmonyPrefix]
    public static void Prefix()
    {
        _pendingScope?.Dispose();
        _pendingScope = null;

        SelectorStackDebug.Log("FromDeckForUpgrade.Prefix called (IsActive=" + ReplayEngine.IsActive + ")");
        if (!ReplayEngine.IsActive)
            return;

        if (ReplayEngine.PeekUpgradeCard(out _))
        {
            _pendingScope = CardSelectCmd.PushSelector(new ReplayDeckCardSelector());
            SelectorStackDebug.Log("PUSH FromDeckForUpgrade");
            PlayerActionBuffer.LogToDevConsole(
                "[UpgradePatch] Pushed ReplayDeckCardSelector for FromDeckForUpgrade.");
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

        // Collect all selected indices into a single command so that
        // multi-card selections (e.g. Morphic Grove) are recorded atomically.
        var indices = new List<int>(cardList.Count);
        foreach (CardModel card in cardList)
        {
            int index = deckList == null ? -1 : deckList.ToList().IndexOf(card);
            indices.Add(index);
        }

        // Buffer the minimal-log command rather than recording it immediately.
        // If this selection was triggered by a PlayCardAction (combat card effect,
        // e.g. Seeker Strike), the buffer is flushed by PlayerActionBuffer after
        // AfterActionExecuted fires for that action, ensuring correct log order.
        // If triggered by an event option (Wood Carvings), EventOptionChosenLogPatch
        // flushes the buffer before recording the next ChooseEventOption.
        string command = $"SelectDeckCard {string.Join(" ", indices)}";
        CardEffectDeckSelectContext.Buffer(command);
        PlayerActionBuffer.LogToDevConsole(
            $"[DeckCardSelectPatch] Buffered {command} ({titles}).");
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
        string scopeSource = FromDeckGenericPatch._pendingScope != null ? "Generic"
            : FromDeckForEnchantmentPatch._pendingScope != null ? "Enchant"
            : FromDeckForEnchantmentWithFilterPatch._pendingScope != null ? "EnchantFilter"
            : FromDeckForTransformationPatch._pendingScope != null ? "Transform"
            : FromDeckForUpgradePatch._pendingScope != null ? "Upgrade"
            : "NONE";
        SelectorStackDebug.Log($"GetSelectedCards called (scope from: {scopeSource}, minSelect={minSelect}, maxSelect={maxSelect})");
        PlayerActionBuffer.LogToDevConsole(
            "[ReplayDeckCardSelector] GetSelectedCards called.");

        // Consume the scope that was pushed by whichever patch created us.
        var scope = FromDeckGenericPatch._pendingScope
            ?? FromDeckForEnchantmentPatch._pendingScope
            ?? FromDeckForEnchantmentWithFilterPatch._pendingScope
            ?? FromDeckForTransformationPatch._pendingScope
            ?? FromDeckForUpgradePatch._pendingScope;
        FromDeckGenericPatch._pendingScope                 = null;
        FromDeckForEnchantmentPatch._pendingScope          = null;
        FromDeckForEnchantmentWithFilterPatch._pendingScope = null;
        FromDeckForTransformationPatch._pendingScope       = null;
        FromDeckForUpgradePatch._pendingScope              = null;
        SelectorStackDebug.Log($"Disposing scope (wasNull={scope == null})");
        scope?.Dispose();

        var optionList = options.ToList();

        if (!ReplayEngine.ConsumeSelectDeckCard(out int[] indices))
        {
            // Fallback: SMITH rest-site upgrades record "UpgradeCard {index}" but
            // during replay the flow goes through CardSelectCmd, not ShowScreen.
            if (ReplayEngine.ConsumeUpgradeCard(out int upgradeIdx))
            {
                indices = new[] { upgradeIdx };
                PlayerActionBuffer.LogToDevConsole(
                    $"[ReplayDeckCardSelector] Consumed UpgradeCard {upgradeIdx} (SMITH upgrade via CardSelectCmd).");
            }
            else
            {
                PlayerActionBuffer.LogToDevConsole(
                    "[ReplayDeckCardSelector] No SelectDeckCard/UpgradeCard command — returning first available card(s).");
                return Task.FromResult<IEnumerable<CardModel>>(
                    optionList.Take(Math.Max(1, minSelect)).ToList());
            }
        }

        var selected = new List<CardModel>(indices.Length);
        foreach (int index in indices)
        {
            if (index >= 0 && index < optionList.Count)
            {
                selected.Add(optionList[index]);
                PlayerActionBuffer.LogToDevConsole(
                    $"[ReplayDeckCardSelector] Selected '{optionList[index].Title}' at index {index}.");
            }
            else
            {
                PlayerActionBuffer.LogToDevConsole(
                    $"[ReplayDeckCardSelector] Index {index} out of range (count={optionList.Count}) — skipping.");
            }
        }

        // Some relics (e.g. Yummy Cookie) allow upgrading multiple cards in
        // a single selection.  Keep consuming UpgradeCard commands while they
        // match available options.
        while (ReplayEngine.PeekUpgradeCard(out int nextIdx)
               && nextIdx >= 0 && nextIdx < optionList.Count)
        {
            ReplayEngine.ConsumeUpgradeCard(out _);
            selected.Add(optionList[nextIdx]);
            PlayerActionBuffer.LogToDevConsole(
                $"[ReplayDeckCardSelector] Selected additional '{optionList[nextIdx].Title}' at index {nextIdx}.");
        }

        if (selected.Count > 0)
            return Task.FromResult<IEnumerable<CardModel>>(selected);

        PlayerActionBuffer.LogToDevConsole(
            "[ReplayDeckCardSelector] No valid indices — falling back.");
        return Task.FromResult<IEnumerable<CardModel>>(
            optionList.Take(Math.Max(1, minSelect)).ToList());
    }

    public CardModel? GetSelectedCardReward(
        IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
        => null;
}
