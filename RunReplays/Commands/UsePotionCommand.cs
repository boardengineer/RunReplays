using System;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

using RunReplays.Patches.Replay;
namespace RunReplays.Commands;

/// <summary>
/// Use a potion from the player's potion belt.
/// Recorded as: "UsePotion {slotIndex} {targetId} # {potionName}"
///          or: "UsePotion {slotIndex} # {potionName}" (no target)
/// </summary>
public sealed class UsePotionCommand : ReplayCommand
{
    private const string Prefix = "UsePotion ";

    public uint PotionIndex { get; }
    public uint? TargetId { get; }

    public UsePotionCommand(uint potionIndex, uint? targetId = null) : base("")
    {
        PotionIndex = potionIndex;
        TargetId = targetId;
    }

    public override string ToString()
        => TargetId.HasValue
            ? $"{Prefix}{PotionIndex} {TargetId.Value}"
            : $"{Prefix}{PotionIndex}";

    public override string Describe()
    {
        string target = TargetId.HasValue ? $" targeting id={TargetId}" : "";
        return $"use potion slot={PotionIndex}{target}";
    }

    public override bool BlocksDuringCombatStartup => true;

    public override ExecuteResult Execute()
    {
        // Wait until the game is in the play phase before using a combat potion.
        var combat = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
        if (combat != null && !combat.IsPlayPhase)
            return ExecuteResult.Retry(200);

        Player? player = CardPlayReplayPatch.ResolveLocalPlayer();
        if (player == null)
        {
            PlayerActionBuffer.LogToDevConsole("[UsePotionCommand] Could not resolve local player.");
            return ExecuteResult.Retry(200);
        }

        PotionModel? potion = player.GetPotionAtSlotIndex((int)PotionIndex);
        if (potion == null)
        {
            PlayerActionBuffer.LogToDevConsole($"[UsePotionCommand] No potion at slot {PotionIndex}.");
            return ExecuteResult.Retry(200);
        }

        Creature? target = null;
        if (TargetId.HasValue)
        {
            target = CardPlayReplayPatch._currentCombatState?.GetCreature(TargetId);
        }

        // Default to self when no target is specified.
        if (target == null)
            target = player.Creature;

        try
        {
            if (combat != null)
                ReplayState.PotionInFlight = true;

            potion.EnqueueManualUse(target);
        }
        catch (Exception ex)
        {
            ReplayState.PotionInFlight = false;
            PlayerActionBuffer.LogToDevConsole(
                $"[UsePotionCommand] EnqueueManualUse threw {ex.GetType().Name}: {ex.Message}");
        }

        return ExecuteResult.Ok();
    }

    public static UsePotionCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        var parts = raw.Substring(Prefix.Length).Trim().Split(' ');
        if (parts.Length >= 1 && uint.TryParse(parts[0], out uint slot))
        {
            uint? target = null;
            if (parts.Length >= 2 && uint.TryParse(parts[1], out uint tid))
                target = tid;
            return new UsePotionCommand(slot, target);
        }
        return null;
    }
}
