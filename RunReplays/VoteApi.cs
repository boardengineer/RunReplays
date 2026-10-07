using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Unlocks;
using RunReplays.Commands;

namespace RunReplays;

/// <summary>
/// For TwitchVoteController, whose chat votes drive a whole run through replay commands: the current decision
/// screen (the live bridge's detection), the commands on offer, running one, the open card screens, and starting a
/// run. Nothing here does anything unless that mod calls it.
/// </summary>
public static class VoteApi
{
    private static readonly PropertyInfo? RunStateProp =
        typeof(RunManager).GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    public static IRunState? RunState => RunManager.Instance == null ? null : RunStateProp?.GetValue(RunManager.Instance) as IRunState;
    public static Player? LocalPlayer => RunState?.Players.FirstOrDefault();

    /// <summary>combat, map, event, rest, shop, treasure, rewards, card_reward, grid_select, choose_card,
    /// hand_select, game_over, main_menu, loading, or the room's type name.</summary>
    public static string Screen()
    {
        var st = RunState;
        return LiveBridge.DetectScreen(st, st?.Players.FirstOrDefault(), RunManager.Instance?.IsInProgress == true);
    }

    public static List<ReplayCommand> Available() => ReplayDispatcher.GetAvailableCommands();

    /// <summary>Runs one command the way the vote controller always has: replay mode on, normal speed.</summary>
    public static ExecuteResult Execute(ReplayCommand command)
    {
        ReplayEngine._replayActive = true;
        ReplayDispatcher.GameSpeed = 1.0f;
        var r = command.Execute();
        ReplayDispatcher.ClearDispatchableCache();
        return r;
    }

    /// <summary>Replay mode for a run driven by votes (set before the run starts, as the controller did).</summary>
    public static void PrepareRun()
    {
        ReplayEngine._replayActive = true;
        ReplayDispatcher.GameSpeed = 1.0f;
    }

    public static NCardGridSelectionScreen? CardGrid
    {
        get
        {
            var s = CardGridScreenCapture.ActiveScreen;
            return s != null && GodotObject.IsInstanceValid(s) && s.IsInsideTree() ? s : null;
        }
    }

    public static IReadOnlyList<CardModel>? CardGridCards(NCardGridSelectionScreen screen)
        => CardGridScreenCapture.CardsField?.GetValue(screen) as IReadOnlyList<CardModel>;

    public static Node? ChooseACard
    {
        get
        {
            var s = ChooseACardScreenCapture.ActiveScreen;
            return s != null && GodotObject.IsInstanceValid(s) && s.IsInsideTree() ? s : null;
        }
    }

    /// <summary>A new standard run, with the same act roll as the live bridge's StartRun (and the game's lobby).</summary>
    public static void StartRun(CharacterModel character, string seed, int ascension)
    {
        if (NGame.Instance == null) return;
        var rng = new Rng(StringHelper.GetDeterministicHashCode(seed), "act_selection");
        var acts = ActModel.GetRandomList(rng, UnlockState.all, false).ToList();
        ReplayDispatcher.Clear();
        ReplayEngine.ActiveSeed = null;
        ReplayEngine.ActiveActs = null;
        PrepareRun();
        TaskHelper.RunSafely(NGame.Instance.StartNewSingleplayerRun(character, true, acts, [], seed, GameMode.Standard, ascension));
    }
}
