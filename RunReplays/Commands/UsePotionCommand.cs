using System;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;

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

    public string PotionDescription { get; }
    public uint PotionIndex { get; }
    public uint? TargetId { get; }
    public string TargetDescription { get; }
    public bool InCombat { get; }


    private UsePotionCommand(string raw, string potionDescription, uint potionIndex, uint? targetId, string targetDescription, bool inCombat) : base(raw)
    {
        PotionDescription = potionDescription;
        PotionIndex = potionIndex;
        TargetId = targetId;
        TargetDescription = targetDescription;
        InCombat = inCombat;
    }

    public override string ToString()
        => $"{Prefix}{PotionDescription}{IndexMarker}{PotionIndex}{TargetMarker}{TargetId} ({TargetDescription}){CombatMarker}{InCombat}";

    public override string Describe()
    {
        string target = TargetId.HasValue ? $" targeting id={TargetId}" : "";
        return $"use potion slot={PotionIndex}{target} combat={InCombat}";
    }

    public override bool BlocksDuringCombatStartup => true;

    public override ExecuteResult Execute()
    {
        // Wait until the game is in the play phase before using a combat potion.
        // Using it too early (e.g. during combat intro at high speed) breaks the UI.
        if (InCombat)
        {
            var combat = MegaCrit.Sts2.Core.Combat.CombatManager.Instance;
            if (combat == null || !combat.IsPlayPhase)
                return ExecuteResult.Retry(200);
        }

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
            // Only block dispatch for in-combat potions where AfterActionExecuted
            // will fire via the ActionExecutor subscription.  Out-of-combat potions
            // (e.g. used before TurnStarted) may not have an ActionExecutor yet.
            if (InCombat)
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

        // Extract creature name between "(" and ")" after the target id
        string targetDescription = "";
        int closeParenIdx = raw.IndexOf(')', openParenIdx + 2);
        if (closeParenIdx > openParenIdx)
            targetDescription = raw.Substring(openParenIdx + 2, closeParenIdx - openParenIdx - 2);

        var combatSpan = raw.AsSpan(combatIdx + CombatMarker.Length).Trim();
        bool inCombat = combatSpan.Equals("True", StringComparison.OrdinalIgnoreCase);

        // Extract potion description (netId + potionName) between prefix and index marker
        string potionDescription = raw.Substring(Prefix.Length, indexIdx - Prefix.Length);

        return new UsePotionCommand(raw, potionDescription, potionIndex, targetId, targetDescription, inCombat);
    }
}
