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

    private static readonly FieldInfo? ExtraOptionsField =
        typeof(NCardRewardSelectionScreen).GetField(
            "_extraOptions", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly MethodInfo? OnAlternateRewardSelectedMethod =
        typeof(NCardRewardSelectionScreen).GetMethod(
            "OnAlternateRewardSelected",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

    public int CardIndex { get; }
    public bool IsSacrifice { get; }

    public TakeCardCommand(int cardIndex) : base("")
    {
        CardIndex = cardIndex;
        IsSacrifice = false;
    }

    private TakeCardCommand(bool sacrifice) : base("")
    {
        CardIndex = -1;
        IsSacrifice = true;
    }

    public static TakeCardCommand Sacrifice() => new(sacrifice: true);

    public override string ToString()
        => IsSacrifice ? $"{Prefix}{SacrificeKeyword}" : $"{Prefix}{CardIndex}";

    public override string Describe()
        => IsSacrifice
            ? "sacrifice card reward"
            : $"take card [{CardIndex}]" + (Comment != null ? $" ({Comment})" : "");

    public override ExecuteResult Execute()
    {
        var screen = ReplayState.CardRewardSelectionScreen;
        if (screen == null)
            return ExecuteResult.Retry(200);

        if (IsSacrifice)
            return ExecuteSacrifice(screen);

        return ExecuteSelectCard(screen);
    }

    private ExecuteResult ExecuteSelectCard(NCardRewardSelectionScreen screen)
    {
        var holder = CardGridScreenCapture.FindCardHolderByIndex(screen, CardIndex);
        if (holder == null)
        {
            PlayerActionBuffer.LogMigrationWarning(
                $"[TakeCard] Card holder at index {CardIndex} not found — retrying.");
            return ExecuteResult.Retry(200);
        }

        holder.EmitSignal("Pressed", holder);
        PlayerActionBuffer.LogDispatcher($"[TakeCard] Selected card [{CardIndex}].");
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

        if (int.TryParse(rest, out int index))
            return new TakeCardCommand(index);

        return null;
    }
}
