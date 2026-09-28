using MegaCrit.Sts2.Core.Random;

namespace RunReplays.Utils;

/// <summary>
/// Game v0.111 removed Rng.Seed / Rng.Counter (the state is xoshiro256** now, see Rng.ToSerializable). These keep the
/// diagnostic log lines compiling: Counter() = draws taken, Seed() = first state word (identifies the stream).
/// </summary>
internal static class RngCompat
{
    public static int Counter(this Rng rng) => rng.ToSerializable().counter;
    public static ulong Seed(this Rng rng) => rng.ToSerializable().state0;
}
