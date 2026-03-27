using System.Collections.Generic;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace RunReplays.Commands;

/// <summary>
/// Select a card from NCardRewardSelectionScreen, or sacrifice (Pael's Wing).
/// Recorded as: "TakeCard {index} # {cardTitle}"
///          or: "TakeCard sacrifice # {optionId}"
///
/// Follows a ClaimReward command that opened the card selection screen.
/// </summary>
public class TakeCardCommand : ReplayCommand
{
    private const string Prefix = "TakeCard ";
    private const string SacrificeKeyword = "sacrifice";
    private const string SkipKeyword = "skip";

    private static readonly FieldInfo? CardRowField =
        typeof(NCardRewardSelectionScreen).GetField(
            "_cardRow", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? ExtraOptionsField =
        typeof(NCardRewardSelectionScreen).GetField(
            "_extraOptions", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo? OnAlternateRewardSelectedMethod =
        typeof(NCardRewardSelectionScreen).GetMethod(
            "OnAlternateRewardSelected",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

    public int CardIndex { get; }
    public bool IsSacrifice { get; }
    public bool IsSkip { get; }

    public TakeCardCommand(int cardIndex) : base("")
    {
        CardIndex = cardIndex;
    }

    private TakeCardCommand(bool sacrifice, bool skip) : base("")
    {
        CardIndex = -1;
        IsSacrifice = sacrifice;
        IsSkip = skip;
    }

    public static TakeCardCommand Sacrifice() => new(sacrifice: true, skip: false);
    public static TakeCardCommand Skip() => new(sacrifice: false, skip: true);

    public override string ToString()
        => IsSacrifice ? $"{Prefix}{SacrificeKeyword}"
         : IsSkip ? $"{Prefix}{SkipKeyword}"
         : $"{Prefix}{CardIndex}";

    public override string Describe()
        => IsSacrifice ? "sacrifice card reward"
         : IsSkip ? "skip card reward"
         : $"take card [{CardIndex}]" + (Comment != null ? $" ({Comment})" : "");

    public override ExecuteResult Execute()
    {
        var screen = ReplayState.CardRewardSelectionScreen;
        if (screen == null)
            return ExecuteResult.Retry(200);

        if (IsSacrifice)
            return ExecuteSacrifice(screen);

        if (IsSkip)
            return ExecuteSkip(screen);

        return ExecuteSelectCard(screen);
    }

    private ExecuteResult ExecuteSelectCard(NCardRewardSelectionScreen screen)
    {
        var cardRow = CardRowField?.GetValue(screen) as Godot.Node;
        if (cardRow == null)
            return ExecuteResult.Retry(200);

        // Collect card holders sorted by X position for correct visual order.
        var holders = new List<Godot.Control>();
        foreach (Godot.Node child in cardRow.GetChildren())
        {
            if (child is not Godot.Control ctrl) continue;
            var prop = child.GetType().GetProperty(
                "CardModel", BindingFlags.Public | BindingFlags.Instance);
            if (prop?.GetValue(child) is MegaCrit.Sts2.Core.Models.CardModel)
                holders.Add(ctrl);
        }
        holders.Sort((a, b) => a.Position.X.CompareTo(b.Position.X));

        if (CardIndex < 0 || CardIndex >= holders.Count)
        {
            PlayerActionBuffer.LogMigrationWarning(
                $"[TakeCard] Index {CardIndex} out of range (count={holders.Count}) — retrying.");
            return ExecuteResult.Retry(200);
        }

        var holder = holders[CardIndex];
        holder.EmitSignal("Pressed", holder);
        PlayerActionBuffer.LogDispatcher($"[TakeCard] Selected card [{CardIndex}].");
        ReplayState.CardRewardSelectionScreen = null;
        ReplayDispatcher.DispatchNow();
        return ExecuteResult.Ok();
    }

    private ExecuteResult ExecuteSkip(NCardRewardSelectionScreen screen)
    {
        var extras = ExtraOptionsField?.GetValue(screen)
            as IReadOnlyList<CardRewardAlternative>;

        // Find the skip option (AfterSelected == DismissScreenAndKeepReward).
        CardRewardAlternative? skipAlt = null;
        if (extras != null)
        {
            foreach (var alt in extras)
            {
                if (alt.AfterSelected == MegaCrit.Sts2.Core.Entities.Rewards.PostAlternateCardRewardAction.DismissScreenAndKeepReward)
                {
                    skipAlt = alt;
                    break;
                }
            }
        }

        if (skipAlt != null)
        {
            TaskHelper.RunSafely(skipAlt.OnSelect());
            OnAlternateRewardSelectedMethod?.Invoke(screen, new object[] { skipAlt.AfterSelected });
        }
        else
        {
            // Fallback: dismiss with KeepReward directly.
            OnAlternateRewardSelectedMethod?.Invoke(screen, new object[]
            {
                MegaCrit.Sts2.Core.Entities.Rewards.PostAlternateCardRewardAction.DismissScreenAndKeepReward
            });
        }

        ReplayState.CardRewardSelectionScreen = null;
        ReplayDispatcher.DispatchNow();
        return ExecuteResult.Ok();
    }

    private ExecuteResult ExecuteSacrifice(NCardRewardSelectionScreen screen)
    {
        var extras = ExtraOptionsField?.GetValue(screen)
            as IReadOnlyList<CardRewardAlternative>;

        if (extras == null || extras.Count == 0)
        {
            PlayerActionBuffer.LogMigrationWarning(
                "[TakeCard] No extra options on selection screen — retrying.");
            return ExecuteResult.Retry(200);
        }

        CardRewardAlternative? sacrifice = null;
        foreach (var alt in extras)
        {
            if (alt.OptionId.Contains("sacrifice", System.StringComparison.OrdinalIgnoreCase)
                || alt.OptionId.Contains("pael", System.StringComparison.OrdinalIgnoreCase))
            {
                sacrifice = alt;
                break;
            }
        }
        sacrifice ??= extras[0];

        TaskHelper.RunSafely(sacrifice.OnSelect());
        OnAlternateRewardSelectedMethod?.Invoke(screen, new object[] { sacrifice.AfterSelected });

        ReplayState.CardRewardSelectionScreen = null;
        ReplayDispatcher.DispatchNow();
        return ExecuteResult.Ok();
    }

    public static TakeCardCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        string rest = raw.Substring(Prefix.Length).Trim();

        if (rest.Equals(SacrificeKeyword, System.StringComparison.OrdinalIgnoreCase))
            return TakeCardCommand.Sacrifice();

        if (rest.Equals(SkipKeyword, System.StringComparison.OrdinalIgnoreCase))
            return TakeCardCommand.Skip();

        if (int.TryParse(rest, out int index))
            return new TakeCardCommand(index);

        return null;
    }
}
