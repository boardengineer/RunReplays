using System;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;

using RunReplays.Patches;
using RunReplays.Patches.Replay;
namespace RunReplays.Commands;

/// <summary>
/// Discard a potion from the player's potion belt.
/// Recorded as: "DiscardPotion {slotIndex}"
/// Legacy:      "NetDiscardPotionGameAction for player {netId} potion slot: {slotIndex}"
/// </summary>
public sealed class DiscardPotionCommand : ReplayCommand
{
    private const string Prefix = "DiscardPotion ";
    private const string LegacyPrefix = "NetDiscardPotionGameAction for player ";
    private const string LegacySlotMarker = " potion slot: ";

    public int SlotIndex { get; }

    public DiscardPotionCommand(int slotIndex) : base("")
    {
        SlotIndex = slotIndex;
    }

    public override string ToString() => $"{Prefix}{SlotIndex}";

    public override string Describe() => $"discard potion slot={SlotIndex}";

    public override ExecuteResult Execute()
    {
        Player? player = CardPlayReplayPatch.ResolveLocalPlayer();
        if (player == null)
        {
            PlayerActionBuffer.LogToDevConsole("[DiscardPotionCommand] Could not resolve local player.");
            return ExecuteResult.Retry(200);
        }

        PotionModel? potion = player.GetPotionAtSlotIndex(SlotIndex);
        if (potion == null)
        {
            PlayerActionBuffer.LogToDevConsole($"[DiscardPotionCommand] No potion at slot {SlotIndex}.");
            return ExecuteResult.Retry(200);
        }

        try
        {
            TaskHelper.RunSafely(PotionCmd.Discard(potion));
        }
        catch (Exception ex)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[DiscardPotionCommand] PotionCmd.Discard threw {ex.GetType().Name}: {ex.Message}");
        }

        return ExecuteResult.Ok();
    }

    public static DiscardPotionCommand? TryParse(string raw)
    {
        // New format: "DiscardPotion {slotIndex}"
        if (raw.StartsWith(Prefix) && !raw.StartsWith(LegacyPrefix))
        {
            if (int.TryParse(raw.AsSpan(Prefix.Length).Trim(), out int slot))
                return new DiscardPotionCommand(slot);
            return null;
        }

        // Legacy: "NetDiscardPotionGameAction for player {id} potion slot: {slot}"
        if (raw.StartsWith(LegacyPrefix))
        {
            int markerPos = raw.LastIndexOf(LegacySlotMarker);
            if (markerPos < 0) return null;

            var afterMarker = raw.AsSpan(markerPos + LegacySlotMarker.Length);
            int spaceIdx = afterMarker.IndexOf(' ');
            var slotSpan = spaceIdx >= 0 ? afterMarker[..spaceIdx] : afterMarker;

            if (int.TryParse(slotSpan, out int slotIndex))
                return new DiscardPotionCommand(slotIndex);
        }

        return null;
    }
}
