using System;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

using RunReplays.Patches;
using RunReplays.Patches.Replay;
namespace RunReplays.Commands;

/// <summary>
/// Use a potion from the player's potion belt.
/// Recorded as: "UsePotionAction {netId} {potionName} index: {potionIndex} target: {targetId} ({creatureName}) combat: {bool}"
/// </summary>
public sealed class UsePotionCommand : ReplayCommand
{
    private const string Prefix = "UsePotionAction ";
    private const string IndexMarker = " index: ";
    private const string TargetMarker = " target: ";
    private const string CombatMarker = " combat: ";

    public uint PotionIndex { get; }
    public uint? TargetId { get; }
    public bool InCombat { get; }


    private UsePotionCommand(string raw, uint potionIndex, uint? targetId, bool inCombat) : base(raw)
    {
        PotionIndex = potionIndex;
        TargetId = targetId;
        InCombat = inCombat;
    }

    public override string Describe()
    {
        string target = TargetId.HasValue ? $" targeting id={TargetId}" : "";
        return $"use potion slot={PotionIndex}{target} combat={InCombat}";
    }

    public override ExecuteResult Execute()
    {
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
            PlayerActionBuffer.LogToDevConsole(
                $"[UsePotionCommand] Resolved target id={TargetId} → {(target == null ? "null" : target.ToString())}.");
        }

        if (!InCombat && target == null)
            target = player.Creature;

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

        int combatIdx = raw.LastIndexOf(CombatMarker, StringComparison.Ordinal);
        if (combatIdx < 0) return null;

        int targetIdx = raw.LastIndexOf(TargetMarker, combatIdx, StringComparison.Ordinal);
        if (targetIdx < 0) return null;

        int indexIdx = raw.LastIndexOf(IndexMarker, targetIdx, StringComparison.Ordinal);
        if (indexIdx < 0) return null;

        var indexSpan = raw.AsSpan(
            indexIdx + IndexMarker.Length,
            targetIdx - indexIdx - IndexMarker.Length).Trim();
        if (!uint.TryParse(indexSpan, out uint potionIndex)) return null;

        int openParenIdx = raw.IndexOf(" (", targetIdx + TargetMarker.Length, StringComparison.Ordinal);
        if (openParenIdx < 0) return null;

        uint? targetId = null;
        var targetSpan = raw.AsSpan(
            targetIdx + TargetMarker.Length,
            openParenIdx - targetIdx - TargetMarker.Length).Trim();
        if (targetSpan.Length > 0 && uint.TryParse(targetSpan, out uint tid))
            targetId = tid;

        var combatSpan = raw.AsSpan(combatIdx + CombatMarker.Length).Trim();
        bool inCombat = combatSpan.Equals("True", StringComparison.OrdinalIgnoreCase);

        return new UsePotionCommand(raw, potionIndex, targetId, inCombat);
    }
}
