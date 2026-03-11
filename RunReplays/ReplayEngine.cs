using System;
using System.Collections.Generic;

namespace RunReplays;

/// <summary>
/// Raw command queue for an active replay.
/// Exposes typed Peek helpers for each command kind; consumption and logging
/// are handled by ReplayRunner, which calls these methods and proxies results.
/// </summary>
public static class ReplayEngine
{
    private static readonly Queue<string> _pending = new();

    public static bool IsActive => _pending.Count > 0;

    public static void Load(IReadOnlyList<string> commands)
    {
        _pending.Clear();
        foreach (string cmd in commands)
            if (!string.IsNullOrWhiteSpace(cmd))
                _pending.Enqueue(cmd);
    }

    public static void Clear() => _pending.Clear();

    /// <summary>Returns the next queued command without consuming it.</summary>
    public static bool PeekNext(out string? cmd) => _pending.TryPeek(out cmd);

    // ── Starting bonus ────────────────────────────────────────────────────────

    private const string StartingBonusPrefix = "ChooseStartingBonus ";

    public static bool PeekStartingBonus(out int choiceIndex)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(StartingBonusPrefix)
            && int.TryParse(cmd.AsSpan(StartingBonusPrefix.Length), out choiceIndex))
            return true;

        choiceIndex = -1;
        return false;
    }

    public static bool ConsumeStartingBonus(out int choiceIndex)
    {
        if (PeekStartingBonus(out choiceIndex))
        {
            _pending.Dequeue();
            return true;
        }
        return false;
    }

    // ── Map node choices ──────────────────────────────────────────────────────
    //
    // Recorded by PlayerActionBuffer via MoveToMapCoordAction.ToString():
    //   "MoveToMapCoordAction {playerId} MapCoord ({col}, {row})"

    private const string MapNodePrefix   = "MoveToMapCoordAction ";
    private const string MapCoordMarker  = "MapCoord (";

    public static bool PeekMapNode(out int col, out int row)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(MapNodePrefix))
        {
            int markerIdx = cmd.IndexOf(MapCoordMarker, StringComparison.Ordinal);
            if (markerIdx >= 0)
            {
                ReadOnlySpan<char> coords = cmd.AsSpan(markerIdx + MapCoordMarker.Length);
                // coords = "{col}, {row})"
                int comma = coords.IndexOf(',');
                int close = coords.IndexOf(')');
                if (comma > 0 && close > comma
                    && int.TryParse(coords[..comma].Trim(), out col)
                    && int.TryParse(coords[(comma + 1)..close].Trim(), out row))
                    return true;
            }
        }
        col = -1;
        row = -1;
        return false;
    }

    public static bool ConsumeMapNode(out int col, out int row)
    {
        if (PeekMapNode(out col, out row))
        {
            _pending.Dequeue();
            return true;
        }
        return false;
    }

    // ── End player turn ───────────────────────────────────────────────────────
    //
    // Recorded by PlayerActionBuffer via EndPlayerTurnAction.ToString():
    //   "EndPlayerTurnAction for player {playerId} round {combatRound}"

    private const string EndTurnPrefix = "EndPlayerTurnAction ";

    public static bool PeekEndTurn()
    {
        return _pending.TryPeek(out string? cmd) && cmd.StartsWith(EndTurnPrefix);
    }

    public static bool ConsumeEndTurn()
    {
        if (PeekEndTurn())
        {
            _pending.Dequeue();
            return true;
        }
        return false;
    }

    // ── Card plays ────────────────────────────────────────────────────────────
    //
    // Recorded by PlayerActionBuffer via PlayCardAction.ToString():
    //   "PlayCardAction card: {CardModel} index: {CombatCardIndex} targetid: {TargetId}"
    // TargetId prints as empty string when null.

    private const string CardPlayPrefix       = "PlayCardAction ";
    private const string CardPlayIndexMarker  = " index: ";
    private const string CardPlayTargetMarker = " targetid: ";

    public static bool PeekCardPlay(out uint combatCardIndex, out uint? targetId)
    {
        combatCardIndex = 0;
        targetId = null;

        if (!_pending.TryPeek(out string? cmd) || !cmd.StartsWith(CardPlayPrefix))
            return false;

        // Parse from the right so card display names containing spaces are safe.
        int targetIdx = cmd.LastIndexOf(CardPlayTargetMarker, StringComparison.Ordinal);
        int indexIdx  = cmd.LastIndexOf(CardPlayIndexMarker,  StringComparison.Ordinal);

        if (targetIdx < 0 || indexIdx < 0 || indexIdx >= targetIdx)
            return false;

        ReadOnlySpan<char> indexStr = cmd.AsSpan(
            indexIdx + CardPlayIndexMarker.Length,
            targetIdx - indexIdx - CardPlayIndexMarker.Length).Trim();

        if (!uint.TryParse(indexStr, out combatCardIndex))
            return false;

        ReadOnlySpan<char> targetStr = cmd.AsSpan(targetIdx + CardPlayTargetMarker.Length).Trim();
        if (targetStr.Length > 0 && uint.TryParse(targetStr, out uint tid))
            targetId = tid;

        return true;
    }

    public static bool ConsumeCardPlay(out uint combatCardIndex, out uint? targetId)
    {
        if (PeekCardPlay(out combatCardIndex, out targetId))
        {
            _pending.Dequeue();
            return true;
        }
        return false;
    }

    // ── Card rewards ──────────────────────────────────────────────────────────

    private const string CardRewardPrefix = "TakeCardReward: ";

    public static bool PeekCardReward(out string cardTitle)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(CardRewardPrefix))
        {
            cardTitle = cmd.Substring(CardRewardPrefix.Length);
            return true;
        }
        cardTitle = string.Empty;
        return false;
    }

    public static bool ConsumeCardReward(out string cardTitle)
    {
        if (PeekCardReward(out cardTitle))
        {
            _pending.Dequeue();
            return true;
        }
        return false;
    }

    // ── Potion rewards ────────────────────────────────────────────────────────

    private const string PotionRewardPrefix = "TakePotionReward: ";

    public static bool PeekPotionReward(out string potionTitle)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(PotionRewardPrefix))
        {
            potionTitle = cmd.Substring(PotionRewardPrefix.Length);
            return true;
        }
        potionTitle = string.Empty;
        return false;
    }

    public static bool ConsumePotionReward(out string potionTitle)
    {
        if (PeekPotionReward(out potionTitle))
        {
            _pending.Dequeue();
            return true;
        }
        return false;
    }

    // ── Event option choices ──────────────────────────────────────────────────
    //
    // Recorded by EventOptionChosenLogPatch via EventOption.Chosen():
    //   "ChooseEventOption {textKey}"

    private const string EventOptionPrefix = "ChooseEventOption ";

    public static bool PeekEventOption(out string textKey)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(EventOptionPrefix))
        {
            textKey = cmd.Substring(EventOptionPrefix.Length);
            return true;
        }
        textKey = string.Empty;
        return false;
    }

    public static bool ConsumeEventOption(out string textKey)
    {
        if (PeekEventOption(out textKey))
        {
            _pending.Dequeue();
            return true;
        }
        return false;
    }

    // ── Card upgrades ─────────────────────────────────────────────────────────
    //
    // Recorded by NDeckUpgradeSelectScreenLogPatch:
    //   "UpgradeCard {deckIndex}"

    private const string UpgradeCardPrefix = "UpgradeCard ";

    public static bool PeekUpgradeCard(out int deckIndex)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(UpgradeCardPrefix)
            && int.TryParse(cmd.AsSpan(UpgradeCardPrefix.Length), out deckIndex))
            return true;

        deckIndex = -1;
        return false;
    }

    public static bool ConsumeUpgradeCard(out int deckIndex)
    {
        if (PeekUpgradeCard(out deckIndex))
        {
            _pending.Dequeue();
            return true;
        }
        return false;
    }

    // ── Shop commands ─────────────────────────────────────────────────────────
    //
    // Recorded by ShopRecordPatch:
    //   "OpenShop"
    //   "BuyCard {title}"
    //   "BuyRelic {title}"
    //   "BuyPotion {title}"
    //   "BuyCardRemoval"

    private const string OpenShopCmd        = "OpenShop";
    private const string BuyCardPrefix      = "BuyCard ";
    private const string BuyRelicPrefix     = "BuyRelic ";
    private const string BuyPotionPrefix    = "BuyPotion ";
    private const string BuyCardRemovalCmd  = "BuyCardRemoval";

    public static bool PeekOpenShop()
        => _pending.TryPeek(out string? cmd) && cmd == OpenShopCmd;

    public static bool ConsumeOpenShop()
    {
        if (PeekOpenShop()) { _pending.Dequeue(); return true; }
        return false;
    }

    public static bool PeekBuyCard(out string cardTitle)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(BuyCardPrefix))
        {
            cardTitle = cmd.Substring(BuyCardPrefix.Length);
            return true;
        }
        cardTitle = string.Empty;
        return false;
    }

    public static bool ConsumeBuyCard(out string cardTitle)
    {
        if (PeekBuyCard(out cardTitle)) { _pending.Dequeue(); return true; }
        return false;
    }

    public static bool PeekBuyRelic(out string relicTitle)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(BuyRelicPrefix))
        {
            relicTitle = cmd.Substring(BuyRelicPrefix.Length);
            return true;
        }
        relicTitle = string.Empty;
        return false;
    }

    public static bool ConsumeBuyRelic(out string relicTitle)
    {
        if (PeekBuyRelic(out relicTitle)) { _pending.Dequeue(); return true; }
        return false;
    }

    public static bool PeekBuyPotion(out string potionTitle)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(BuyPotionPrefix))
        {
            potionTitle = cmd.Substring(BuyPotionPrefix.Length);
            return true;
        }
        potionTitle = string.Empty;
        return false;
    }

    public static bool ConsumeBuyPotion(out string potionTitle)
    {
        if (PeekBuyPotion(out potionTitle)) { _pending.Dequeue(); return true; }
        return false;
    }

    public static bool PeekBuyCardRemoval()
        => _pending.TryPeek(out string? cmd) && cmd == BuyCardRemovalCmd;

    public static bool ConsumeBuyCardRemoval()
    {
        if (PeekBuyCardRemoval()) { _pending.Dequeue(); return true; }
        return false;
    }

    // ── Rest site option choices ──────────────────────────────────────────────
    //
    // Recorded by RestSiteRecordPatch via RestSiteSynchronizer.ChooseLocalOption:
    //   "ChooseRestSiteOption {optionId}"  (e.g. "HEAL", "SMITH")

    private const string RestSiteOptionPrefix = "ChooseRestSiteOption ";

    public static bool PeekRestSiteOption(out string optionId)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(RestSiteOptionPrefix))
        {
            optionId = cmd.Substring(RestSiteOptionPrefix.Length);
            return true;
        }
        optionId = string.Empty;
        return false;
    }

    public static bool ConsumeRestSiteOption(out string optionId)
    {
        if (PeekRestSiteOption(out optionId))
        {
            _pending.Dequeue();
            return true;
        }
        return false;
    }

    // ── Gold rewards ──────────────────────────────────────────────────────────

    private const string GoldRewardPrefix = "TakeGoldReward: ";

    public static bool PeekGoldReward(out int goldAmount)
    {
        if (_pending.TryPeek(out string? cmd) && cmd.StartsWith(GoldRewardPrefix)
            && int.TryParse(cmd.AsSpan(GoldRewardPrefix.Length), out goldAmount))
            return true;

        goldAmount = 0;
        return false;
    }

    public static bool ConsumeGoldReward(out int goldAmount)
    {
        if (PeekGoldReward(out goldAmount))
        {
            _pending.Dequeue();
            return true;
        }
        return false;
    }
}
