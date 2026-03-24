using System.Collections.Generic;

namespace RunReplays;

/// <summary>
/// we probably don't need this
/// </summary>
public static class ReplayRunner
{
    public static void Load(IReadOnlyList<string> commands)
    {
        ReplayEngine.Load(commands);
    }
}
