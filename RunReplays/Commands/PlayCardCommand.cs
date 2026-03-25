using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

namespace RunReplays.Commands;

/// <summary>
/// Play a card from the hand.
/// Recorded as: "PlayCardAction card: {CardModel} index: {CombatCardIndex} targetid: {TargetId}"
/// </summary>
public class PlayCardCommand : ReplayCommand
{
    private const string Prefix = "PlayCardAction ";
    private const string IndexMarker = " index: ";
    private const string TargetMarker = " targetid: ";

    public uint CombatCardIndex { get; }
    public uint? TargetId { get; }

    public override ReplayState.ReadyState RequiredState => ReplayState.ReadyState.Combat;

    private PlayCardCommand(string raw, uint combatCardIndex, uint? targetId) : base(raw)
    {
        CombatCardIndex = combatCardIndex;
        TargetId = targetId;
    }

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
        if (!raw.StartsWith(Prefix))
            return null;

        int targetIdx = raw.LastIndexOf(TargetMarker, System.StringComparison.Ordinal);
        int indexIdx = raw.LastIndexOf(IndexMarker, System.StringComparison.Ordinal);

        if (targetIdx < 0 || indexIdx < 0 || indexIdx >= targetIdx)
            return null;

        var indexStr = raw.AsSpan(
            indexIdx + IndexMarker.Length,
            targetIdx - indexIdx - IndexMarker.Length).Trim();

        if (!uint.TryParse(indexStr, out uint combatCardIndex))
            return null;

        uint? targetId = null;
        var targetStr = raw.AsSpan(targetIdx + TargetMarker.Length).Trim();
        if (targetStr.Length > 0 && uint.TryParse(targetStr, out uint tid))
            targetId = tid;

        return new PlayCardCommand(raw, combatCardIndex, targetId);
    }
}
