namespace RunReplays.Commands;

/// <summary>
/// Parses raw command strings into <see cref="ReplayCommand"/> objects.
/// Returns null for commands that haven't been migrated yet — the dispatcher
/// falls back to the legacy string-based switch for those.
/// </summary>
public static class ReplayCommandParser
{
    /// <summary>
    /// Attempts to parse a raw command string into a typed command object.
    /// Returns null if the command type hasn't been migrated yet.
    /// </summary>
    public static ReplayCommand? TryParse(string raw)
    {
        return (ReplayCommand?)PlayCardCommand.TryParse(raw)
            ?? (ReplayCommand?)EndTurnCommand.TryParse(raw)
            ?? (ReplayCommand?)MapMoveCommand.TryParse(raw)
            ?? (ReplayCommand?)ChooseRestSiteOptionCommand.TryParse(raw)
            ?? (ReplayCommand?)ChooseEventOptionCommand.TryParse(raw)
            ?? (ReplayCommand?)ClaimRewardCommand.TryParse(raw)
            ?? (ReplayCommand?)TakeCardCommand.TryParse(raw)
            ?? (ReplayCommand?)SelectGridCardCommand.TryParse(raw)
            ?? (ReplayCommand?)SelectHandCardsCommand.TryParse(raw)
            ?? (ReplayCommand?)OpenShopCommand.TryParse(raw)
            ?? (ReplayCommand?)OpenFakeShopCommand.TryParse(raw)
            ?? (ReplayCommand?)BuyCardCommand.TryParse(raw)
            ?? (ReplayCommand?)BuyRelicCommand.TryParse(raw)
            ?? (ReplayCommand?)BuyCardRemovalCommand.TryParse(raw)
            ?? (ReplayCommand?)BuyPotionCommand.TryParse(raw)
            ?? (ReplayCommand?)UsePotionCommand.TryParse(raw)
            ?? (ReplayCommand?)DiscardPotionCommand.TryParse(raw)
            ?? (ReplayCommand?)ProceedToNextActCommand.TryParse(raw)
            ?? (ReplayCommand?)OpenChestCommand.TryParse(raw)
            ?? (ReplayCommand?)TakeChestRelicCommand.TryParse(raw)
            ?? (ReplayCommand?)CrystalSphereClickCommand.TryParse(raw)
            ?? (ReplayCommand?)SelectCardFromScreenCommand.TryParse(raw);
    }
}
