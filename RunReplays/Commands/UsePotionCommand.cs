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
        Player? player = CardPlayReplayPatch.ResolveLocalPlayer();
        if (player == null)
        {
            PlayerActionBuffer.LogToDevConsole("[UsePotionCommand] Could not resolve local player.");
            return ExecuteResult.Retry(200);
        }

        // During combat, wait until the play phase before using a potion.
        // Outside combat there is no phase to wait on — potions can be used
        // any time (e.g. Foul Potion thrown at the shopkeep).
        if (MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress
            && player.PlayerCombatState?.Phase != MegaCrit.Sts2.Core.Combat.PlayerTurnPhase.Play)
            return ExecuteResult.Retry(200);

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
            if (target == null)
            {
                // Recorded target not in combat (yet) — retry instead of
                // misfiring the potion at something else.
                PlayerActionBuffer.LogToDevConsole(
                    $"[UsePotionCommand] Target id={TargetId} not found — retrying.");
                return ExecuteResult.Retry(200);
            }
        }

        // No recorded target → pass null. EnqueueManualUse self-targets only
        // when that is valid for the potion's TargetType. Forcing self here
        // made UsePotionAction cancel non-targeted potions (e.g. the
        // AllEnemies Explosive Ampoule fizzled with 0 damage on replay).

        try
        {
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
