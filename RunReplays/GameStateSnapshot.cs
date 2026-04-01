using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace RunReplays;

public record CardInfo
{
    public string Id { get; init; } = "";
    public bool Upgraded { get; init; }
    public uint? CombatId { get; init; }
}

public record PowerInfo
{
    public string Id { get; init; } = "";
    public int Amount { get; init; }
}

public record EnemyInfo
{
    public string Name { get; init; } = "";
    public uint? CombatId { get; init; }
    public int CurrentHp { get; init; }
    public int MaxHp { get; init; }
    public IReadOnlyList<PowerInfo> Debuffs { get; init; } = new List<PowerInfo>();
    public string? Intent { get; init; }
}

public record GameStateSnapshot
{
    private static readonly PropertyInfo? RunStateProp =
        typeof(RunManager).GetProperty("State",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    public IReadOnlyList<string> AvailableCommands { get; init; }
    public string? RoomType { get; init; }
    public int Act { get; init; }
    public int Floor { get; init; }
    public int CurrentHp { get; init; }
    public int MaxHp { get; init; }
    public int Gold { get; init; }
    public IReadOnlyList<CardInfo> Deck { get; init; }
    public IReadOnlyList<CardInfo>? Hand { get; init; }
    public IReadOnlyList<CardInfo>? DrawPile { get; init; }
    public IReadOnlyList<CardInfo>? DiscardPile { get; init; }
    public IReadOnlyList<CardInfo>? ExhaustPile { get; init; }
    public IReadOnlyList<EnemyInfo>? Enemies { get; init; }
    public IReadOnlyList<string> Potions { get; init; }
    public IReadOnlyList<string> Relics { get; init; }

    public GameStateSnapshot(HashSet<Type> dispatchableTypes)
    {
        var state = RunStateProp?.GetValue(RunManager.Instance) as IRunState;
        var player = state?.Players.FirstOrDefault();
        var combat = player?.PlayerCombatState;

        CardInfo ToCardInfo(CardModel c) =>
            new CardInfo { Id = c.Id.ToString(), Upgraded = c.IsUpgraded };

        CardInfo ToCombatCardInfo(CardModel c)
        {
            uint? combatId = null;
            try
            {
                if (NetCombatCardDb.Instance.TryGetCardId(c, out uint id))
                    combatId = id;
            }
            catch { /* card may not be mutable */ }
            return new CardInfo { Id = c.Id.ToString(), Upgraded = c.IsUpgraded, CombatId = combatId };
        }

        AvailableCommands = dispatchableTypes.Select(t => t.Name).OrderBy(n => n).ToList();
        RoomType = state?.CurrentRoom?.RoomType.ToString();
        Act = state?.CurrentActIndex ?? 0;
        Floor = state?.ActFloor ?? 0;
        CurrentHp = player?.Creature?.CurrentHp ?? 0;
        MaxHp = player?.Creature?.MaxHp ?? 0;
        Gold = player?.Gold ?? 0;
        Deck = player?.Deck.Cards.Select(ToCardInfo).ToList() ?? new List<CardInfo>();
        Hand = combat?.Hand.Cards.Select(ToCombatCardInfo).ToList();
        DrawPile = combat?.DrawPile.Cards.Select(ToCombatCardInfo).ToList();
        DiscardPile = combat?.DiscardPile.Cards.Select(ToCombatCardInfo).ToList();
        ExhaustPile = combat?.ExhaustPile.Cards.Select(ToCombatCardInfo).ToList();

        if (CombatManager.Instance.IsInProgress)
        {
            var combatState = CombatManager.Instance.DebugOnlyGetState();
            Enemies = combatState?.Enemies
                .Where(e => e.IsAlive)
                .Select(e => new EnemyInfo
                {
                    Name = e.Name,
                    CombatId = e.CombatId,
                    CurrentHp = e.CurrentHp,
                    MaxHp = e.MaxHp,
                    Debuffs = e.Powers
                        .Select(p => new PowerInfo { Id = p.Id.ToString(), Amount = p.Amount })
                        .ToList(),
                    Intent = e.Monster?.NextMove?.Intents.FirstOrDefault()?.IntentType.ToString(),
                }).ToList();
        }

        Potions = player?.Potions.Select(p => p.Id.ToString()).ToList() ?? new List<string>();
        Relics = player?.Relics.Select(r => r.Id.ToString()).ToList() ?? new List<string>();
    }
}
