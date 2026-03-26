using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;

namespace RunReplays.Patches.Record;
using RunReplays;

/// <summary>
/// State for correlating FromDeckForRemoval with the CardsSelected recording
/// in DeckCardSelectRecordPatch.  Recording is handled entirely through the
/// CardsSelected path — the RemoveFromDeck patches have been removed.
/// </summary>
internal static class DeckRemovalState
{
    internal static bool PendingRemoval;
}

/// <summary>
/// Sets PendingRemoval when CardSelectCmd.FromDeckForRemoval is entered so that
/// DeckCardSelectRecordPatch records a RemoveCardFromDeck command instead of
/// SelectDeckCard.
/// </summary>
[HarmonyPatch(typeof(CardSelectCmd), nameof(CardSelectCmd.FromDeckForRemoval))]
public static class FromDeckForRemovalPatch
{
    [HarmonyPrefix]
    public static void Prefix()
    {
        DeckRemovalState.PendingRemoval = true;

        PlayerActionBuffer.LogToDevConsole("[DeckRemovalRecordPatch] FromDeckForRemoval entered — awaiting CardsSelected.");
    }
}
