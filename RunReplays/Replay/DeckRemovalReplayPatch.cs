namespace RunReplays;

/// <summary>
/// Deck removal replay is now handled by RemoveCardFromDeckCommand, which
/// resolves NCardGridSelectionScreen._completionSource directly via
/// CardGridScreenCapture — the same pattern as SelectDeckCardCommand.
///
/// This file is kept as a placeholder; the old ICardSelector-based
/// DeckRemovalReplayPatch and ReplayRemoveCardSelector have been removed.
/// </summary>
internal static class DeckRemovalReplayPatchLegacy { }
