using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Rooms;

using RunReplays.Patches;
namespace RunReplays.Commands;

/// <summary>
/// Choose a rest site option (e.g. HEAL, SMITH).
/// Recorded as: "ChooseRestSiteOption {optionId}"
/// </summary>
public class ChooseRestSiteOptionCommand : ReplayCommand
{
    private const string Prefix = "ChooseRestSiteOption ";

    public string OptionId { get; }


    public ChooseRestSiteOptionCommand(string optionId) : base("")
    {
        OptionId = optionId;
    }

    public override string ToString() => $"ChooseRestSiteOption {OptionId}";

    public override string Describe() => $"choose rest site option '{OptionId}'";

    public override ExecuteResult Execute()
    {
        var sync = ReplayState.ActiveRestSiteSynchronizer;
        if (sync == null)
            return ExecuteResult.Retry(300);

        var options = sync.GetLocalOptions();
        int index = -1;
        RestSiteOption? chosenOption = null;
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].OptionId == OptionId)
            {
                index = i;
                chosenOption = options[i];
                break;
            }
        }

        if (index == -1 || chosenOption == null)
            return ExecuteResult.Retry(300);

        PlayerActionBuffer.LogDispatcher($"Dispatcher path notifying rest site");
        _ = SelectAndNotifyRoom(sync, index, chosenOption);
        return ExecuteResult.Ok();
    }

    public static ChooseRestSiteOptionCommand? TryParse(string raw)
    {
        if (!raw.StartsWith(Prefix))
            return null;

        string optionId = raw.Substring(Prefix.Length);
        return new ChooseRestSiteOptionCommand(optionId);
    }

    private static async Task SelectAndNotifyRoom(
        RestSiteSynchronizer sync, int index, RestSiteOption option)
    {
        bool success = await sync.ChooseLocalOption(index);
        PlayerActionBuffer.LogToDevConsole(
            $"[RestSiteReplayPatch] ChooseLocalOption returned {success} — notifying room.");
        if (!success)
            return;

        Callable.From(() => NRestSiteRoom.Instance?.AfterSelectingOption(option)).CallDeferred();
    }
}
