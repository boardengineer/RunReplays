using System;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Helpers;
using RunReplays.Patch;

namespace RunReplays.Commands;

/// <summary>
///     Discard a potion from the player's potion belt.
///     Recorded as: "NetDiscardPotionGameAction for player {netId} potion slot: {slotIndex}"
/// </summary>
public sealed class DiscardPotionCommand : ReplayCommand
{
    private const string Prefix = "NetDiscardPotionGameAction for player ";
    private const string SlotMarker = " potion slot: ";


    private DiscardPotionCommand(string raw, int slotIndex) : base(raw)
    {
        SlotIndex = slotIndex;
    }

    public int SlotIndex { get; }

    public override string Describe()
    {
        return $"discard potion slot={SlotIndex}";
    }

    public override ExecuteResult Execute()
    {
        var player = CardPlayReplayPatch.ResolveLocalPlayer();
        if (player == null)
        {
            PlayerActionBuffer.LogToDevConsole("[DiscardPotionCommand] Could not resolve local player.");
            return ExecuteResult.Retry(200);
        }

        var potion = player.GetPotionAtSlotIndex(SlotIndex);
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
        if (!raw.StartsWith(Prefix))
            return null;

        var markerPos = raw.LastIndexOf(SlotMarker);
        if (markerPos < 0) return null;

        var afterMarker = raw.AsSpan(markerPos + SlotMarker.Length);
        var spaceIdx = afterMarker.IndexOf(' ');
        var slotSpan = spaceIdx >= 0 ? afterMarker[..spaceIdx] : afterMarker;

        if (!int.TryParse(slotSpan, out var slotIndex))
            return null;

        return new DiscardPotionCommand(raw, slotIndex);
    }
}