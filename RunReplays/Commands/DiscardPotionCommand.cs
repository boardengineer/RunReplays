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
/// </summary>
public sealed class DiscardPotionCommand : ReplayCommand
{
    private const string Prefix = "DiscardPotion ";

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
        if (!raw.StartsWith(Prefix))
            return null;

        if (int.TryParse(raw.AsSpan(Prefix.Length).Trim(), out int slot))
            return new DiscardPotionCommand(slot);
        return null;
    }
}
