using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;

using RunReplays.Patches;
using RunReplays.Patches.Replay;
using RunReplays.Utils;
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
            LogStall($"GetCard({CombatCardIndex}) threw {ex.GetType().Name}: {ex.Message}", null);
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
        {
            if (_failedAttempts > 0)
                DiagnosticLog.Write("PlayCard",
                    $"{ToString()} recovered after {_failedAttempts} failed attempt(s).");
            return ExecuteResult.Ok();
        }

        string reason = TargetId.HasValue && target == null
            ? $"recorded target id={TargetId} not found in combat state"
            : DescribeUnplayable(card);
        LogStall($"TryManualPlay=false ({reason})", card);
        return ExecuteResult.Retry(100);
    }

    private int _failedAttempts;

    /// <summary>
    /// Writes stall context to the diagnostic log: why the play failed, plus
    /// the live hand (with combat card ids) and enemy list so a divergence
    /// from the recording is visible. Throttled — the dispatcher retries every
    /// 100ms; log the first failure, then every 50th (~5s).
    /// </summary>
    private void LogStall(string reason, CardModel? card)
    {
        _failedAttempts++;
        if (_failedAttempts != 1 && _failedAttempts % 50 != 0)
            return;

        string hand = "?", enemies = "?";
        try
        {
            var state = CardPlayReplayPatch._currentCombatState;
            if (state != null)
            {
                enemies = string.Join(", ", state.Enemies.Select(
                    e => $"{e.CombatId}:{e.Name} {e.CurrentHp}/{e.MaxHp}"));

                MegaCrit.Sts2.Core.Entities.Players.Player? me;
                try { me = LocalContext.GetMe(state); }
                catch { me = state.Players.FirstOrDefault(); }

                var cards = me?.PlayerCombatState?.Hand?.Cards;
                if (cards != null)
                    hand = string.Join(", ", cards.Select(c =>
                        NetCombatCardDb.Instance.TryGetCardId(c, out var id)
                            ? $"{id}:{c.Title}"
                            : $"?:{c.Title}"));
            }
        }
        catch { /* diagnostics must never break dispatch */ }

        string cardDesc = card == null
            ? "unresolved"
            : $"'{card.Title}' pile={SafePileType(card)}";
        DiagnosticLog.Write("PlayCard",
            $"{ToString()} stalled (attempt {_failedAttempts}): {reason}; " +
            $"card={cardDesc}; hand=[{hand}]; enemies=[{enemies}]");
    }

    private static string SafePileType(CardModel card)
    {
        try { return card.Pile?.Type.ToString() ?? "none"; }
        catch { return "?"; }
    }

    private static string DescribeUnplayable(CardModel card)
    {
        try
        {
            if (card.CanPlay(out var unplayableReason, out var preventer))
                return "CanPlay=true";
            return $"CanPlay=false reason={unplayableReason} preventer={preventer?.ToString() ?? "none"}";
        }
        catch (Exception ex)
        {
            return $"CanPlay threw {ex.GetType().Name}";
        }
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
