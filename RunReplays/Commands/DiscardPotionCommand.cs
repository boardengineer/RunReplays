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
/// Recorded as: "NetDiscardPotionGameAction for player {netId} potion slot: {slotIndex}"
/// </summary>
public sealed class DiscardPotionCommand : ReplayCommand
{
    private const string Prefix = "NetDiscardPotionGameAction for player ";
    private const string SlotMarker = " potion slot: ";

    public string PlayerInfo { get; }
    public int SlotIndex { get; }


    private DiscardPotionCommand(string raw, string playerInfo, int slotIndex) : base(raw)
    {
        PlayerInfo = playerInfo;
        SlotIndex = slotIndex;
    }

    public override string ToString()
        => $"{Prefix}{PlayerInfo}{SlotMarker}{SlotIndex}";

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
        if (!raw.StartsWith(Prefix))
            return null;

        int markerPos = raw.LastIndexOf(SlotMarker);
        if (markerPos < 0) return null;

        var afterMarker = raw.AsSpan(markerPos + SlotMarker.Length);
        int spaceIdx = afterMarker.IndexOf(' ');
        var slotSpan = spaceIdx >= 0 ? afterMarker[..spaceIdx] : afterMarker;

        if (!int.TryParse(slotSpan, out int slotIndex))
            return null;

        // Extract the player info between the prefix and the slot marker
        string playerInfo = raw.Substring(Prefix.Length, markerPos - Prefix.Length);

        return new DiscardPotionCommand(raw, playerInfo, slotIndex);
    }
}
