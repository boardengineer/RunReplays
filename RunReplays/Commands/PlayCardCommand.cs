using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

using RunReplays.Patches;
using RunReplays.Patches.Replay;
namespace RunReplays.Commands;

/// <summary>
/// Play a card from the hand.
/// Recorded as: "PlayCard {combatCardIndex} {targetId} # {cardDescription}"
/// </summary>
public class PlayCardCommand : ReplayCommand
{
    private const string Prefix = "PlayCard ";

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

        card = ResolveCurrentHandCard(card);
        PlayerActionBuffer.LogDispatcher($"Found card to play: {card.Id.Entry}");

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

    private CardModel ResolveCurrentHandCard(CardModel resolved)
    {
        var hand = CardPlayReplayPatch.ResolveLocalPlayer()
            ?.PlayerCombatState
            ?.Hand
            ?.Cards;
        if (hand == null)
            return resolved;

        if (hand.Any(card => ReferenceEquals(card, resolved)))
            return resolved;

        string? recordedId = RecordedCardId();
        if (recordedId != null)
        {
            GD.Print(
                $"[RunReplays] [PlayCard] command={ToLogString()} hand=[{string.Join(", ", hand.Select(card => card.Id.Entry))}] resolved={resolved.Id.Entry}");
            var matching = hand.LastOrDefault(card => card.Id.Entry == recordedId);
            if (matching != null)
            {
                GD.Print(
                    $"[RunReplays] [PlayCard] selected recorded card {recordedId} from hand for command {ToLogString()}");
                PlayerActionBuffer.LogDispatcher(
                    $"[RunReplays] Resolved replay combat card {CombatCardIndex} via recorded hand card {recordedId}.");
                return matching;
            }
        }

        return resolved;
    }

    private string? RecordedCardId()
    {
        if (string.IsNullOrWhiteSpace(Comment))
            return null;

        const string prefix = "CARD.";
        int start = Comment.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += prefix.Length;
        int end = Comment.IndexOfAny([' ', ')'], start);
        return end > start ? Comment[start..end] : Comment[start..];
    }

    public static PlayCardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

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
}
