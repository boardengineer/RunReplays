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

namespace RunReplays.Patch;
using RunReplays;

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
    }

    internal static void Clear()
    {
    }
}

internal static class DeckCardSelectContext
{
    internal static bool Pending;
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
        if (!ReplayEngine.IsActive)
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
    }
}

// ── FromDeckForEnchantment (with explicit card list — e.g. Sapphire Seed) ────

[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForEnchantment),
    new[] { typeof(IReadOnlyList<CardModel>), typeof(EnchantmentModel), typeof(int), typeof(CardSelectorPrefs) })]
public static class FromDeckForEnchantmentWithCardsPatch
{
    internal static IDisposable? _pendingScope;

    [HarmonyPrefix]
    public static void Prefix()
    {
        _pendingScope?.Dispose();
        _pendingScope = null;
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

        // When a deck removal is pending (Empty Cage, Cook, etc.), record as
        // a single combined RemoveCardFromDeck command.
        string command;
        if (DeckRemovalState.PendingRemoval)
        {
            DeckRemovalState.PendingRemoval = false;
            command = $"RemoveCardFromDeck: {string.Join(" ", indices)}";
            PlayerActionBuffer.Record(command);
        }
        else
        {
            command = $"SelectDeckCard {string.Join(" ", indices)}";
            PlayerActionBuffer.RecordMinimalOnly(command);
        }
        PlayerActionBuffer.LogToDevConsole(
            $"[DeckCardSelectPatch] Recorded {command} ({titles}).");
    }
}