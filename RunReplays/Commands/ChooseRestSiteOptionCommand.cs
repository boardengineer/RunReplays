using MegaCrit.Sts2.Core.Entities.RestSite;
using RunReplays.Patch;

namespace RunReplays.Commands;

/// <summary>
///     Choose a rest site option (e.g. HEAL, SMITH).
///     Recorded as: "ChooseRestSiteOption {optionId}"
/// </summary>
public class ChooseRestSiteOptionCommand : ReplayCommand
{
    private const string Prefix = "ChooseRestSiteOption ";


    private ChooseRestSiteOptionCommand(string raw, string optionId) : base(raw)
    {
        OptionId = optionId;
    }

    public string OptionId { get; }

    public override string Describe()
    {
        return $"choose rest site option '{OptionId}'";
    }

    public override ExecuteResult Execute()
    {
        var sync = RestSiteReplayPatch._activeSynchronizer;
        if (sync == null)
            return ExecuteResult.Retry(300);

        var options = sync.GetLocalOptions();
        var index = -1;
        RestSiteOption? chosenOption = null;
        for (var i = 0; i < options.Count; i++)
            if (options[i].OptionId == OptionId)
            {
                index = i;
                chosenOption = options[i];
                break;
            }

        if (index == -1 || chosenOption == null)
            return ExecuteResult.Retry(300);

        PlayerActionBuffer.LogDispatcher("Dispatcher path notifying rest site");
        _ = RestSiteReplayPatch.SelectAndNotifyRoom(sync, index, chosenOption);
        return ExecuteResult.Ok();
    }

    public static ChooseRestSiteOptionCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        var optionId = raw.Substring(Prefix.Length);
        return new ChooseRestSiteOptionCommand(raw, optionId);
    }
}