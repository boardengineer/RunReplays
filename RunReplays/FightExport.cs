// Fight snapshot exporter: serializes the live STS2 (v0.111) combat state to a single-line JSON "fight snapshot" that
// the C++ simulator (C:\sts-sim, src/fight_import.cpp) can load to continue the fight identically.
//
// Shared verbatim between the headless oracle (C:\sts-sim\oracle) and the RunReplays mod (live game). Only depends on
// sts2.dll and the BCL. Format: see C:\sts-sim\README.md ("Mid-fight snapshots").
//
// Everything is read from the game's objects; content-specific state is exported generically:
//  * "f": every instance field declared by the concrete model class (CardModel/PowerModel/RelicModel/MonsterModel
//    subclass, up to but excluding the framework base class), found by reflection. Auto-property backing fields are
//    named after the property ("<TurnsSeen>k__BackingField" -> "TurnsSeen").
//  * "d": a power's InternalData object (its fields), "v": DynamicVars (name -> BaseValue).
// References are written as {"$card":{..}}, {"$cid":N}, {"$power":"ID"}, {"$state":"ID"}; Rng as
// {"$rng":["s0","s1","s2","s3",counter]}. 64-bit values that may exceed 2^53 are written as strings.
#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Runs;

namespace StsSim
{
    public static class FightExport
    {
        public const int FormatVersion = 1;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        // ------------------------------------------------------------------ public API

        /// <summary>Single-line JSON snapshot of the player's current combat (null if the player is not in combat).</summary>
        public static string Export(Player player)
        {
            var cs = player?.Creature?.CombatState;
            if (cs == null || player.PlayerCombatState == null) return null;
            var w = new W();
            // card identities: position in draw, hand, discard, exhaust, play (1-based) = CardInst::uid in the sim
            _refs = new Dictionary<CardModel, int>(ReferenceEqualityComparer.Instance);
            var pcs0 = player.PlayerCombatState;
            foreach (var pile in new[] { pcs0.DrawPile, pcs0.Hand, pcs0.DiscardPile, pcs0.ExhaustPile, pcs0.PlayPile })
                foreach (var c in pile.Cards) if (!_refs.ContainsKey(c)) _refs[c] = _refs.Count + 1;
            string json;
            try
            {
                WriteSnapshot(w, cs, player);
                json = w.ToString();
                if (IsPlayerInputPoint(player))   // the base of a later pending-choice export (ExportPending)
                {
                    _lastInputJson = json;
                    _lastInputRefs = new Dictionary<CardModel, int>(_refs, ReferenceEqualityComparer.Instance);
                }
            }
            finally { _refs = null; }
            return json;
        }

        // ------------------------------------------------------------------ pending card-selection prompt
        // A card-selection prompt is open inside an action (the game waits on the player's choice; not an input point).
        // The export is the snapshot of the last input point (the state the action started from) plus
        //   "pending": {"action": {"token": "p3.1" | "u0" | "e" (optional, sim cmp syntax relative to that state),
        //                          "kind": "card" | "potion" | "endTurn" | "other", "ref"/"id"/"up" (card), "slot" (potion),
        //                          "target": cid | null},
        //               "answered": [[picks..], ..]  (the prompts of this action answered so far, option indexes; [-1] = none),
        //               "prompt": {"min", "max", "options": [{"id","up","ref"}]}}   (the open prompt)
        // The simulator replays the action from the base state with the answered prompts to reach the same prompt
        // (src/fight_import.cpp pendingPrefix; `mcts plan` answers it). answered == null: unknown (assumed none).
        static string _lastInputJson;
        static Dictionary<CardModel, int> _lastInputRefs;

        public static string ExportPending(Player player, IEnumerable<CardModel> options, int minSelect, int maxSelect,
                                           IEnumerable<int[]> answered, string actionToken = null)
        {
            if (_lastInputJson == null || player?.Creature?.CombatState == null) return null;
            var w = new W();
            w.Obj();
            w.Key("action").Obj();
            if (actionToken != null) w.Key("token").Str(actionToken);
            var act = SafeGet(() => RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction);
            if (act is MegaCrit.Sts2.Core.GameActions.PlayCardAction pa)
            {
                var card = GetField(pa, "_card") as CardModel;
                w.Key("kind").Str("card");
                w.Key("id").Str(pa.CardModelId.Entry);
                w.Key("up").Num(card?.CurrentUpgradeLevel ?? 0);
                w.Key("ref").Num(card != null && _lastInputRefs.TryGetValue(card, out var rf) ? rf : 0);
                w.Key("target"); if (pa.TargetId.HasValue) w.Num(pa.TargetId.Value); else w.Null();
            }
            else if (act is MegaCrit.Sts2.Core.GameActions.UsePotionAction ua)
            {
                w.Key("kind").Str("potion");
                w.Key("slot").Num(ua.PotionIndex);
                w.Key("target"); if (ua.TargetId.HasValue) w.Num(ua.TargetId.Value); else w.Null();
            }
            else w.Key("kind").Str(act == null ? "none" : act.GetType().Name);
            w.End();
            w.Key("answered");
            if (answered == null) w.Null();
            else
            {
                w.Arr();
                foreach (var a in answered) { w.Arr(); foreach (var i in a) w.Num(i); w.End(); }
                w.End();
            }
            w.Key("prompt").Obj();
            w.Key("min").Num(minSelect);
            w.Key("max").Num(maxSelect);
            w.Key("options").Arr();
            foreach (var c in options)
            {
                w.Obj(); w.Key("id").Str(c.Id.Entry); w.Key("up").Num(c.CurrentUpgradeLevel);
                w.Key("ref").Num(_lastInputRefs.TryGetValue(c, out var r) ? r : 0); w.End();
            }
            w.End();
            w.End();
            w.End();
            return _lastInputJson.Substring(0, _lastInputJson.Length - 1) + ",\"pending\":" + w + "}";
        }

        /// <summary>True while the player can act (combat in progress, player's side, Play phase, no action running).</summary>
        public static bool IsPlayerInputPoint(Player player)
        {
            try
            {
                var cs = player?.Creature?.CombatState;
                var pcs = player?.PlayerCombatState;
                if (cs == null || pcs == null) return false;
                var cm = CombatManager.Instance;
                if (cm == null || !cm.IsInProgress) return false;
                if (cs.CurrentSide != CombatSide.Player) return false;
                if (pcs.Phase != PlayerTurnPhase.Play) return false;
                if (cm.PlayerActionsDisabled || cm.EndingPlayerTurnPhaseOne || cm.EndingPlayerTurnPhaseTwo) return false;
                if (RunManager.Instance?.ActionExecutor?.IsRunning == true) return false;
                if (player.Creature.IsDead) return false;
                // the fight goes on while no primary enemy is alive if something stops it from ending (Test Subject
                // between phases: Hook.ShouldStopCombatFromEnding); CombatManager.IsEnding covers both cases
                return !cm.IsOverOrEnding;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ snapshot

        static void WriteSnapshot(W w, ICombatState cs, Player player)
        {
            var run = cs.RunState ?? player.RunState;
            var pcs = player.PlayerCombatState;
            w.Obj();
            w.Key("format").Str("sts2-fight-snapshot");
            w.Key("version").Num(FormatVersion);
            w.Key("seed").Str(run?.Rng?.StringSeed);
            w.Key("seedU64").U64(run?.Rng?.Seed ?? 0);
            w.Key("asc").Num(run?.AscensionLevel ?? 0);
            w.Key("act").Num(run?.CurrentActIndex ?? 0);
            w.Key("actId").Str(SafeGet(() => run?.Act?.Id.Entry));
            w.Key("floor").Num(SafeGet(() => run?.TotalFloor ?? 0));
            var coord = SafeGet(() => run?.CurrentMapCoord);
            w.Key("coord");
            if (coord.HasValue) { w.Arr(); w.Num(coord.Value.col); w.Num(coord.Value.row); w.End(); } else w.Null();
            w.Key("encounter").Str(cs.Encounter?.Id.Entry);
            w.Key("roomType").Str(SafeGet(() => cs.Encounter?.RoomType.ToString()));
            w.Key("round").Num(cs.RoundNumber);
            w.Key("side").Str(cs.CurrentSide.ToString());
            w.Key("phase").Str(pcs.Phase.ToString());
            w.Key("inputPoint").Bool(IsPlayerInputPoint(player));
            w.Key("nextCid").Num(Convert.ToInt64(GetField(cs, "_nextCreatureId") ?? 0u));
            // MonsterModel.Rng seed base (CombatState.CreateCreature): Seed + col + row + CurrentActIndex (+ CombatId)
            ulong mbase = unchecked((ulong)((long)(run?.Rng?.Seed ?? 0) + (long)(coord?.col ?? 0) + (long)(coord?.row ?? 0) + (run?.CurrentActIndex ?? 0)));
            w.Key("monsterRngBase").U64(mbase);
            w.Key("rng"); WriteRngSet(w, run?.Rng);
            w.Key("playerRng"); WritePlayerRng(w, player);
            w.Key("history"); WriteHistory(w, cs, player);
            w.Key("player"); WritePlayer(w, cs, player);
            w.Key("allies").Arr();
            foreach (var a in cs.Allies) if (a != player.Creature) WriteCreatureBasic(w, a, true);
            w.End();
            w.Key("enemies").Arr();
            foreach (var e in cs.Enemies) WriteEnemy(w, e);
            w.End();
            w.Key("escaped").Arr();
            foreach (var e in SafeGet(() => (cs as CombatState)?.EscapedCreatures) ?? (IReadOnlyList<Creature>)Array.Empty<Creature>()) w.Str(e.ModelId.Entry);
            w.End();
            w.End();
        }

        static void WriteRngSet(W w, RunRngSet set)
        {
            w.Obj();
            if (set != null && GetField(set, "_rngs") is IDictionary dict)
                foreach (var key in dict.Keys.Cast<object>().OrderBy(k => Convert.ToInt32(k)).ToList())
                {
                    w.Key(key.ToString());
                    WriteRng(w, dict[key] as MegaCrit.Sts2.Core.Random.Rng);
                }
            w.End();
        }

        static void WritePlayerRng(W w, Player player)
        {
            w.Obj();
            try
            {
                var set = player.GetType().GetProperty("PlayerRng", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(player);
                if (set != null && GetFieldDeep(set, "_rngs") is IDictionary dict)
                    foreach (var key in dict.Keys.Cast<object>().ToList())
                    {
                        w.Key(key.ToString());
                        WriteRng(w, dict[key] as MegaCrit.Sts2.Core.Random.Rng);
                    }
            }
            catch { }
            w.End();
        }

        static void WriteRng(W w, MegaCrit.Sts2.Core.Random.Rng r)
        {
            if (r == null) { w.Null(); return; }
            var s = r.ToSerializable();
            w.Arr(); w.U64(s.state0); w.U64(s.state1); w.U64(s.state2); w.U64(s.state3); w.Num(s.counter); w.End();
        }

        // CombatHistory-derived counters (HappenedThisTurn = this round and side).
        static void WriteHistory(W w, ICombatState cs, Player player)
        {
            var h = CombatManager.Instance?.History;
            var entries = h?.Entries?.ToList() ?? new List<MegaCrit.Sts2.Core.Combat.History.CombatHistoryEntry>();
            bool mine(Player p) => p == player;
            var started = entries.OfType<CardPlayStartedEntry>().Where(e => mine(e.CardPlay.Player)).ToList();
            var finished = entries.OfType<CardPlayFinishedEntry>().Where(e => mine(e.CardPlay.Player)).ToList();
            var startedT = started.Where(e => e.HappenedThisTurn(cs)).ToList();
            var finishedT = finished.Where(e => e.HappenedThisTurn(cs)).ToList();
            var dmg = entries.OfType<DamageReceivedEntry>().Where(e => e.Receiver == player.Creature && e.Result.UnblockedDamage > 0).ToList();
            w.Obj();
            w.Key("playsStartedThisTurn").Num(startedT.Count);
            w.Key("manualPlaysStartedThisTurn").Num(startedT.Count(e => !e.CardPlay.IsAutoPlay && e.CardPlay.PlayIndex == 0));
            w.Key("manualAttackPlaysStartedThisTurn").Num(startedT.Count(e => !e.CardPlay.IsAutoPlay && e.CardPlay.PlayIndex == 0 && e.CardPlay.Card.Type == CardType.Attack));
            w.Key("attackPlaysStartedThisTurn").Num(startedT.Count(e => e.CardPlay.Card.Type == CardType.Attack));
            w.Key("skillPlaysStartedThisTurn").Num(startedT.Count(e => e.CardPlay.Card.Type == CardType.Skill));
            w.Key("powerPlaysStartedThisTurn").Num(startedT.Count(e => e.CardPlay.Card.Type == CardType.Power));
            w.Key("playsFinishedThisTurn").Num(finishedT.Count);
            w.Key("attackPlaysFinishedThisTurn").Num(finishedT.Count(e => e.CardPlay.Card.Type == CardType.Attack));
            w.Key("skillPlaysFinishedThisTurn").Num(finishedT.Count(e => e.CardPlay.Card.Type == CardType.Skill));
            w.Key("powerPlaysFinishedThisTurn").Num(finishedT.Count(e => e.CardPlay.Card.Type == CardType.Power));
            w.Key("shivPlaysFinishedThisTurn").Num(finishedT.Count(e => e.CardPlay.Card.Tags.Contains(CardTag.Shiv)));
            w.Key("playsStartedCombat").Num(started.Count);
            w.Key("playsFinishedCombat").Num(entries.OfType<CardPlayFinishedEntry>().Count());   // all players (Gold Axe)
            w.Key("attackPlaysStartedCombat").Num(started.Count(e => e.CardPlay.Card.Type == CardType.Attack));
            w.Key("skillPlaysStartedCombat").Num(started.Count(e => e.CardPlay.Card.Type == CardType.Skill));
            w.Key("exhaustedThisTurn").Num(entries.OfType<CardExhaustedEntry>().Count(e => e.HappenedThisTurn(cs) && e.Actor == player.Creature));
            w.Key("exhaustedCombat").Num(entries.OfType<CardExhaustedEntry>().Count(e => e.Actor == player.Creature));
            w.Key("discardsThisTurn").Num(entries.OfType<CardDiscardedEntry>().Count(e => e.HappenedThisTurn(cs) && e.Actor == player.Creature));
            w.Key("cardsDrawnThisTurn").Num(entries.OfType<CardDrawnEntry>().Count(e => e.HappenedThisTurn(cs) && e.Actor == player.Creature));
            w.Key("cardsDrawnCombat").Num(entries.OfType<CardDrawnEntry>().Count(e => e.Actor == player.Creature));
            w.Key("hpLostThisTurn").Bool(dmg.Any(e => e.HappenedThisTurn(cs)));
            w.Key("hpLossEventsCombat").Num(dmg.Count);
            w.Key("cardBlockGainsThisTurn").Num(entries.OfType<BlockGainedEntry>().Count(e => e.HappenedThisTurn(cs) && e.Receiver == player.Creature
                && e.CardPlay != null && (e.Props & MegaCrit.Sts2.Core.ValueProps.ValueProp.Move) != 0));
            w.Key("potionsUsedThisTurn").Num(entries.OfType<PotionUsedEntry>().Count(e => e.HappenedThisTurn(cs)));
            w.Key("potionsUsedCombat").Num(entries.OfType<PotionUsedEntry>().Count());
            // HistoryCourse: the last non-dupe Attack whose play finished this turn
            w.Key("lastAttackFinishedThisTurn");
            WriteValue(w, finishedT.LastOrDefault(e => e.CardPlay.Card.Type == CardType.Attack && !e.CardPlay.Card.IsDupe)?.CardPlay.Card, 1);
            // Defect (sim: cards_defect.cpp / orbs.cpp)
            w.Key("lightningChanneledCombat").Num(entries.OfType<OrbChanneledEntry>().Count(e => e.Actor?.Player == player && e.Orb?.Id.Entry == "LIGHTNING_ORB"));
            w.Key("energySpentThisTurn").Num(entries.OfType<EnergySpentEntry>().Where(e => e.HappenedThisTurn(cs) && e.Actor?.Player == player).Sum(e => e.Amount));
            w.Key("firstPlaysStartedThisTurn").Num(startedT.Count(e => e.CardPlay.PlayIndex == 0));
            w.Key("zeroEnergyAttackPlaysStartedThisTurn").Num(startedT.Count(e => e.CardPlay.Card.Type == CardType.Attack && e.CardPlay.Resources.EnergyValue == 0));
            w.Key("statusDrawnThisTurn").Num(entries.OfType<CardDrawnEntry>().Count(e => e.HappenedThisTurn(cs) && e.Actor == player.Creature && e.Card?.Type == CardType.Status));
            {
                var ptn = typeof(MegaCrit.Sts2.Core.Combat.History.CombatHistoryEntry).GetField("_playerTurnNumbers", BindingFlags.Instance | BindingFlags.NonPublic);
                int turn = player.PlayerCombatState?.TurnNumber ?? 0;
                var nfb = entries.OfType<DamageReceivedEntry>().Where(e => e.Receiver == player.Creature && !e.Result.WasFullyBlocked)
                    .Select(e => ptn?.GetValue(e) is IDictionary d && d.Contains(player.NetId) ? (int)d[player.NetId] : -1).ToList();
                w.Key("notFullyBlockedLastPlayerTurn").Bool(nfb.Contains(turn - 1));
                w.Key("notFullyBlockedThisPlayerTurn").Bool(nfb.Contains(turn));
            }
            // Regent (sim: cards_regent.cpp)
            w.Key("starsGainedThisTurn").Num(entries.OfType<StarsModifiedEntry>().Where(e => e.HappenedThisTurn(cs) && e.Amount > 0 && e.Actor == player.Creature).Sum(e => e.Amount));
            w.Key("cardsGeneratedByPlayerCombat").Num(entries.OfType<CardGeneratedEntry>().Count(e => e.Creator == player));
            w.Key("playerAttackHitsThisTurn").Arr();   // [cid, hits]: DamageReceivedEntry from the player's powered attacks (Beat Into Shape)
            foreach (var g in entries.OfType<DamageReceivedEntry>()
                         .Where(e => e.HappenedThisTurn(cs) && e.Dealer == player.Creature && e.Receiver?.CombatId != null
                                     && MegaCrit.Sts2.Core.ValueProps.ValuePropExtensions.IsPoweredAttack(e.Result.Props))
                         .GroupBy(e => e.Receiver.CombatId.Value))
            {
                w.Arr(); w.Num(g.Key); w.Num(g.Count()); w.End();
            }
            w.End();
            // Necrobinder (sim: cards_necrobinder.cpp / osty.cpp)
            {
                var osty = player.Osty;
                w.Key("ostyAttacksThisTurn").Num(osty == null ? 0 : entries.OfType<CreatureAttackedEntry>().Count(e => e.HappenedThisTurn(cs) && e.Actor == osty));
                w.Key("etherealPlaysFinishedCombat").Num(finished.Count(e => e.WasEthereal));
                w.Key("nonHandDrawsThisTurn").Num(entries.OfType<CardDrawnEntry>().Count(e => e.HappenedThisTurn(cs) && e.Actor == player.Creature && !e.FromHandDraw));
                w.Key("doomAppliedThisTurn").Bool(entries.OfType<PowerReceivedEntry>().Any(e => e.HappenedThisTurn(cs) && e.Power is MegaCrit.Sts2.Core.Models.Powers.DoomPower && e.Applier == player.Creature));
                w.Key("finishedThisTurnRefs").Arr();   // refs of the cards whose play finished this turn (Fetch)
                foreach (var e in finishedT) w.Num(_refs != null && _refs.TryGetValue(e.CardPlay.Card, out var rf) ? rf : 0);
                w.End();
            }
            // Act 3 (sim: monsters_act3.cpp): Bound afflictions this turn (ChainsOfBindingPower's CardAfflictedEntry count)
            w.Key("boundAfflictionsThisTurn").Num(entries.OfType<CardAfflictedEntry>().Count(e => e.HappenedThisTurn(cs) && e.Actor == player.Creature
                && e.Affliction?.Id.Entry == "BOUND"));
            w.Key("entries").Num(entries.Count);
            w.End();
        }

        // ------------------------------------------------------------------ player

        static void WritePlayer(W w, ICombatState cs, Player player)
        {
            var pcs = player.PlayerCombatState;
            var c = player.Creature;
            w.Obj();
            w.Key("character").Str(player.Character.Id.Entry);
            w.Key("cid").Num(c.CombatId.HasValue ? (long)c.CombatId.Value : -1);
            w.Key("hp").Num(c.CurrentHp);
            w.Key("maxHp").Num(c.MaxHp);
            w.Key("block").Num(c.Block);
            w.Key("energy").Num(pcs.Energy);
            w.Key("maxEnergyBase").Num(player.MaxEnergy);
            w.Key("maxEnergy").Num(SafeGet(() => pcs.MaxEnergy));
            w.Key("stars").Num(pcs.Stars);
            w.Key("turn").Num(pcs.TurnNumber);
            w.Key("gold").Num(player.Gold);
            w.Key("potionSlots").Num(player.MaxPotionCount);
            w.Key("potions").Arr();
            foreach (var p in player.PotionSlots)
            {
                if (p == null) { w.Null(); continue; }
                w.Obj(); w.Key("id").Str(p.Id.Entry); WriteModelFields(w, p, typeof(PotionModel)); w.End();
            }
            w.End();
            w.Key("relics").Arr();
            foreach (var r in player.Relics)
            {
                w.Obj();
                w.Key("id").Str(r.Id.Entry);
                w.Key("melted").Bool(r.IsMelted);
                w.Key("wax").Bool(r.IsWax);
                w.Key("status").Str(SafeGet(() => r.Status.ToString()));
                w.Key("display").Num(SafeGet(() => r.DisplayAmount));
                WriteModelFields(w, r, typeof(RelicModel));
                WriteVars(w, r);
                w.End();
            }
            w.End();
            w.Key("powers"); WritePowers(w, c);
            w.Key("draw"); WritePile(w, pcs.DrawPile.Cards);
            w.Key("hand"); WritePile(w, pcs.Hand.Cards);
            w.Key("discard"); WritePile(w, pcs.DiscardPile.Cards);
            w.Key("exhaust"); WritePile(w, pcs.ExhaustPile.Cards);
            w.Key("play"); WritePile(w, pcs.PlayPile.Cards);
            w.Key("orbs");
            var oq = pcs.OrbQueue;
            if (oq != null && oq.Capacity > 0)
            {
                w.Obj(); w.Key("capacity").Num(oq.Capacity); w.Key("orbs").Arr();
                foreach (var o in oq.Orbs) w.Str(o.Id.Entry);
                w.End();
                w.Key("orbData").Arr();   // per orb: its fields (DarkOrb._evokeVal, GlassOrb._passiveVal)
                foreach (var o in oq.Orbs) { w.Obj(); w.Key("id").Str(o.Id.Entry); WriteModelFields(w, o, typeof(OrbModel)); w.End(); }
                w.End();
                w.End();
            }
            else w.Null();
            w.End();
        }

        static void WritePile(W w, IEnumerable<CardModel> cards)
        {
            w.Arr();
            foreach (var c in cards) WriteCard(w, c, 0);
            w.End();
        }

        [ThreadStatic] static Dictionary<CardModel, int> _refs;

        // depth 0: full card; depth 1 (a reference from some model's field): {"id","up","ref"} if the card is in a pile,
        // else the full card (e.g. a power's stored card) with ref 0; deeper: id/up only.
        public static void WriteCard(W w, CardModel c, int depth)
        {
            int rf = _refs != null && _refs.TryGetValue(c, out var r) ? r : 0;
            w.Obj();
            w.Key("id").Str(c.Id.Entry);
            w.Key("up").Num(c.CurrentUpgradeLevel);
            w.Key("ref").Num(rf);
            if (depth > 1 || (depth == 1 && rf > 0)) { w.End(); return; }
            w.Key("type").Str(c.Type.ToString());
            var ec = c.EnergyCost;
            w.Key("cost").Obj();
            w.Key("x").Bool(ec.CostsX);
            w.Key("canonical").Num(ec.Canonical);
            w.Key("base").Num(Convert.ToInt64(GetField(ec, "_base") ?? 0));
            w.Key("local").Num(SafeGet(() => ec.GetWithModifiers(CostModifiers.Local)));
            w.Key("all").Num(SafeGet(() => ec.GetWithModifiers(CostModifiers.All)));
            w.Key("mods").Arr();
            if (GetField(ec, "_localModifiers") is IEnumerable mods)
                foreach (var m in mods.Cast<LocalCostModifier>())
                {
                    w.Obj();
                    w.Key("amt").Num(m.Amount);
                    w.Key("type").Str(m.Type.ToString());
                    w.Key("exp").Num((long)Convert.ToInt32(m.Expiration));
                    w.Key("expName").Str(m.Expiration.ToString());
                    w.Key("reduceOnly").Bool(m.IsReduceOnly);
                    w.End();
                }
            w.End();
            w.End();
            w.Key("kw").Arr();
            foreach (var k in SafeGet(() => c.GetKeywordsWithSources(KeywordSources.Local)) ?? (IReadOnlySet<CardKeyword>)new HashSet<CardKeyword>()) w.Str(k.ToString());
            w.End();
            w.Key("tags").Arr();
            foreach (var t in SafeGet(() => c.Tags) ?? Array.Empty<CardTag>()) w.Str(t.ToString());
            w.End();
            w.Key("turnRetain").Bool(GetField(c, "_hasSingleTurnRetain") is true);
            w.Key("turnSly").Bool(GetField(c, "_hasSingleTurnSly") is true);
            w.Key("exhaustNext").Bool(c.ExhaustOnNextPlay);
            w.Key("clone").Bool(c.IsClone);
            w.Key("dupe").Bool(c.IsDupe);
            w.Key("replays").Num(c.BaseReplayCount);
            // star cost (Regent): CanonicalStarCost / HasStarCostX / BaseStarCost, LastStarsSpent, _temporaryStarCosts
            w.Key("stars").Obj();
            w.Key("canonical").Num(c.CanonicalStarCost);
            w.Key("x").Bool(c.HasStarCostX);
            w.Key("base").Num(SafeGet(() => c.BaseStarCost));
            w.Key("last").Num(c.LastStarsSpent);
            w.Key("temps").Arr();
            if (GetField(c, "_temporaryStarCosts") is IEnumerable tsc)
                foreach (var t in tsc.Cast<TemporaryCardCost>())
                {
                    w.Obj(); w.Key("cost").Num(t.Cost); w.Key("turn").Bool(t.ClearsWhenTurnEnds); w.Key("played").Bool(t.ClearsWhenCardIsPlayed); w.End();
                }
            w.End();
            w.End();
            w.Key("deck").Bool(c.DeckVersion != null);
            w.Key("ench");
            if (c.Enchantment != null)
            {
                w.Obj(); w.Key("id").Str(c.Enchantment.Id.Entry); w.Key("amt").Num(c.Enchantment.Amount);
                w.Key("status").Str(c.Enchantment.Status.ToString());   // EnchantmentStatus (Sown / Swift / Vigorous / Glam used)
                WriteModelFields(w, c.Enchantment, typeof(EnchantmentModel)); w.End();
            }
            else w.Null();
            w.Key("affl");
            if (c.Affliction != null)
            {
                w.Obj(); w.Key("id").Str(c.Affliction.Id.Entry); w.Key("amt").Num(c.Affliction.Amount);
                WriteModelFields(w, c.Affliction, typeof(AfflictionModel)); w.End();
            }
            else w.Null();
            WriteModelFields(w, c, typeof(CardModel), depth * 2);
            WriteVars(w, c);
            w.End();
        }

        // ------------------------------------------------------------------ creatures / powers

        static void WriteCreatureBasic(W w, Creature c, bool close)
        {
            w.Obj();
            w.Key("id").Str(c.ModelId.Entry);
            w.Key("cid").Num(c.CombatId.HasValue ? (long)c.CombatId.Value : -1);
            w.Key("slot").Str(c.SlotName);
            w.Key("hp").Num(c.CurrentHp);
            w.Key("maxHp").Num(c.MaxHp);
            w.Key("block").Num(c.Block);
            w.Key("alive").Bool(c.IsAlive);
            w.Key("powers"); WritePowers(w, c);
            if (close) w.End();
        }

        static void WriteEnemy(W w, Creature e)
        {
            WriteCreatureBasic(w, e, false);
            var m = e.Monster;
            w.Key("monster");
            if (m == null) { w.Null(); w.End(); return; }
            w.Obj();
            w.Key("spawnedThisTurn").Bool(m.SpawnedThisTurn);
            var sm = m.MoveStateMachine;
            var next = m.NextMove;
            w.Key("next").Str(next?.Id);
            w.Key("nextPerformed").Bool(next != null && GetField(next, "_performedAtLeastOnce") is true);
            w.Key("nextMustPerformOnce").Bool(next?.MustPerformOnceBeforeTransitioning ?? false);
            w.Key("nextFollowUp").Str(next == null ? null : (next.FollowUpState?.Id ?? next.FollowUpStateId));
            w.Key("nextRegistered").Bool(sm != null && next != null && sm.States.TryGetValue(next.Id, out var reg) && reg == next);
            if (sm != null)
            {
                var cur = GetField(sm, "_currentState") as MonsterState;
                w.Key("cur").Str(cur?.Id);
                w.Key("curIsMove").Bool(cur?.IsMove ?? false);
                w.Key("performedFirstMove").Bool(GetField(sm, "_performedFirstMove") is true);
                w.Key("log").Arr();
                foreach (var s in sm.StateLog) w.Str(s.Id);
                w.End();
            }
            w.Key("rng"); WriteRng(w, SafeGet(() => m.Rng));
            WriteModelFields(w, m, typeof(MonsterModel));
            w.End();
            w.End();
        }

        static void WritePowers(W w, Creature c)
        {
            w.Arr();
            foreach (var p in c.Powers)
            {
                w.Obj();
                w.Key("id").Str(p.Id.Entry);
                w.Key("amt").Num(p.Amount);
                w.Key("aots").Num(p.AmountOnTurnStart);
                w.Key("skip").Bool(p.SkipNextDurationTick);
                w.Key("applier");
                if (p.Applier?.CombatId != null) w.Num(p.Applier.CombatId.Value); else w.Null();
                w.Key("target");
                var tgt = GetField(p, "_target") as Creature;
                if (tgt?.CombatId != null) w.Num(tgt.CombatId.Value); else w.Null();
                var data = GetField(p, "_internalData");
                w.Key("d");
                if (data != null) WriteObjectFields(w, data, null, 1); else w.Null();
                WriteModelFields(w, p, typeof(PowerModel));
                WriteVars(w, p);
                w.End();
            }
            w.End();
        }

        // ------------------------------------------------------------------ reflection helpers

        static void WriteModelFields(W w, object o, Type stopAt, int depth = 0)
        {
            w.Key("f");
            WriteObjectFields(w, o, stopAt, depth);
        }

        static void WriteVars(W w, object o)
        {
            w.Key("v").Obj();
            try
            {
                var pi = o.GetType().GetProperty("DynamicVars", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pi?.GetValue(o) is IEnumerable vars)
                    foreach (var kv in vars)
                    {
                        var t = kv.GetType();
                        var key = t.GetProperty("Key")?.GetValue(kv) as string;
                        var dv = t.GetProperty("Value")?.GetValue(kv);
                        if (key == null || dv == null) continue;
                        var bv = dv.GetType().GetProperty("BaseValue")?.GetValue(dv);
                        if (bv is decimal d) w.Key(key).Dec(d);
                    }
            }
            catch { }
            w.End();
        }

        // Writes {name: value} for the instance fields declared by o's type and its bases up to (excluding) stopAt
        // (stopAt == null: only o's own type chain up to object).
        static void WriteObjectFields(W w, object o, Type stopAt, int depth)
        {
            w.Obj();
            for (var t = o.GetType(); t != null && t != stopAt && t != typeof(object) && t != typeof(AbstractModel); t = t.BaseType)
                foreach (var f in t.GetFields(Inst))
                {
                    if (typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                    if (f.Name.Contains("<>") || f.Name.StartsWith("CS$")) continue;
                    object v;
                    try { v = f.GetValue(o); } catch { continue; }
                    string name = f.Name;
                    if (name.StartsWith("<") && name.Contains(">k__BackingField")) name = name.Substring(1, name.IndexOf('>') - 1);
                    w.Key(name);
                    WriteValue(w, v, depth + 1);
                }
            w.End();
        }

        static void WriteValue(W w, object v, int depth)
        {
            switch (v)
            {
                case null: w.Null(); return;
                case bool b: w.Bool(b); return;
                case string s: w.Str(s); return;
                case Enum e: w.Str(e.ToString()); return;
                case decimal d: w.Dec(d); return;
                case double d: w.Raw(d.ToString("R", CultureInfo.InvariantCulture)); return;
                case float f: w.Raw(((double)f).ToString("R", CultureInfo.InvariantCulture)); return;
                case ulong u: w.U64(u); return;
                case long l: w.Num(l); return;
                case int or uint or short or ushort or byte or sbyte: w.Num(Convert.ToInt64(v)); return;
                case MegaCrit.Sts2.Core.Random.Rng r: w.Obj(); w.Key("$rng"); WriteRng(w, r); w.End(); return;
                case CardModel c: w.Obj(); w.Key("$card"); WriteCard(w, c, depth <= 2 ? 1 : 2); w.End(); return;
                case Creature cr: w.Obj(); w.Key("$cid"); if (cr.CombatId.HasValue) w.Num(cr.CombatId.Value); else w.Null(); w.End(); return;
                case PowerModel p: w.Obj(); w.Key("$power").Str(p.Id.Entry); w.End(); return;
                case MonsterState st: w.Obj(); w.Key("$state").Str(st.Id); w.End(); return;
                case AbstractModel am: w.Obj(); w.Key("$model").Str(SafeGet(() => am.Id.Entry)); w.End(); return;
            }
            var t = v.GetType();
            if (t.Namespace != null && t.Namespace.StartsWith("Godot")) { w.Str("$" + t.Name); return; }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>)) { WriteValue(w, t.GetProperty("Value").GetValue(v), depth); return; }
            if (v is IDictionary dict)
            {
                if (depth > 3) { w.Str("$dict"); return; }
                w.Arr();
                int n = 0;
                foreach (var key in dict.Keys.Cast<object>().ToList())
                {
                    if (n++ >= 100) break;
                    w.Arr(); WriteValue(w, key, depth + 1); WriteValue(w, dict[key], depth + 1); w.End();
                }
                w.End();
                return;
            }
            if (v is IEnumerable en)
            {
                if (depth > 3) { w.Str("$list"); return; }
                w.Arr();
                int n = 0;
                foreach (var x in en) { if (n++ >= 100) break; WriteValue(w, x, depth + 1); }
                w.End();
                return;
            }
            if (t.IsValueType && t.IsPrimitive == false && depth <= 3)
            {
                // structs (e.g. MapCoord): their fields
                w.Obj();
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    string name = f.Name;
                    if (name.StartsWith("<") && name.Contains(">k__BackingField")) name = name.Substring(1, name.IndexOf('>') - 1);
                    w.Key(name); WriteValue(w, f.GetValue(v), depth + 1);
                }
                w.End();
                return;
            }
            if (t.IsClass && depth <= 3 && (t.IsNested || t.Namespace == null || t.Namespace.StartsWith("MegaCrit")))
            {
                WriteObjectFields(w, v, null, depth);
                return;
            }
            w.Str("$" + t.Name);
        }

        static object GetField(object o, string name)
        {
            if (o == null) return null;
            for (var t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, Inst);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }
        static object GetFieldDeep(object o, string name) => GetField(o, name);

        static T SafeGet<T>(Func<T> f)
        {
            try { return f(); } catch { return default; }
        }

        // ------------------------------------------------------------------ tiny JSON writer

        public sealed class W
        {
            readonly StringBuilder _sb = new StringBuilder(16384);
            readonly Stack<bool> _first = new Stack<bool>();   // per open container: nothing written yet
            readonly Stack<bool> _isObj = new Stack<bool>();
            bool _afterKey;

            void Sep()
            {
                if (_afterKey) { _afterKey = false; return; }
                if (_first.Count > 0)
                {
                    if (_first.Peek()) { _first.Pop(); _first.Push(false); }
                    else _sb.Append(',');
                }
            }
            public W Obj() { Sep(); _sb.Append('{'); _first.Push(true); _isObj.Push(true); return this; }
            public W Arr() { Sep(); _sb.Append('['); _first.Push(true); _isObj.Push(false); return this; }
            public W End()
            {
                _first.Pop();
                _sb.Append(_isObj.Pop() ? '}' : ']');
                return this;
            }
            public W Key(string k) { Sep(); WriteString(k); _sb.Append(':'); _afterKey = true; return this; }
            public W Str(string s) { Sep(); if (s == null) _sb.Append("null"); else WriteString(s); return this; }
            public W Num(long v) { Sep(); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }
            public W U64(ulong v) { Sep(); _sb.Append('"').Append(v.ToString(CultureInfo.InvariantCulture)).Append('"'); return this; }
            public W Dec(decimal d) { Sep(); _sb.Append(d.ToString(CultureInfo.InvariantCulture)); return this; }
            public W Bool(bool b) { Sep(); _sb.Append(b ? "true" : "false"); return this; }
            public W Null() { Sep(); _sb.Append("null"); return this; }
            public W Raw(string s) { Sep(); _sb.Append(s); return this; }
            void WriteString(string s)
            {
                _sb.Append('"');
                foreach (char ch in s)
                {
                    switch (ch)
                    {
                        case '"': _sb.Append("\\\""); break;
                        case '\\': _sb.Append("\\\\"); break;
                        case '\n': _sb.Append("\\n"); break;
                        case '\r': _sb.Append("\\r"); break;
                        case '\t': _sb.Append("\\t"); break;
                        default:
                            if (ch < 0x20) _sb.Append("\\u").Append(((int)ch).ToString("x4")); else _sb.Append(ch);
                            break;
                    }
                }
                _sb.Append('"');
            }
            public override string ToString() => _sb.ToString();
        }
    }
}
