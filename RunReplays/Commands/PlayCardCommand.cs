using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

using RunReplays.Patches;
using RunReplays.Patches.Replay;
namespace RunReplays.Commands;

/// <summary>
/// Play a card from the hand.
/// Recorded as: "PlayCard {combatCardIndex} {targetId} # {cardDescription}"
/// Legacy:      "PlayCardAction card: {cardDescription} index: {combatCardIndex} targetid: {targetId}"
/// </summary>
public class PlayCardCommand : ReplayCommand
{
    private const string Prefix = "PlayCard ";
    private const string LegacyPrefix = "PlayCardAction ";
    private const string LegacyIndexMarker = " index: ";
    private const string LegacyTargetMarker = " targetid: ";

    public uint CombatCardIndex { get; }
    public uint? TargetId { get; }

    public PlayCardCommand(uint combatCardIndex, uint? targetId = null) : base("")
    {
        CombatCardIndex = combatCardIndex;
        TargetId = targetId;
    }

    public override string ToString()
        => TargetId.HasValue
            ? $"{Prefix}{CombatCardIndex} {TargetId.Value}"
            : $"{Prefix}{CombatCardIndex}";

    public override string Describe()
    {
        string target = TargetId.HasValue ? $" targeting id={TargetId}" : "";
        return $"play card index={CombatCardIndex}{target}";
    }

    public override ExecuteResult Execute()
    {
        PlayerActionBuffer.LogDispatcher("Should execute card....");
        if (!CardPlayReplayPatch.IsCombatReady())
        {
            PlayerActionBuffer.LogDispatcher("Combat not ready to play cards, retrying in 100 ms");
            return ExecuteResult.Retry(100);
        }

        CardModel? card;
        try
        {
            card = NetCombatCardDb.Instance.GetCard(CombatCardIndex);
            PlayerActionBuffer.LogDispatcher($"[RunReplays] TryPlayNextCard: resolved card '{card}' from index {CombatCardIndex}.");
        }
        catch (Exception ex)
        {
            PlayerActionBuffer.LogToDevConsole(
                $"[RunReplays] TryPlayNextCard: GetCard({CombatCardIndex}) threw {ex.GetType().Name}: {ex.Message}");
            return ExecuteResult.Retry(100);
        }

        PlayerActionBuffer.LogDispatcher("Found card to play");

        Creature? target = null;
        if (TargetId.HasValue)
        {
            target = CardPlayReplayPatch._currentCombatState?.GetCreature(TargetId);
            PlayerActionBuffer.LogDispatcher($"[RunReplays] TryPlayNextCard: resolved target id={TargetId} → {(target == null ? "null" : target.ToString())}.");
        }

        CardPlayReplayPatch._dispatching = true;
        ReplayState.CardPlayInFlight = true;
        bool played = card.TryManualPlay(target);

        PlayerActionBuffer.LogDispatcher($"Card play returning {played}");
        if (played)
            return ExecuteResult.Ok();
        return ExecuteResult.Retry(100);
    }

    public static PlayCardCommand? TryParse(string raw)
    {
        // New format: "PlayCard {combatCardIndex}" or "PlayCard {combatCardIndex} {targetId}"
        if (raw.StartsWith(Prefix) && !raw.StartsWith(LegacyPrefix))
        {
            var parts = raw.Substring(Prefix.Length).Trim().Split(' ');
            if (parts.Length >= 1 && uint.TryParse(parts[0], out uint cardIdx))
            {
                uint? target = null;
                if (parts.Length >= 2 && uint.TryParse(parts[1], out uint tid))
                    target = tid;
                return new PlayCardCommand(cardIdx, target);
            }
            return null;
        }

        // Legacy format: "PlayCardAction card: {desc} index: {idx} targetid: {tid}"
        if (!raw.StartsWith(LegacyPrefix))
            return null;

        int targetIdx = raw.LastIndexOf(LegacyTargetMarker, System.StringComparison.Ordinal);
        int indexIdx = raw.LastIndexOf(LegacyIndexMarker, System.StringComparison.Ordinal);

        if (targetIdx < 0 || indexIdx < 0 || indexIdx >= targetIdx)
            return null;

        var indexStr = raw.AsSpan(
            indexIdx + LegacyIndexMarker.Length,
            targetIdx - indexIdx - LegacyIndexMarker.Length).Trim();

        if (!uint.TryParse(indexStr, out uint combatCardIndex))
            return null;

        uint? targetId = null;
        var targetStr = raw.AsSpan(targetIdx + LegacyTargetMarker.Length).Trim();
        if (targetStr.Length > 0 && uint.TryParse(targetStr, out uint legacyTid))
            targetId = legacyTid;

        // Extract card description for comment
        const string cardMarker = "card: ";
        int cardStart = raw.IndexOf(cardMarker, LegacyPrefix.Length, System.StringComparison.Ordinal);
        string? cardDescription = null;
        if (cardStart >= 0)
        {
            cardStart += cardMarker.Length;
            cardDescription = raw.Substring(cardStart, indexIdx - cardStart).Trim();
        }

        return new PlayCardCommand(combatCardIndex, targetId) { Comment = cardDescription };
    }
}
