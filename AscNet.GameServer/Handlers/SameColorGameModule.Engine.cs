using AscNet.Common.Database;
using AscNet.Common.Util;
using AscNet.Table.V2.share.samecolorgame;

namespace AscNet.GameServer.Handlers;

/// <summary>
/// Board machine for SameColorGame (Circuit Connect).
///
/// Field authority: the thirteen shared SameColorGame tables are authoritative for content (orbs,
/// roles, bosses, boss skills, buffs, skills, score/combo curves, passive rows). Action types,
/// record shapes and ordering follow the shipped client consumers in XSCBattleManager and
/// XEnumConst.
///
/// Retail server authority for board generation, RNG, overlapping-match decomposition, effect
/// ordering, the per-move score formula and cascade bounds is not available from any recovered
/// source. Every rule chosen for those gaps is an explicit AscNet policy documented at its use
/// site. The generator is counter-based and persisted with the run, so replaying the same requests
/// against the same run state produces identical actions.
/// </summary>
internal static class SameColorGameEngine
{
    // AscNet policy: hard bounds keeping one response finite. Reaching a bound always ends in a
    // visible resolution (an explicit reshuffle action), never a silent truncation.
    internal const int MaxCascadeSteps = 64;
    internal const int MaxShuffleAttempts = 64;

    // AscNet policy: one authored passive (Echoing Twins) declares a basic-attack appearance bonus
    // in text only, with no probability column in the table. Its authored +5% is applied as a
    // relative weight on the role's basic-attack orb (base weight is 1000, so 50 = +5%).
    internal const int AppearanceBonusPassiveId = 6;
    internal const int AppearanceBonusWeight = 50;

    internal const int PlayerBuffOwner = -1;

    // AscNet policy: client BALL_REMOVE_TYPE anchors are presentation-only markers that select
    // effect anchors. They never change engine behaviour.
    internal const int RemoveTypeNone = 0;
    internal const int RemoveTypeBoomCenter = 1;
    internal const int RemoveTypeVeraLightning = 4;
    internal const int RemoveTypeAlisaWave = 5;

    internal const decimal PercentScale = 1000m;

    internal static readonly Lazy<Dictionary<int, SameColorGameBallTable>> Balls =
        new(() => TableReaderV2.Parse<SameColorGameBallTable>().ToDictionary(row => row.Id));
    internal static readonly Lazy<Dictionary<int, SameColorGameRoleTable>> Roles =
        new(() => TableReaderV2.Parse<SameColorGameRoleTable>().ToDictionary(row => row.Id));
    internal static readonly Lazy<Dictionary<int, SameColorGameBossTable>> Bosses =
        new(() => TableReaderV2.Parse<SameColorGameBossTable>().ToDictionary(row => row.Id));
    internal static readonly Lazy<Dictionary<int, SameColorGameBossSkillTable>> BossSkills =
        new(() => TableReaderV2.Parse<SameColorGameBossSkillTable>().ToDictionary(row => row.Id));
    internal static readonly Lazy<Dictionary<int, SameColorGameBuffTable>> Buffs =
        new(() => TableReaderV2.Parse<SameColorGameBuffTable>().ToDictionary(row => row.Id));
    internal static readonly Lazy<Dictionary<int, SameColorGameSkillTable>> Skills =
        new(() => TableReaderV2.Parse<SameColorGameSkillTable>().ToDictionary(row => row.Id));
    internal static readonly Lazy<Dictionary<int, SameColorGameSkillGroupTable>> SkillGroups =
        new(() => TableReaderV2.Parse<SameColorGameSkillGroupTable>().ToDictionary(row => row.Id));
    internal static readonly Lazy<Dictionary<int, SameColorGamePassiveSkillTable>> Passives =
        new(() => TableReaderV2.Parse<SameColorGamePassiveSkillTable>().ToDictionary(row => row.Id));
    internal static readonly Lazy<Dictionary<int, SameColorGameAttributeFactorTable>> AttributeFactors =
        new(() => TableReaderV2.Parse<SameColorGameAttributeFactorTable>().ToDictionary(row => row.Id));
    internal static readonly Lazy<List<SameColorGameScoreTable>> ScoreCurve =
        new(() => TableReaderV2.Parse<SameColorGameScoreTable>().OrderBy(row => row.Id).ToList());
    internal static readonly Lazy<List<SameColorGameComboTable>> ComboCurve =
        new(() => TableReaderV2.Parse<SameColorGameComboTable>().OrderBy(row => row.Id).ToList());

    #region Table helpers

    internal static SameColorGameRoleTable? Role(int roleId) =>
        Roles.Value.TryGetValue(roleId, out SameColorGameRoleTable? role) ? role : null;

    internal static SameColorGameBossTable? Boss(int bossId) =>
        Bosses.Value.TryGetValue(bossId, out SameColorGameBossTable? boss) ? boss : null;

    internal static decimal ScoreFactorOf(SameColorGameSkillTable? skill) =>
        skill?.ScoreFactor is double factor ? (decimal)factor : 1m;

    internal static int BallScore(int ballId) =>
        Balls.Value.TryGetValue(ballId, out SameColorGameBallTable? ball) ? ball.Score.GetValueOrDefault() : 0;

    internal static int BallColor(int ballId) =>
        Balls.Value.TryGetValue(ballId, out SameColorGameBallTable? ball) ? ball.Color.GetValueOrDefault() : 0;

    internal static int BallWeakHitTimes(int ballId) =>
        Balls.Value.TryGetValue(ballId, out SameColorGameBallTable? ball)
            ? Math.Max(1, ball.WeakHitTimes.GetValueOrDefault(1))
            : 1;

    internal static int ScoreCurveValue(int orbCount)
    {
        List<SameColorGameScoreTable> curve = ScoreCurve.Value;
        if (curve.Count == 0) return 0;
        return curve[Math.Clamp(orbCount, 1, curve.Count) - 1].Score;
    }

    internal static decimal ComboPercent(int combo)
    {
        List<SameColorGameComboTable> curve = ComboCurve.Value;
        if (curve.Count == 0) return PercentScale;
        return curve[Math.Clamp(combo, 1, curve.Count) - 1].Percent;
    }

    /// <summary>Authored attribute bonus: only when the boss is weak to the role's attribute.</summary>
    internal static decimal AttributeBonus(SameColorGameRun run)
    {
        SameColorGameRoleTable? role = Role(run.RoleId);
        SameColorGameBossTable? boss = Boss(run.BossId);
        if (role is null || boss is null) return 0m;
        if (!AttributeFactors.Value.TryGetValue(role.AttributeFactorId, out SameColorGameAttributeFactorTable? factor))
            return 0m;
        return factor.Type == boss.AttributeType ? (decimal)factor.Factor : 0m;
    }

    /// <summary>
    /// Active orb damage modifiers for one colour. AscNet policy: buff DamagePercent is authored in
    /// tenths of a percent (500 = 50%, 75 = 7.5%), which the shipped buff descriptions confirm.
    /// </summary>
    internal static decimal ColourDamageDelta(SameColorGameRun run, int colour)
    {
        decimal delta = 0m;
        foreach (SameColorGameBuffState state in run.Buffs)
        {
            if (!Buffs.Value.TryGetValue(state.BuffId, out SameColorGameBuffTable? buff)) continue;
            if (buff.Type is not (3 or 4)) continue;
            if (buff.TargetColors.Count > 0 && !buff.TargetColors.Contains(colour)) continue;
            decimal value = buff.DamagePercent.GetValueOrDefault() / PercentScale;
            delta += buff.Type == 3 ? value : -value;
        }
        return delta;
    }

    #endregion

    #region Board primitives

    internal static int Index(SameColorGameRun run, int x, int y) => y * run.Cols + x;

    internal static bool InBounds(SameColorGameRun run, int x, int y) =>
        x >= 0 && y >= 0 && x < run.Cols && y < run.Rows;

    internal static SameColorGameCell? CellAt(SameColorGameRun run, int index) =>
        index >= 0 && index < run.Board.Count && run.Board[index].ItemId != 0 ? run.Board[index] : null;

    internal static bool IsNormal(SameColorGameCell? cell) => cell is { BallType: 1 };
    internal static bool IsProp(SameColorGameCell? cell) => cell is { BallType: 2 };
    internal static bool IsSummon(SameColorGameCell? cell) => cell is { BallType: 3 };

    internal static SameColorGameBallRecord Record(SameColorGameRun run, int index, int itemType = 0)
    {
        SameColorGameCell cell = run.Board[index];
        List<int>? buffUids = cell.BallType == 3
            ? run.Buffs.Where(buff => buff.OwnerWeakUid == cell.WeakUid).Select(buff => buff.Uid).ToList()
            : null;
        if (buffUids is { Count: 0 }) buffUids = null;
        return new SameColorGameBallRecord
        {
            ItemId = cell.ItemId,
            PositionX = index % run.Cols,
            PositionY = index / run.Cols,
            BallType = cell.BallType,
            ItemType = itemType,
            WeakUid = cell.WeakUid,
            WeakHitTimes = cell.WeakHitTimes,
            BuffUids = buffUids
        };
    }

    internal static SameColorGameCell NormalCell(int ballId) =>
        new() { ItemId = ballId, BallType = 1, WeakUid = 0, WeakHitTimes = 0 };

    internal static List<int> Neighbours(SameColorGameRun run, int index)
    {
        int x = index % run.Cols;
        int y = index / run.Cols;
        List<int> result = new(4);
        if (InBounds(run, x, y - 1)) result.Add(Index(run, x, y - 1));
        if (InBounds(run, x, y + 1)) result.Add(Index(run, x, y + 1));
        if (InBounds(run, x - 1, y)) result.Add(Index(run, x - 1, y));
        if (InBounds(run, x + 1, y)) result.Add(Index(run, x + 1, y));
        return result;
    }

    #endregion

    #region Random

    // AscNet policy: counter-based SplitMix64 over a persisted (seed, step) pair. Retail seed
    // handling is not recoverable; this generator is deterministic, replayable from the persisted
    // run and cannot desynchronise from the board state.
    internal static int RandomInt(SameColorGameRun run, int bound)
    {
        if (bound <= 1) return 0;
        run.RngStep++;
        ulong x = (ulong)run.RngSeed + (ulong)run.RngStep * 0x9E3779B97F4A7C15UL;
        x ^= x >> 30;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 27;
        x *= 0x94D049BB133111EBUL;
        x ^= x >> 31;
        return (int)(x % (ulong)bound);
    }

    /// <summary>
    /// Appearance weights for refill orbs, from the role's authored orb set. AscNet policy: the
    /// authored Echoing Twins passive adds its +5% here (base weight 1000), and a drop-exclusion
    /// skill (603) removes its colour from the draw.
    /// </summary>
    internal static List<int> BallWeights(SameColorGameRoleTable role)
    {
        List<int> weights = role.BallId.Select(_ => 1000).ToList();
        if (weights.Count > 0 && role.PassiveSkillId == AppearanceBonusPassiveId)
            weights[^1] += AppearanceBonusWeight;
        return weights;
    }

    internal static int RollBall(SameColorGameRun run, SameColorGameRoleTable role)
    {
        List<int> weights = BallWeights(role);
        int excluded = run.ExcludedColorLeft > 0 ? run.ExcludedColor : 0;
        int total = 0;
        for (int index = 0; index < role.BallId.Count; index++)
        {
            if (excluded != 0 && BallColor(role.BallId[index]) == excluded) continue;
            total += weights[index];
        }
        if (total <= 0) return role.BallId.Count > 0 ? role.BallId[0] : 0;

        int roll = RandomInt(run, total);
        for (int index = 0; index < role.BallId.Count; index++)
        {
            if (excluded != 0 && BallColor(role.BallId[index]) == excluded) continue;
            roll -= weights[index];
            if (roll < 0) return role.BallId[index];
        }
        return role.BallId[^1];
    }

    internal static void Shuffle<T>(SameColorGameRun run, IList<T> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = RandomInt(run, i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    #endregion

    #region Run construction

    internal static void InitializeRun(SameColorGameRun run, SameColorGameRoleTable role, SameColorGameBossTable boss)
    {
        run.Rows = role.Row;
        run.Cols = role.Col;
        run.MaxRound = boss.MaxRound;
        run.CurRound = 1;
        // Every authored boss is a round stage: the imported authority exposes no MaxTime column, and
        // no current SameColorGameBoss row authors a time limit. Timed-stage RPCs therefore report the
        // authored "not timed stage" error rather than running an uninvented clock.
        run.Board = Enumerable.Range(0, run.Rows * run.Cols).Select(_ => new SameColorGameCell()).ToList();

        // AscNet policy: the opening board has no matches and at least one legal move. The bounded
        // random attempts below always terminate on a playable board through the deterministic
        // repair pass.
        for (int attempt = 0; attempt < MaxShuffleAttempts; attempt++)
        {
            FillRandom(run, role);
            if (!HasAnyMatch(run) && HasLegalMove(run)) return;
        }
        EnsurePlayable(run, role);
    }

    private static void FillRandom(SameColorGameRun run, SameColorGameRoleTable role)
    {
        for (int index = 0; index < run.Board.Count; index++)
            run.Board[index] = NormalCell(RollBall(run, role));
    }

    /// <summary>Deterministic repair: replace orbs inside matches until none remain.</summary>
    private static void RepairBoard(SameColorGameRun run, SameColorGameRoleTable role)
    {
        for (int guard = 0; guard < run.Board.Count * 4; guard++)
        {
            HashSet<int> matched = MatchedIndices(run);
            if (matched.Count == 0) break;
            foreach (int index in matched)
                run.Board[index] = NormalCell(RollBall(run, role));
        }
    }

    /// <summary>
    /// Guarantees a published board has a legal move. AscNet policy: matches are repaired first; when
    /// the repaired board still offers no legal swap, a deterministic three-in-a-row with a
    /// completing neighbour is written into a run of ordinary cells, which the next cascade resolves
    /// visibly. Special cells (props and summons) are never overwritten, so no owned summon buff or
    /// weak record can be orphaned. A board whose ordinary cells cannot host such a run is left
    /// match-free; the authored content cannot reach that state, because summons only spawn on
    /// ordinary cells and boss skills cap how many exist at once.
    /// </summary>
    private static void EnsurePlayable(SameColorGameRun run, SameColorGameRoleTable role)
    {
        // Repair first: HasLegalMove only asks whether some swap can match, which is also true while
        // an unrelated match is still sitting on the board.
        RepairBoard(run, role);
        if (HasLegalMove(run) || role.BallId.Count == 0) return;

        int ballId = role.BallId[0];
        int separator = role.BallId.Count > 1 ? role.BallId[1] : role.BallId[0];

        // Horizontal three plus the single orb that completes it when swapped in. Only these four
        // cells must be ordinary: demanding a whole rectangle would miss boards where summons sit
        // inside every rectangle while a valid pattern still exists.
        for (int y = 0; y + 1 < run.Rows; y++)
        for (int x = 0; x + 2 < run.Cols; x++)
        {
            if (!OrdinaryCells(run, Index(run, x, y), Index(run, x + 1, y), Index(run, x + 2, y), Index(run, x + 1, y + 1)))
                continue;
            run.Board[Index(run, x, y)] = NormalCell(ballId);
            run.Board[Index(run, x + 1, y)] = NormalCell(separator);
            run.Board[Index(run, x + 2, y)] = NormalCell(ballId);
            run.Board[Index(run, x + 1, y + 1)] = NormalCell(ballId);
            return;
        }

        // Vertical three plus the orb that completes it when swapped in.
        for (int y = 0; y + 2 < run.Rows; y++)
        for (int x = 0; x + 1 < run.Cols; x++)
        {
            if (!OrdinaryCells(run, Index(run, x, y), Index(run, x, y + 1), Index(run, x, y + 2), Index(run, x + 1, y + 1)))
                continue;
            run.Board[Index(run, x, y)] = NormalCell(ballId);
            run.Board[Index(run, x, y + 1)] = NormalCell(separator);
            run.Board[Index(run, x, y + 2)] = NormalCell(ballId);
            run.Board[Index(run, x + 1, y + 1)] = NormalCell(ballId);
            return;
        }
    }

    /// <summary>
    /// A cell can carry an authored effect anchor when the effect actually removes it: ordinary orbs
    /// and props both appear in the removal list, while a summon takes a direct hit and never does.
    /// </summary>
    private static bool Anchorable(SameColorGameRun run, int index)
    {
        SameColorGameCell? cell = CellAt(run, index);
        return IsNormal(cell) || IsProp(cell);
    }

    private static bool OrdinaryCells(SameColorGameRun run, params int[] indices)
    {
        foreach (int index in indices)
        {
            if (!InBounds(run, index % run.Cols, index / run.Cols)) return false;
            if (!IsNormal(CellAt(run, index))) return false;
        }
        return true;
    }

    internal static void ShuffleBoard(SameColorGameRun run, SameColorGameRoleTable role, bool keepMultiset = true)
    {
        // AscNet policy: a shuffle keeps the ordinary-orb multiset and retries until the layout has
        // no matches and at least one legal move; the bounded tail refills randomly and finally
        // repairs in place, so the board is always playable when this returns.
        List<int> slots = run.Board
            .Select((cell, index) => (cell, index))
            .Where(entry => IsNormal(entry.cell))
            .Select(entry => entry.index)
            .ToList();
        List<int> pool = keepMultiset
            ? slots.Select(index => run.Board[index].ItemId).ToList()
            : slots.Select(_ => RollBall(run, role)).ToList();

        for (int attempt = 0; attempt < MaxShuffleAttempts; attempt++)
        {
            List<int> shuffled = new(pool);
            Shuffle(run, shuffled);
            for (int i = 0; i < slots.Count; i++)
                run.Board[slots[i]] = NormalCell(shuffled[i]);
            if (!HasAnyMatch(run) && HasLegalMove(run)) return;
        }
        for (int attempt = 0; attempt < MaxShuffleAttempts; attempt++)
        {
            foreach (int slot in slots)
                run.Board[slot] = NormalCell(RollBall(run, role));
            if (!HasAnyMatch(run) && HasLegalMove(run)) return;
        }
        EnsurePlayable(run, role);
    }

    #endregion

    #region Match detection

    internal sealed class SameColorMatch
    {
        internal int BallId;
        internal int PropType;
        internal int PlaceIndex;
        internal List<int> Indices = new();
    }

    internal static bool HasAnyMatch(SameColorGameRun run) => MatchedIndices(run).Count > 0;

    internal static HashSet<int> MatchedIndices(SameColorGameRun run)
    {
        HashSet<int> matched = new();
        foreach (SameColorMatch match in FindMatches(run)) matched.UnionWith(match.Indices);
        return matched;
    }

    /// <summary>
    /// Groups every maximal run of three or more same-orb cells. Runs sharing a cell form one
    /// match, which is what gives props their authored shape: straight/cross five or longer -> star,
    /// four -> rapid fire, five with exactly one interior crossing -> slash, five cornering -> blast.
    /// AscNet policy: retail's overlap decomposition is not recoverable from any available source.
    /// </summary>
    internal static List<SameColorMatch> FindMatches(SameColorGameRun run)
    {
        List<List<int>> runs = new();
        List<int> runBalls = new();
        for (int y = 0; y < run.Rows; y++)
        {
            int x = 0;
            while (x < run.Cols)
            {
                SameColorGameCell? cell = CellAt(run, Index(run, x, y));
                if (!IsNormal(cell))
                {
                    x++;
                    continue;
                }
                int end = x + 1;
                while (end < run.Cols && CellAt(run, Index(run, end, y))?.ItemId == cell!.ItemId) end++;
                if (end - x >= 3)
                {
                    runBalls.Add(cell!.ItemId);
                    runs.Add(Enumerable.Range(x, end - x).Select(offset => Index(run, offset, y)).ToList());
                }
                x = end;
            }
        }
        for (int x = 0; x < run.Cols; x++)
        {
            int y = 0;
            while (y < run.Rows)
            {
                SameColorGameCell? cell = CellAt(run, Index(run, x, y));
                if (!IsNormal(cell))
                {
                    y++;
                    continue;
                }
                int end = y + 1;
                while (end < run.Rows && CellAt(run, Index(run, x, end))?.ItemId == cell!.ItemId) end++;
                if (end - y >= 3)
                {
                    runBalls.Add(cell!.ItemId);
                    runs.Add(Enumerable.Range(y, end - y).Select(offset => Index(run, x, offset)).ToList());
                }
                y = end;
            }
        }

        List<SameColorMatch> matches = new();
        HashSet<int> consumed = new();
        for (int i = 0; i < runs.Count; i++)
        {
            if (!consumed.Add(i)) continue;
            List<int> component = new() { i };
            HashSet<int> componentCells = runs[i].ToHashSet();
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int j = 0; j < runs.Count; j++)
                {
                    if (consumed.Contains(j) || runBalls[j] != runBalls[i]) continue;
                    if (!runs[j].Any(componentCells.Contains)) continue;
                    consumed.Add(j);
                    component.Add(j);
                    componentCells.UnionWith(runs[j]);
                    grew = true;
                }
            }

            List<int> indices = component.SelectMany(index => runs[index]).Distinct().ToList();
            SameColorMatch match = new()
            {
                BallId = runBalls[i],
                Indices = indices,
                PropType = ResolvePropType(run, component.Select(index => runs[index]).ToList(), out int placeIndex)
            };
            match.PlaceIndex = placeIndex;
            matches.Add(match);
        }

        return matches
            .OrderBy(match => match.Indices.Min() % run.Cols)
            .ThenBy(match => match.Indices.Min() / run.Cols)
            .ToList();
    }

    private static int ResolvePropType(SameColorGameRun run, List<List<int>> runs, out int placeIndex)
    {
        List<List<int>> horizontal = runs.Where(cells => IsHorizontal(run, cells)).ToList();
        List<List<int>> vertical = runs.Where(cells => !IsHorizontal(run, cells)).ToList();
        int widestRow = horizontal.Select(cells => cells.Count).DefaultIfEmpty(0).Max();
        int widestColumn = vertical.Select(cells => cells.Count).DefaultIfEmpty(0).Max();
        placeIndex = runs.SelectMany(cells => cells).Min();

        if (widestRow >= 3 && widestColumn >= 3)
        {
            List<int> rowRun = horizontal.First(cells => cells.Count == widestRow);
            List<int> columnRun = vertical.First(cells => cells.Count == widestColumn);
            int? crossing = rowRun.Where(columnRun.Contains).Select(index => (int?)index).FirstOrDefault();
            if (crossing is int cross)
            {
                placeIndex = cross;
                if (widestRow >= 5 || widestColumn >= 5) return 1;
                int rowAt = rowRun.IndexOf(cross);
                int columnAt = columnRun.IndexOf(cross);
                bool rowInterior = rowAt > 0 && rowAt < rowRun.Count - 1;
                bool columnInterior = columnAt > 0 && columnAt < columnRun.Count - 1;
                if (rowInterior && columnInterior) return 1;   // cross -> star
                if (rowInterior || columnInterior) return 3;   // T shape -> slash
                return 2;                                      // L shape -> blast
            }
        }
        if (widestRow >= 5 || widestColumn >= 5) return 1;
        if (widestRow == 4 || widestColumn == 4) return 4;
        // A plain three-orb match creates no prop; its cells are simply dissolved.
        return 0;
    }

    private static bool IsHorizontal(SameColorGameRun run, List<int> cells) =>
        cells.Count > 0 && cells.All(index => index / run.Cols == cells[0] / run.Cols);

    internal static int PropBallFor(SameColorGameRoleTable role, int propType)
    {
        foreach (int ballId in role.PropBallIds)
        {
            if (!Balls.Value.TryGetValue(ballId, out SameColorGameBallTable? ball)) continue;
            if (ball.PropType.GetValueOrDefault() == propType) return ballId;
        }
        return 0;
    }

    #endregion

    #region Removal

    internal sealed class SameColorRemovalGroup
    {
        internal int BallId;
        internal List<int> Indices = new();
        internal List<int> ItemTypes = new();
        internal decimal ScoreFactor = 1m;
        internal int Combo;
        internal bool Scored = true;
    }

    /// <summary>
    /// Emits the removal actions for one step and then resolves what those removals triggered. A
    /// cleared prop activates immediately after its own removal action, so the client's remove scanner
    /// always sees the origin's collateral next to it; a consumed prop activates exactly once and the
    /// board holds finitely many, so chaining needs no depth cap and drops nothing.
    /// </summary>
    internal static void EmitRemovals(SameColorGameRun run, SameColorResponseContext ctx, List<SameColorRemovalGroup> groups)
    {
        HashSet<int> cleared = EmitRemovalAction(run, ctx, groups);
        if (cleared.Count == 0) return;
        ResolveSummons(run, ctx, cleared);
    }

    /// <summary>
    /// Pure removal step: score, action, board clear. No chains and no summon resolution.
    ///
    /// Two client contracts shape the emitted actions:
    ///  - XUiPanelBoard keeps a single prop/summon origin per remove action and plays its effect
    ///    once, so an action never carries more than one special ball;
    ///  - XSCBattleManager:GetCurUsingSkillId only attributes an action to the in-use skill when one
    ///    of the action's own balls carries a non-zero ItemType, so each step keeps the marker that
    ///    attributes it (see the per-skill markers below).
    /// Ordinary cells of one match stay together, which is what the observed stream shows.
    /// </summary>
    private static HashSet<int> EmitRemovalAction(
        SameColorGameRun run, SameColorResponseContext ctx, List<SameColorRemovalGroup> groups)
    {
        // Ordered plan: one step per group of ordinary cells and one step per special ball, so the
        // client's single-origin rule holds. Cells are re-validated when each step is emitted, because
        // an earlier step's effect can clear cells a later step still lists.
        List<(SameColorRemovalGroup Group, int Index, int ItemType)> cells = new();
        foreach (SameColorRemovalGroup group in groups)
        {
            for (int i = 0; i < group.Indices.Count; i++)
            {
                int itemType = i < group.ItemTypes.Count ? group.ItemTypes[i] : RemoveTypeNone;
                cells.Add((group, group.Indices[i], itemType));
            }
        }
        if (cells.Count == 0) return new HashSet<int>();

        // The attribution marker belongs to this step only; a cascade that follows in the same
        // response must not claim to be a direct skill effect.
        bool attribute = ctx.AttributeNextRemoval;
        ctx.AttributeNextRemoval = false;

        List<List<(SameColorRemovalGroup Group, int Index, int ItemType)>> steps = new();
        List<(SameColorRemovalGroup Group, int Index, int ItemType)> ordinary = new();
        foreach ((SameColorRemovalGroup group, int index, int itemType) in cells)
        {
            SameColorGameCell? cell = CellAt(run, index);
            if (cell is null || cell.BallType == 1)
            {
                ordinary.Add((group, index, itemType));
                continue;
            }
            if (ordinary.Count > 0)
            {
                steps.Add(ordinary);
                ordinary = new List<(SameColorRemovalGroup Group, int Index, int ItemType)>();
            }
            steps.Add(new List<(SameColorRemovalGroup Group, int Index, int ItemType)> { (group, index, itemType) });
        }
        if (ordinary.Count > 0) steps.Add(ordinary);

        // Publish steps that carry an authored anchor first, then the remaining ordinary cells, then
        // the special balls. A special's chained effect can clear cells listed in a later step, and the
        // anchor is what places the authored effect, so it must be emitted before any recursion.
        steps = steps
            .OrderByDescending(step => step.Any(entry => entry.ItemType != RemoveTypeNone))
            .ThenBy(step => step.Any(entry => CellAt(run, entry.Index) is { BallType: not 1 }) ? 1 : 0)
            .ToList();

        HashSet<int> cleared = new();
        HashSet<SameColorRemovalGroup> countedGroups = new();
        foreach (List<(SameColorRemovalGroup Group, int Index, int ItemType)> step in steps)
        {
            decimal raw = 0m;
            int combo = 0;
            List<SameColorGameBallRecord> items = new();
            List<(int Index, int BallId, int BallType)> toClear = new();
            foreach ((SameColorRemovalGroup group, int index, int itemType) in step)
            {
                SameColorGameCell? cell = CellAt(run, index);
                if (cell is null) continue;
                if (countedGroups.Add(group))
                {
                    // A group is scored and counted once, using the cells it still owns at this moment.
                    int live = group.Indices.Count(candidate => CellAt(run, candidate) is not null);
                    if (live > 0)
                    {
                        raw += GroupRawScore(run, ctx, group, live);
                        combo += group.Combo;
                        if (group.Combo > 0)
                            ctx.ComboByBall[group.BallId] = ctx.ComboByBall.GetValueOrDefault(group.BallId) + group.Combo;
                    }
                }
                items.Add(Record(run, index, itemType));
                toClear.Add((index, cell.ItemId, cell.BallType));
                int colour = BallColor(cell.ItemId);
                ctx.ColourRemoveCount[colour] = ctx.ColourRemoveCount.GetValueOrDefault(colour) + 1;
                cleared.Add(index);
                ctx.RemovedCount++;
            }
            if (items.Count == 0) continue;

            ctx.RawScore += raw;
            ctx.Combo += combo;
            ctx.HasScore = true;
            // One marker per action when a skill owns this response: the client checks the action's
            // own balls to attribute it, and authored anchors (VERA/ALISA) always win over this.
            if (attribute && !step.Any(entry => entry.ItemType != RemoveTypeNone))
                items[0].ItemType = RemoveTypeBoomCenter;
            ctx.Actions.Add(new SameColorGameAction
            {
                ActionType = SameColorGameActionType.ItemRemove,
                ItemList = items,
                CurrentScore = (long)decimal.Truncate(raw),
                CurrentCombo = combo
            });

            // Clear this step before anything it triggers inspects the board, then run the effect of a
            // special ball it removed so the collateral directly follows this action.
            foreach ((int index, int ballId, int ballType) in toClear)
            {
                // Register the cleared cell before anything it triggers: the next adjacency walk -
                // whether it belongs to this removal or to the effect running right after - must see
                // it, so a cell is never dropped from the once-per-move adjacency budget.
                ctx.PendingAdjacency.Add(index);
                run.Board[index] = new SameColorGameCell();
                if (ballType == 2) ActivatePropEffect(run, ctx, index, ballId);
            }
        }
        return cleared;
    }

    private static decimal GroupRawScore(SameColorGameRun run, SameColorResponseContext ctx, SameColorRemovalGroup group, int liveCount)
    {
        if (!group.Scored) return 0m;
        int colour = BallColor(group.BallId);
        decimal multiplier = 1m + AttributeBonus(run);
        multiplier += group.ScoreFactor - 1m;
        multiplier += ColourDamageDelta(run, colour);
        if (ctx.PassiveColour != 0 && ctx.PassiveColour == colour) multiplier += ctx.PassiveColourDelta;
        if (ctx.CriticalMove) multiplier *= 2m;
        if (multiplier < 0m) multiplier = 0m;
        return decimal.Truncate(BallScore(group.BallId) * ScoreCurveValue(liveCount) * multiplier);
    }

    /// <summary>
    /// Walks summon hits. AscNet policy: a summon takes at most one adjacency hit per move plus one
    /// hit per direct prop or skill hit; reaching zero removes it and runs its authored death skill.
    /// Ordering follows the sequence the client's remove scanner expects: hits, the defeated summon,
    /// then the death-skill collateral.
    /// </summary>
    internal static void ResolveSummons(SameColorGameRun run, SameColorResponseContext ctx, HashSet<int> cleared)
    {
        // Each iteration lands a hit or removes a summon for good, so the walk always terminates
        // inside this bound; the board size is the natural ceiling.
        for (int guard = 0; guard < run.Board.Count; guard++)
        {
            // Cells cleared by a step whose adjacency was deferred - including cells a nested effect
            // just cleared - join this walk.
            if (ctx.PendingAdjacency.Count > 0)
            {
                cleared.UnionWith(ctx.PendingAdjacency);
                ctx.PendingAdjacency.Clear();
            }
            bool hit = false;
            for (int index = 0; index < run.Board.Count; index++)
            {
                SameColorGameCell cell = run.Board[index];
                if (cell.BallType != 3 || cell.WeakHitTimes <= 0) continue;
                if (ctx.AdjacencyHitWeakUids.Contains(cell.WeakUid)) continue;
                if (!Neighbours(run, index).Any(cleared.Contains)) continue;
                ctx.AdjacencyHitWeakUids.Add(cell.WeakUid);
                AppendWeakHit(run, ctx, index);
                hit = true;
            }

            List<(int Index, int BallId, int WeakUid)> dead = run.Board
                .Select((cell, index) => (cell, index))
                .Where(entry => entry.cell.BallType == 3 && entry.cell.WeakHitTimes <= 0)
                .Select(entry => (entry.index, entry.cell.ItemId, entry.cell.WeakUid))
                .ToList();
            if (dead.Count == 0)
            {
                if (!hit) return;
                continue;
            }

            HashSet<int> wave = new();
            foreach ((int index, int ballId, int weakUid) in dead)
            {
                // A death skill can activate a prop whose effect already removed this summon, so the
                // snapshot entry is re-checked before it is removed or its skill runs.
                SameColorGameCell? living = CellAt(run, index);
                if (living is null || living.BallType != 3 || living.WeakUid != weakUid) continue;
                // One action per defeated summon, with its death-skill collateral emitted directly
                // after it, which is the pairing the client's remove scanner walks.
                wave.UnionWith(EmitRemovalAction(run, ctx, new List<SameColorRemovalGroup>
                {
                    new()
                    {
                        BallId = ballId,
                        Indices = new List<int> { index },
                        ItemTypes = new List<int> { RemoveTypeNone },
                        Combo = 1
                    }
                }));
                ApplyDeathSkill(run, ctx, index, ballId, weakUid, wave);
            }
            cleared = wave;
        }
    }

    internal static void AppendWeakHit(SameColorGameRun run, SameColorResponseContext ctx, int index)
    {
        SameColorGameCell cell = run.Board[index];
        if (cell.BallType != 3 || cell.WeakHitTimes <= 0) return;
        cell.WeakHitTimes--;
        run.WeakInfos.FirstOrDefault(info => info.Uid == cell.WeakUid)?.UpdateRounds.Add(run.CurRound);
        ctx.Actions.Add(new SameColorGameAction
        {
            ActionType = SameColorGameActionType.WeakHit,
            Destination = Record(run, index)
        });
    }

    /// <summary>Death-skill geometry: 106 row, 107 column, 108 buffs only, 110 row and column.</summary>
    internal static void ApplyDeathSkill(
        SameColorGameRun run, SameColorResponseContext ctx, int index, int ballId, int weakUid, HashSet<int> collateral)
    {
        foreach (SameColorGameBuffState owned in run.Buffs.Where(buff => buff.OwnerWeakUid == weakUid && weakUid != PlayerBuffOwner).ToList())
            RemoveBuff(run, ctx, owned);

        if (!Balls.Value.TryGetValue(ballId, out SameColorGameBallTable? ball)) return;
        if (ball.SkillId is not int skillId) return;
        if (!Skills.Value.TryGetValue(skillId, out SameColorGameSkillTable? skill)) return;

        int x = index % run.Cols;
        int y = index / run.Cols;
        List<int> area = new();
        switch (skill.Type)
        {
            case 106:
                area.AddRange(Enumerable.Range(0, run.Cols).Select(offset => Index(run, offset, y)));
                break;
            case 107:
                area.AddRange(Enumerable.Range(0, run.Rows).Select(offset => Index(run, x, offset)));
                break;
            case 110:
                area.AddRange(Enumerable.Range(0, run.Cols).Select(offset => Index(run, offset, y)));
                area.AddRange(Enumerable.Range(0, run.Rows).Select(offset => Index(run, x, offset)));
                break;
            case 108:
                break;
            default:
                // AscNet policy: an unauthored death-skill type still removes the summon and its
                // owned buffs. The authored current closure only uses 106/107/108/110.
                break;
        }

        if (area.Count > 0)
        {
            HashSet<int> directHits = new();
            foreach (int cell in area.Distinct())
            {
                SameColorGameCell? summon = CellAt(run, cell);
                if (!IsSummon(summon) || summon!.WeakHitTimes <= 0) continue;
                if (!directHits.Add(summon.WeakUid)) continue;
                AppendWeakHit(run, ctx, cell);
            }

            List<SameColorRemovalGroup> groups = PerOrbGroups(
                run, AreaTargets(run, area, area.Select(_ => RemoveTypeNone).ToList()), ScoreFactorOf(skill));
            if (groups.Count == 0) return;
            collateral.UnionWith(EmitRemovalAction(run, ctx, groups));
        }
        foreach (int buffId in skill.BuffIds)
            ApplyBuff(run, ctx, buffId, PlayerBuffOwner);
    }

    /// <summary>
    /// One removal group per target orb. Effect-driven removals are scored orb by orb with the
    /// authored single-orb curve step, which the observed prop sequence confirms: a star's twelve
    /// ordinary targets each scored a single-orb ping, and their removal action carried the sum.
    /// </summary>
    private static List<SameColorRemovalGroup> PerOrbGroups(
        SameColorGameRun run, IEnumerable<(int Index, int ItemType)> targets, decimal scoreFactor) =>
        targets
            .Where(target => CellAt(run, target.Index) is not null)
            .Select(target => new SameColorRemovalGroup
            {
                BallId = run.Board[target.Index].ItemId,
                Indices = new List<int> { target.Index },
                ItemTypes = new List<int> { target.ItemType },
                ScoreFactor = scoreFactor
            })
            .ToList();

    private static List<(int Index, int ItemType)> AreaTargets(
        SameColorGameRun run, List<int> indices, List<int> itemTypes)
    {
        List<(int Index, int ItemType)> targets = new();
        for (int i = 0; i < indices.Count; i++)
        {
            SameColorGameCell? cell = CellAt(run, indices[i]);
            if (!IsNormal(cell) && !IsProp(cell)) continue;
            targets.Add((indices[i], i < itemTypes.Count ? itemTypes[i] : RemoveTypeNone));
        }
        return targets.DistinctBy(target => target.Index).OrderBy(target => target.Index).ToList();
    }

    internal static List<int> OrderedByBoard(IEnumerable<int> indices, SameColorGameRun run) => indices
        .Distinct()
        .OrderByDescending(index => index / run.Cols)
        .ThenBy(index => index % run.Cols)
        .ToList();

    #endregion

    #region Props

    private static List<int> RandomOrdinaryTargets(SameColorGameRun run, int count)
    {
        List<int> candidates = run.Board
            .Select((cell, index) => (cell, index))
            .Where(entry => IsNormal(entry.cell))
            .Select(entry => entry.index)
            .ToList();
        Shuffle(run, candidates);
        return OrderedByBoard(candidates.Take(Math.Max(0, count)), run);
    }

    /// <summary>
    /// Activation requested by a player action (prop swap, double click, skill 604): dissolve the prop
    /// itself (one combo ping) and then run its effect. Props consumed by another effect activate from
    /// that effect's own removal step instead.
    /// </summary>
    internal static void ActivateProp(SameColorGameRun run, SameColorResponseContext ctx, int index, int ballId)
    {
        if (!Balls.Value.TryGetValue(ballId, out SameColorGameBallTable? ball)) return;
        // Another activation may already have consumed this prop earlier in the same response
        // (a blast covering a second prop, or skill 604 walking its board snapshot).
        SameColorGameCell? cell = CellAt(run, index);
        if (cell is null || !IsProp(cell) || cell.ItemId != ballId) return;

        EmitRemovalAction(run, ctx, new List<SameColorRemovalGroup>
        {
            new()
            {
                BallId = ballId,
                Indices = new List<int> { index },
                ItemTypes = new List<int> { RemoveTypeNone },
                Combo = 1
            }
        });
        // The step above registered the prop's cell for adjacency and ran its effect inline, so the
        // effect's removal sequence keeps the order the client's remove scanner expects while the
        // adjacency hit still lands in this move.
    }

    private static void ActivatePropEffect(SameColorGameRun run, SameColorResponseContext ctx, int index, int ballId)
    {
        if (!Balls.Value.TryGetValue(ballId, out SameColorGameBallTable? ball)) return;

        SameColorGamePropStat stat = GetPropStat(run, ballId, run.CurRound);
        stat.UseCount++;

        SameColorGameSkillTable? skill = ball.SkillId is int skillId && Skills.Value.TryGetValue(skillId, out SameColorGameSkillTable? row)
            ? row
            : null;
        decimal scoreFactor = ScoreFactorOf(skill);
        int propType = ball.PropType.GetValueOrDefault();
        int x = index % run.Cols;
        int y = index / run.Cols;

        switch (propType)
        {
            case 1:
            {
                // Shining Star: one hit on every summon, then dissolve the authored orb count.
                foreach (int summonIndex in Enumerable.Range(0, run.Board.Count).ToList())
                {
                    SameColorGameCell target = run.Board[summonIndex];
                    if (target.BallType != 3 || target.WeakHitTimes <= 0) continue;
                    AppendWeakHit(run, ctx, summonIndex);
                }
                int count = skill is { SkillParams.Count: > 0 } ? skill.SkillParams[0] : 12;
                EmitEffectTargets(run, ctx, RandomOrdinaryTargets(run, count), scoreFactor);
                break;
            }
            case 2:
            {
                // Blast: the 3x3 area around the prop.
                List<int> indices = new();
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!InBounds(run, x + dx, y + dy)) continue;
                    indices.Add(Index(run, x + dx, y + dy));
                }
                EmitAreaEffect(run, ctx, indices, Index(run, x, y), RemoveTypeBoomCenter, scoreFactor);
                break;
            }
            case 3:
            {
                // Vertical Slash: the prop's whole column.
                List<int> indices = Enumerable.Range(0, run.Rows).Select(row => Index(run, x, row)).ToList();
                EmitAreaEffect(run, ctx, indices, index, RemoveTypeNone, scoreFactor);
                break;
            }
            default:
            {
                // Rapid Fire: dissolve the authored number of random orbs.
                int count = skill is { SkillParams.Count: > 0 } ? skill.SkillParams[0] : 4;
                EmitEffectTargets(run, ctx, RandomOrdinaryTargets(run, count), scoreFactor);
                break;
            }
        }

        // A direct hit can be the only thing that defeats a summon in this effect, so the defeated
        // summon is resolved here as well instead of waiting for an unrelated removal.
        ResolveSummons(run, ctx, new HashSet<int>());
    }

    /// <summary>
    /// Removes an effect area. <paramref name="preferredAnchor"/> is the cell whose position the
    /// client uses to place the authored effect (it derives the centre as anchor + (n, -n)); when that
    /// cell is a summon or was already cleared it cannot carry the marker, so the nearest surviving
    /// ordinary target takes it - the effect still lands on the board instead of at a recycled
    /// prefab position, and no anchor is ever silently dropped.
    /// </summary>
    internal static void EmitAreaEffect(
        SameColorGameRun run, SameColorResponseContext ctx, List<int> indices, int preferredAnchor, int anchorType,
        decimal scoreFactor)
    {
        List<(int Index, int ItemType)> targets = AreaTargets(run, indices, new List<int>());

        // Summons standing in an effect area take one direct hit from this effect instead of being
        // dissolved. Direct hits are counted per effect; adjacency has its own once-per-move budget.
        HashSet<int> directHits = new();
        foreach (int index in indices.Distinct())
        {
            SameColorGameCell? cell = CellAt(run, index);
            if (!IsSummon(cell) || cell!.WeakHitTimes <= 0) continue;
            if (!directHits.Add(cell.WeakUid)) continue;
            AppendWeakHit(run, ctx, index);
        }

        if (targets.Count > 0 && anchorType != RemoveTypeNone)
        {
            // The anchor carries the effect position, so it is marked only when it is itself one of the
            // removed cells; callers pick a centre/anchor pair whose anchor is a real ordinary orb.
            for (int i = 0; i < targets.Count; i++)
            {
                if (targets[i].Index != preferredAnchor) continue;
                targets[i] = (preferredAnchor, anchorType);
                break;
            }
        }

        List<SameColorRemovalGroup> groups = PerOrbGroups(run, targets, scoreFactor);
        if (groups.Count > 0) EmitRemovals(run, ctx, groups);
        else ResolveSummons(run, ctx, new HashSet<int>());
    }

    private static void EmitEffectTargets(
        SameColorGameRun run, SameColorResponseContext ctx, List<int> indices, decimal scoreFactor)
    {
        List<(int Index, int ItemType)> targets = indices.Select(index => (index, RemoveTypeNone)).ToList();
        List<SameColorRemovalGroup> groups = PerOrbGroups(run, targets, scoreFactor);
        if (groups.Count > 0) EmitRemovals(run, ctx, groups);
    }

    internal static SameColorGamePropStat GetPropStat(SameColorGameRun run, int ballId, int round)
    {
        SameColorGamePropStat? stat = run.PropStats.FirstOrDefault(entry => entry.BallId == ballId && entry.Round == round);
        if (stat is null)
        {
            stat = new SameColorGamePropStat { BallId = ballId, Round = round };
            run.PropStats.Add(stat);
        }
        return stat;
    }

    #endregion

    #region Board flow

    /// <summary>
    /// Moves every orb toward larger Y and refills from the Y=0 edge. Wire evidence: after a
    /// three-orb removal in column 3, the surviving orb at (3,0) dropped to (3,3) and new orbs were
    /// created at (3,0..2), so orbs fall toward larger Y and spawn at the small-Y edge.
    ///
    /// AscNet policy: gravity is uniform - props and summons fall like ordinary orbs, which the
    /// client's own prop note expects ("a created prop ends below the orbs created with it"). That
    /// keeps the emptied cells contiguous from Y=0, which the client's refill animation requires: it
    /// derives each new orb's virtual origin as PositionY - ColumnAddCount - 1, so only a contiguous
    /// low-edge block produces the off-board start the drop animation expects.
    /// </summary>
    internal static void DropAndRefill(SameColorGameRun run, SameColorGameRoleTable role, SameColorResponseContext ctx)
    {
        List<SameColorGameDropRecord> drops = new();
        List<SameColorGameBallRecord> created = new();
        for (int x = 0; x < run.Cols; x++)
        {
            int write = run.Rows - 1;
            for (int y = run.Rows - 1; y >= 0; y--)
            {
                int index = Index(run, x, y);
                SameColorGameCell cell = run.Board[index];
                if (cell.ItemId == 0) continue;
                if (y != write)
                {
                    int destination = Index(run, x, write);
                    drops.Add(new SameColorGameDropRecord
                    {
                        ItemId = cell.ItemId,
                        StartPositionX = x,
                        StartPositionY = y,
                        EndPositionX = x,
                        EndPositionY = write
                    });
                    run.Board[destination] = cell;
                    run.Board[index] = new SameColorGameCell();
                }
                write--;
            }
            // After compaction the empty cells are exactly Y=0..write, so the new orbs land in one
            // contiguous block at the spawn edge and every virtual origin stays off-board.
            for (int y = write; y >= 0; y--)
            {
                int index = Index(run, x, y);
                if (run.Board[index].ItemId != 0) continue;
                int ballId = run.DropColorLeft > 0 && run.DropColor != 0
                    ? BasicBallOfColour(role, run.DropColor)
                    : RollBall(run, role);
                run.Board[index] = NormalCell(ballId);
                created.Add(Record(run, index));
                if (run.DropColorLeft > 0)
                {
                    run.DropColorLeft--;
                    if (run.DropColorLeft == 0) RemoveBuffByUid(run, ctx, run.DropColorBuffUid);
                }
                else if (run.ExcludedColorLeft > 0)
                {
                    run.ExcludedColorLeft--;
                    if (run.ExcludedColorLeft == 0) RemoveBuffByUid(run, ctx, run.ExcludedColorBuffUid);
                }
            }
        }
        if (drops.Count > 0)
            ctx.Actions.Add(new SameColorGameAction { ActionType = SameColorGameActionType.ItemDrop, DropItemList = drops });
        if (created.Count > 0)
            ctx.Actions.Add(new SameColorGameAction { ActionType = SameColorGameActionType.ItemCreateNew, ItemList = created });
    }

    private static int BasicBallOfColour(SameColorGameRoleTable role, int colour) =>
        role.BallId.FirstOrDefault(ballId => BallColor(ballId) == colour, role.BallId.Count > 0 ? role.BallId[0] : 0);

    /// <summary>
    /// Resolves an effect that removed cells: refill first, then cascade matches until the board is
    /// stable and playable. Any removal outside the cascade loop must go through this, otherwise a
    /// response could publish a board with holes.
    /// </summary>
    internal static void ResolveBoard(SameColorGameRun run, SameColorGameRoleTable role, SameColorResponseContext ctx)
    {
        DropAndRefill(run, role, ctx);
        Cascade(run, role, ctx);
    }

    /// <summary>Resolves matches until the board is stable, then guarantees a playable board.</summary>
    internal static void Cascade(SameColorGameRun run, SameColorGameRoleTable role, SameColorResponseContext ctx)
    {
        for (int step = 0; step < MaxCascadeSteps; step++)
        {
            List<SameColorMatch> matches = FindMatches(run);
            if (matches.Count == 0) break;

            List<SameColorRemovalGroup> groups = matches
                .Select(match => new SameColorRemovalGroup
                {
                    BallId = match.BallId,
                    Indices = OrderedByBoard(match.Indices, run),
                    ItemTypes = match.Indices.Select(_ => RemoveTypeNone).ToList(),
                    Combo = 1
                })
                .ToList();
            EmitRemovals(run, ctx, groups);

            List<SameColorGameBallRecord> props = new();
            foreach (SameColorMatch match in matches)
            {
                if (match.PropType <= 0) continue;
                int propBall = PropBallFor(role, match.PropType);
                if (propBall == 0) continue;
                run.Board[match.PlaceIndex] = new SameColorGameCell { ItemId = propBall, BallType = 2 };
                props.Add(Record(run, match.PlaceIndex));
                GetPropStat(run, propBall, run.CurRound).GainCount++;
            }
            if (props.Count > 0)
                ctx.Actions.Add(new SameColorGameAction { ActionType = SameColorGameActionType.PropCreateNew, ItemList = props });

            DropAndRefill(run, role, ctx);
        }

        if (HasAnyMatch(run) || !HasLegalMove(run))
        {
            // Announced reshuffle: the player is told the board was redistributed.
            ShuffleBoard(run, role);
            ctx.Actions.Add(new SameColorGameAction
            {
                ActionType = SameColorGameActionType.MapShuffle,
                ItemList = BoardRecords(run)
            });
        }
    }

    internal static List<SameColorGameBallRecord> BoardRecords(SameColorGameRun run, bool attributed = false)
    {
        List<SameColorGameBallRecord> records = new();
        for (int y = run.Rows - 1; y >= 0; y--)
        for (int x = 0; x < run.Cols; x++)
        {
            int index = Index(run, x, y);
            if (CellAt(run, index) is not null) records.Add(Record(run, index));
        }
        if (attributed && records.Count > 0) records[0].ItemType = RemoveTypeBoomCenter;
        return records;
    }

    internal static bool HasLegalMove(SameColorGameRun run)
    {
        if (run.Board.Any(cell => cell.BallType == 2)) return true;
        int[] offsets = { 1, 0, 0, 1 };
        for (int index = 0; index < run.Board.Count; index++)
        {
            if (!IsNormal(CellAt(run, index))) continue;
            int x = index % run.Cols;
            int y = index / run.Cols;
            for (int direction = 0; direction < 2; direction++)
            {
                int otherX = x + offsets[direction * 2];
                int otherY = y + offsets[direction * 2 + 1];
                if (!InBounds(run, otherX, otherY)) continue;
                int other = Index(run, otherX, otherY);
                if (!IsNormal(CellAt(run, other))) continue;
                if (run.Board[index].ItemId == run.Board[other].ItemId) continue;
                (run.Board[index], run.Board[other]) = (run.Board[other], run.Board[index]);
                bool matched = HasAnyMatch(run);
                (run.Board[index], run.Board[other]) = (run.Board[other], run.Board[index]);
                if (matched) return true;
            }
        }
        return false;
    }

    #endregion

    #region Skill effects

    internal static int UseSkill(
        SameColorGameRun run, SameColorResponseContext ctx, SameColorGameSkillState skillState,
        SameColorGameUseSkillTarget? target)
    {
        if (!Skills.Value.TryGetValue(skillState.SkillId, out SameColorGameSkillTable? skill))
            return SameColorGameModule.SkillConfigError;
        SameColorGameRoleTable role = Roles.Value[run.RoleId];

        int cost = skill.EnergyCost.GetValueOrDefault();
        run.Energy -= cost;
        run.SkillCostEnergy += cost;
        ctx.Actions.Add(new SameColorGameAction
        {
            ActionType = SameColorGameActionType.EnergyChange,
            EnergyChange = -cost,
            EnergyChangeType = SameColorGameEnergyReason.UseSkill
        });
        skillState.LeftCd = SkillGroups.Value.TryGetValue(skillState.GroupId, out SameColorGameSkillGroupTable? group)
            ? group.Cd
            : 0;
        GetCounter(run.SkillUseCount, skillState.SkillId).Value++;
        ctx.Actions.Add(new SameColorGameAction
        {
            ActionType = SameColorGameActionType.SkillCooldownChange,
            SkillId = skillState.GroupId,
            LeftCd = skillState.LeftCd
        });

        switch (skill.Type)
        {
            case 112:
            {
                // Collapsing Realm: the next authored number of drops take the chosen colour.
                int colour = target is null ? 0 : BallColor(target.ItemId);
                if (colour == 0) return SameColorGameModule.SkillParamError;
                run.DropColor = colour;
                run.DropColorLeft = BuffChangeColorCount(skill.BuffIds, 10);
                run.DropColorBuffUid = ApplyBuff(run, ctx, BuffIdForColour(skill.BuffIds, colour), PlayerBuffOwner);
                return 0;
            }
            case 114:
            {
                // Stringless Cage: dissolve every orb of the chosen colour, then bar it from drops.
                int colour = target is null ? 0 : BallColor(target.ItemId);
                if (colour == 0) return SameColorGameModule.SkillParamError;
                List<int> targets = run.Board
                    .Select((cell, index) => (cell, index))
                    .Where(entry => IsNormal(entry.cell) && BallColor(entry.cell.ItemId) == colour)
                    .Select(entry => entry.index)
                    .ToList();
                run.ExcludedColor = colour;
                run.ExcludedColorLeft = BuffChangeColorCount(skill.BuffIds, 10);
                run.ExcludedColorBuffUid = skill.BuffIds.Count > 0
                    ? ApplyBuff(run, ctx, skill.BuffIds[0], PlayerBuffOwner)
                    : 0;
                ctx.AttributeNextRemoval = true;
                EmitEffectTargets(run, ctx, targets, ScoreFactorOf(skill));
                ResolveBoard(run, role, ctx);
                return 0;
            }
            case 113:
            {
                // Enlightened Soul: every prop on the board activates, in board order.
                List<int> props = Enumerable.Range(0, run.Board.Count)
                    .Where(index => IsProp(CellAt(run, index)))
                    .ToList();
                foreach (int index in props)
                {
                    int ballId = run.Board[index].ItemId;
                    ctx.AttributeNextRemoval = true;
                    ActivateProp(run, ctx, index, ballId);
                }
                ResolveBoard(run, role, ctx);
                return 0;
            }
            case 120:
            {
                // Sunken Finale: rearrange every orb on the board.
                ShuffleBoard(run, role);
                ctx.Actions.Add(new SameColorGameAction
                {
                    ActionType = SameColorGameActionType.NewMapShuffle,
                    ItemList = BoardRecords(run, attributed: true)
                });
                return 0;
            }
            case 117:
            {
                // Thorough Analysis: convert random orbs into the authored prop set.
                if (skill.SkillParams.Count == 0) return SameColorGameModule.SkillConfigError;
                List<int> candidates = RandomOrdinaryTargets(run, skill.SkillParams.Count);
                if (candidates.Count == 0) return SameColorGameModule.SkillEffectError;
                for (int i = 0; i < candidates.Count; i++)
                {
                    int ballId = skill.SkillParams[i % skill.SkillParams.Count];
                    run.Board[candidates[i]] = new SameColorGameCell { ItemId = ballId, BallType = 2 };
                    GetPropStat(run, ballId, run.CurRound).GainCount++;
                    ctx.Actions.Add(new SameColorGameAction
                    {
                        ActionType = SameColorGameActionType.ItemTransform,
                        // Attribution marker: the client reads the action's own balls to decide
                        // whether the in-use skill owns this step.
                        Destination = Record(run, candidates[i], RemoveTypeBoomCenter)
                    });
                }
                return 0;
            }
            case 115:
            {
                // Shining Piercer: the authored number of 3x3 lightning strikes.
                int strikes = Math.Max(1, skill.SkillParam.GetValueOrDefault(1));
                for (int strike = 0; strike < strikes; strike++)
                {
                    // AscNet policy: strike centres stay one cell inside the border and are drawn from
                    // the centres whose bottom-left cell is a real ordinary orb, because that cell is
                    // the anchor the client's (Col + n, Row - n) derivation expects. Geometry and
                    // anchor are therefore chosen together and never relabelled afterwards.
                    List<int> centres = new();
                    int fallbackCentre = Index(run, Math.Max(0, run.Cols / 2), Math.Max(0, run.Rows / 2));
                    int fallbackTargets = 0;
                    for (int candidateY = 1; candidateY < run.Rows - 1; candidateY++)
                    for (int candidateX = 1; candidateX < run.Cols - 1; candidateX++)
                    {
                        if (Anchorable(run, Index(run, candidateX - 1, candidateY + 1)))
                            centres.Add(Index(run, candidateX, candidateY));
                        // A strike always lands on something: when no centre carries a usable anchor,
                        // the centre covering the most removable cells is used instead.
                        int covered = 0;
                        for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (Anchorable(run, Index(run, candidateX + dx, candidateY + dy))) covered++;
                        }
                        if (covered > fallbackTargets)
                        {
                            fallbackTargets = covered;
                            fallbackCentre = Index(run, candidateX, candidateY);
                        }
                    }
                    int centre = centres.Count > 0 ? centres[RandomInt(run, centres.Count)] : fallbackCentre;
                    int x = centre % run.Cols;
                    int y = centre / run.Cols;
                    List<int> indices = new();
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (!InBounds(run, x + dx, y + dy)) continue;
                        indices.Add(Index(run, x + dx, y + dy));
                    }
                    // One remove action per strike, anchored at the 3x3's bottom-left cell, which is
                    // the cell the client's (Col + n, Row - n) derivation expects.
                    EmitAreaEffect(run, ctx, indices, Index(run, x - 1, y + 1), RemoveTypeVeraLightning, ScoreFactorOf(skill));
                    // Resolve the board before the next strike is aimed: a later strike must not target
                    // the hole the previous one left, which would lose its effect silently.
                    ResolveBoard(run, role, ctx);
                }
                return 0;
            }
            case 118:
            {
                // Celestial Edge: two horizontal and two vertical three-block waves. The client
                // renders one anchored removal as a horizontal+vertical PAIR from a single centre
                // (EffectPositionList[1] and [2] both receive that centre), so the authored four
                // waves are two paired actions, each anchored at the bottom-left cell of its bands'
                // intersection block - cleared geometry and anchor are chosen together.
                int width = Math.Max(1, skill.SkillParam.GetValueOrDefault(3));
                for (int pair = 0; pair < 2; pair++)
                {
                    List<(int Row, int Column)> bands = new();
                    for (int y = 0; y + width - 1 < run.Rows; y++)
                    for (int x = 0; x + width - 1 < run.Cols; x++)
                    {
                        if (Anchorable(run, Index(run, x, y + width - 1))) bands.Add((y, x));
                    }
                    (int Row, int Column) band = bands.Count > 0
                        ? bands[RandomInt(run, bands.Count)]
                        : (Math.Max(0, run.Rows / 2 - 1), Math.Max(0, run.Cols / 2 - 1));

                    List<int> indices = new();
                    for (int y = band.Row; y < band.Row + width; y++)
                    for (int x = 0; x < run.Cols; x++)
                        indices.Add(Index(run, x, y));
                    for (int x = band.Column; x < band.Column + width; x++)
                    for (int y = 0; y < run.Rows; y++)
                        indices.Add(Index(run, x, y));
                    EmitAreaEffect(
                        run, ctx, indices, Index(run, band.Column, band.Row + width - 1),
                        RemoveTypeAlisaWave, ScoreFactorOf(skill));
                    // Resolve before the second pair so both authored waves land on a real board.
                    ResolveBoard(run, role, ctx);
                }
                return 0;
            }
            default:
                return SameColorGameModule.SkillConfigError;
        }
    }

    private static int BuffChangeColorCount(List<int> buffIds, int fallback)
    {
        foreach (int buffId in buffIds)
            if (Buffs.Value.TryGetValue(buffId, out SameColorGameBuffTable? buff) && buff.ChangeColorCount is int count && count > 0)
                return count;
        return fallback;
    }

    private static int BuffIdForColour(List<int> buffIds, int colour)
    {
        foreach (int buffId in buffIds)
            if (Buffs.Value.TryGetValue(buffId, out SameColorGameBuffTable? buff) && buff.TargetColors.Contains(colour))
                return buffId;
        return 0;
    }

    internal static SameColorGameCounter GetCounter(List<SameColorGameCounter> counters, int key)
    {
        SameColorGameCounter? counter = counters.FirstOrDefault(entry => entry.Key == key);
        if (counter is null)
        {
            counter = new SameColorGameCounter { Key = key };
            counters.Add(counter);
        }
        return counter;
    }

    #endregion

    #region Buffs and passives

    /// <summary>Applies one authored buff. Returns its instance uid, or 0 when it was not applied.</summary>
    internal static int ApplyBuff(SameColorGameRun run, SameColorResponseContext ctx, int buffId, int ownerWeakUid)
    {
        if (buffId == 0 || !Buffs.Value.TryGetValue(buffId, out SameColorGameBuffTable? buff)) return 0;
        switch (buff.Type)
        {
            case 1 or 2 or 3 or 4 or 6 or 7 or 8 or 9 or 10 or 11:
                break;
            default:
                // Outside the authored current closure: the buff is not reported as active, so no
                // effect is ever presented without a matching engine rule.
                return 0;
        }

        SameColorGameBuffState state = new()
        {
            BuffId = buffId,
            Uid = run.NextBuffUid++,
            AppliedRound = run.CurRound,
            LeftTurns = buff.Type is 3 or 4 ? buff.Duration.GetValueOrDefault() : 0,
            RemainingCount = buff.ChangeColorCount.GetValueOrDefault(),
            OwnerWeakUid = ownerWeakUid
        };
        run.Buffs.Add(state);
        ctx.Actions.Add(new SameColorGameAction
        {
            ActionType = SameColorGameActionType.BuffAdd,
            BossId = ownerWeakUid == PlayerBuffOwner ? 0 : run.BossId,
            BuffId = buffId,
            BuffUid = state.Uid
        });

        if (buff.Type is 1 or 2)
        {
            int step = Math.Max(1, buff.Step.GetValueOrDefault(1));
            run.MaxRound = Math.Max(1, run.MaxRound + (buff.Type == 1 ? step : -step));
            ctx.Actions.Add(new SameColorGameAction
            {
                ActionType = buff.Type == 1 ? SameColorGameActionType.StepAdd : SameColorGameActionType.StepSub,
                Step = step
            });
        }
        return state.Uid;
    }

    private static void RemoveBuffByUid(SameColorGameRun run, SameColorResponseContext ctx, int uid)
    {
        SameColorGameBuffState? state = run.Buffs.FirstOrDefault(buff => buff.Uid == uid);
        if (state is not null) RemoveBuff(run, ctx, state);
    }

    internal static void RemoveBuff(SameColorGameRun run, SameColorResponseContext ctx, SameColorGameBuffState state)
    {
        run.Buffs.Remove(state);
        ctx.Actions.Add(new SameColorGameAction
        {
            ActionType = SameColorGameActionType.BuffRemove,
            BuffUid = state.Uid
        });
        if (state.Uid == run.DropColorBuffUid)
        {
            run.DropColorBuffUid = 0;
            run.DropColor = 0;
            run.DropColorLeft = 0;
        }
        if (state.Uid == run.ExcludedColorBuffUid)
        {
            run.ExcludedColorBuffUid = 0;
            run.ExcludedColor = 0;
            run.ExcludedColorLeft = 0;
        }
    }

    /// <summary>
    /// Every buff with an authored turn count advances by turn, including a summon's weak-owned debuff:
    /// BossSkill 661-663 author "reducing Red Orb DMG by 20% for 2 turns", and defeating the summon is
    /// the additional early cleanup, not the only expiry. Boss 7's variants author no duration
    /// ("permanently"), so their LeftTurns stays 0 and they are removed only by their death skills.
    /// </summary>
    internal static void TickBuffs(SameColorGameRun run, SameColorResponseContext ctx)
    {
        foreach (SameColorGameBuffState state in run.Buffs.Where(state => state.LeftTurns > 0).ToList())
        {
            // A buff applied during the turn being resolved keeps its full authored duration: the
            // turn it was created on is not counted against it.
            if (state.AppliedRound >= run.CurRound) continue;
            state.LeftTurns--;
            if (state.LeftTurns > 0) continue;
            RemoveBuff(run, ctx, state);
        }
    }

    /// <summary>Authored "ping one random orb after the combo" buff (type 7).</summary>
    internal static bool PostComboBuffRemovals(SameColorGameRun run, SameColorResponseContext ctx)
    {
        List<SameColorGameBuffState> states = run.Buffs
            .Where(state => Buffs.Value.TryGetValue(state.BuffId, out SameColorGameBuffTable? buff) && buff.Type == 7)
            .ToList();
        bool removed = false;
        foreach (SameColorGameBuffState state in states)
        {
            EmitEffectTargets(run, ctx, RandomOrdinaryTargets(run, 1), 1m);
            RemoveBuff(run, ctx, state);
            removed = true;
        }
        return removed;
    }

    /// <summary>
    /// Rolls the authored per-swap passives. Type 3 boosts a random orb colour, type 4 pings one
    /// extra orb, type 2 crits the move's pings. Passive rows without an engine type are already
    /// encoded in authored values (Qu prop ScoreFactor 1.5, Lamia basic-attack score 200, Echoing
    /// Twins refill weight) and are deliberately not applied twice.
    /// </summary>
    internal static void RollPassives(SameColorGameRun run, SameColorResponseContext ctx, SameColorGameRoleTable role)
    {
        if (role.PassiveSkillId is not int passiveId) return;
        if (!Passives.Value.TryGetValue(passiveId, out SameColorGamePassiveSkillTable? passive)) return;
        int rate = passive.TriggerRate.GetValueOrDefault();
        if (rate <= 0) return;

        switch (passive.Type)
        {
            case 3:
            {
                if (RandomInt(run, 100) >= rate) return;
                int buffId = passive.Params.Count > 0 ? passive.Params[0] : 0;
                if (!Buffs.Value.TryGetValue(buffId, out SameColorGameBuffTable? buff)) return;
                List<int> colours = run.Board
                    .Where(cell => cell.BallType == 1)
                    .Select(cell => BallColor(cell.ItemId))
                    .Distinct()
                    .OrderBy(colour => colour)
                    .ToList();
                if (colours.Count == 0) return;
                int buffUid = run.NextBuffUid++;
                ctx.PassiveColour = colours[RandomInt(run, colours.Count)];
                ctx.PassiveColourDelta = buff.DamagePercent.GetValueOrDefault() / PercentScale;
                ctx.PassiveScoreFactor += ctx.PassiveColourDelta;
                ctx.PassiveDamages.Add(new SameColorGamePassiveDamage
                {
                    BuffId = buffId,
                    BuffUid = buffUid,
                    TargetColor = ctx.PassiveColour
                });
                ctx.Actions.Add(new SameColorGameAction
                {
                    ActionType = SameColorGameActionType.BuffAdd,
                    BossId = 0,
                    BuffId = buffId,
                    BuffUid = buffUid
                });
                return;
            }
            case 4:
            {
                if (RandomInt(run, 100) >= rate) return;
                int buffId = passive.Params.Count > 0 ? passive.Params[0] : 0;
                ApplyBuff(run, ctx, buffId, PlayerBuffOwner);
                return;
            }
            case 2:
            {
                if (RandomInt(run, 100) >= rate) return;
                int factor = passive.Params.Count > 0 ? passive.Params[0] : 0;
                if (factor <= 1) return;
                ctx.CriticalMove = true;
                ctx.PassiveScoreFactor += factor - 1;
                return;
            }
            default:
                return;
        }
    }

    /// <summary>Removes the one-move passive buff reported through PassiveSkillMoreDamages.</summary>
    internal static void ClosePassiveBuff(SameColorGameRun run, SameColorResponseContext ctx)
    {
        foreach (SameColorGamePassiveDamage damage in ctx.PassiveDamages)
        {
            ctx.Actions.Add(new SameColorGameAction
            {
                ActionType = SameColorGameActionType.BuffRemove,
                BuffUid = damage.BuffUid
            });
        }
    }

    #endregion

    #region Boss skills

    internal static void CheckBossSkill(SameColorGameRun run, SameColorResponseContext ctx, SameColorGameBossTable boss)
    {
        foreach (int bossSkillId in boss.ShowSkillIds)
        {
            if (!BossSkills.Value.TryGetValue(bossSkillId, out SameColorGameBossSkillTable? bossSkill)) continue;
            if (bossSkill.TriggerRound != run.CurRound) continue;

            if (bossSkill.SkipDamage is int skipDamage && skipDamage > 0 && ctx.MoveDamage >= skipDamage)
            {
                ctx.Actions.Add(new SameColorGameAction
                {
                    ActionType = SameColorGameActionType.BossSkipSkill,
                    BossSkillId = bossSkillId
                });
                continue;
            }

            ctx.Actions.Add(new SameColorGameAction
            {
                ActionType = SameColorGameActionType.BossReleaseSkill,
                BossSkillId = bossSkillId
            });
            foreach (int buffId in bossSkill.BuffId)
                ApplyBuff(run, ctx, buffId, PlayerBuffOwner);

            if (bossSkill.SubEnergyCount is int subEnergy && subEnergy > 0)
            {
                int delta = -Math.Min(run.Energy, subEnergy);
                if (delta != 0)
                {
                    run.Energy += delta;
                    run.BossCostEnergy += -delta;
                    ctx.Actions.Add(new SameColorGameAction
                    {
                        ActionType = SameColorGameActionType.EnergyChange,
                        EnergyChange = delta,
                        EnergyChangeType = SameColorGameEnergyReason.Boss
                    });
                }
            }
            SpawnSummons(run, ctx, bossSkill);
        }
    }

    internal static void SpawnSummons(SameColorGameRun run, SameColorResponseContext ctx, SameColorGameBossSkillTable bossSkill)
    {
        foreach (int ballId in bossSkill.WeakBallIds)
        {
            if (!Balls.Value.TryGetValue(ballId, out SameColorGameBallTable? ball)) continue;
            List<int> candidates = run.Board
                .Select((cell, index) => (cell, index))
                .Where(entry => IsNormal(entry.cell))
                .Select(entry => entry.index)
                .ToList();
            if (candidates.Count == 0) continue;

            int index = candidates[RandomInt(run, candidates.Count)];
            int weakUid = run.NextWeakUid++;
            run.Board[index] = new SameColorGameCell
            {
                ItemId = ballId,
                BallType = 3,
                WeakUid = weakUid,
                WeakHitTimes = BallWeakHitTimes(ballId)
            };
            run.WeakInfos.Add(new SameColorGameWeakInfo
            {
                Uid = weakUid,
                ItemId = ballId,
                CreateRound = run.CurRound,
                UpdateRounds = new List<int>()
            });
            ctx.Actions.Add(new SameColorGameAction
            {
                ActionType = SameColorGameActionType.ItemTransform,
                Destination = Record(run, index)
            });
            foreach (int buffId in ball.WeakBuffIds)
                ApplyBuff(run, ctx, buffId, weakUid);
        }
    }

    #endregion

    #region Settlement

    internal static long MoveScore(SameColorResponseContext ctx) =>
        ctx.RawScore <= 0m ? 0 : (long)decimal.Truncate(ctx.RawScore * ComboPercent(ctx.Combo) / PercentScale);

    /// <summary>
    /// Settles the current move. The client pre-reads score/energy from the response, so Run stats
    /// and cumulative fields are only included in the terminal settlement, matching the observed
    /// action stream.
    /// </summary>
    internal static void AppendSettlement(
        SameColorGameRun run, SameColorResponseContext ctx, bool isLastRound, bool fullRunStats, int? round = null)
    {
        int reportedRound = round ?? run.CurRound;
        long current = MoveScore(ctx);
        run.Score += current;
        run.TotalGameCombo += ctx.Combo;
        if (ctx.Combo > run.MaxCombo) run.MaxCombo = ctx.Combo;
        ctx.MoveDamage = current;

        ctx.Actions.Add(new SameColorGameAction
        {
            ActionType = SameColorGameActionType.SettleScore,
            CurRound = reportedRound,
            CurrentBossId = run.BossId,
            CurrentScore = current,
            TotalScore = run.Score,
            TotalCombo = ctx.Combo,
            TotalGameCombo = (int)Math.Min(int.MaxValue, run.TotalGameCombo),
            CurrentBallCount = ctx.RemovedCount,
            IsLastRound = isLastRound ? 1 : 0,
            ComboRecord = ctx.ComboByBall
                .OrderBy(entry => entry.Key)
                .Select(entry => new SameColorGameComboRecord { ItemId = entry.Key, Combo = entry.Value })
                .ToList(),
            ColorRemoveCount = ctx.ColourRemoveCount.ToDictionary(entry => entry.Key, entry => entry.Value),
            SkillUseCount = fullRunStats ? CountersToMap(run.SkillUseCount) : new Dictionary<int, int>(),
            PropInfoDict = fullRunStats ? PropStatsToMap(run) : new Dictionary<int, Dictionary<int, SameColorGamePropStatRecord>>(),
            WeakInfoDict = fullRunStats ? WeakInfosToMap(run) : new Dictionary<int, SameColorGameWeakInfoRecord>(),
            ObtainEnergy = fullRunStats ? run.ObtainEnergy : 0,
            SkillCostEnergy = fullRunStats ? -run.SkillCostEnergy : 0,
            BossCostEnergy = fullRunStats ? -run.BossCostEnergy : 0,
            GameStartTime = fullRunStats ? run.StartUnix : 0,
            LeftTime = 0,
            CurTimeline = reportedRound,
            PassiveSkillMoreDamages = ctx.PassiveDamages,
            PassiveSkillMoreScoreFactor = (double)ctx.PassiveScoreFactor
        });
    }

    internal static Dictionary<int, int> CountersToMap(List<SameColorGameCounter> counters) =>
        counters.Where(counter => counter.Value != 0).ToDictionary(counter => counter.Key, counter => counter.Value);

    internal static Dictionary<int, Dictionary<int, SameColorGamePropStatRecord>> PropStatsToMap(SameColorGameRun run) =>
        run.PropStats
            .GroupBy(stat => stat.BallId)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(
                    stat => stat.Round,
                    stat => new SameColorGamePropStatRecord { GainCount = stat.GainCount, UseCount = stat.UseCount }));

    internal static Dictionary<int, SameColorGameWeakInfoRecord> WeakInfosToMap(SameColorGameRun run) =>
        run.WeakInfos.ToDictionary(
            info => info.Uid,
            info => new SameColorGameWeakInfoRecord
            {
                ItemId = info.ItemId,
                CreateRound = info.CreateRound,
                UpdateRound = new List<int>(info.UpdateRounds)
            });

    internal static void AppendEnergy(SameColorGameRun run, SameColorResponseContext ctx, SameColorGameRoleTable role)
    {
        int gain = ctx.Combo * role.ComboAddEnergy;
        if (gain <= 0) return;
        int limit = role.EnergyLimit;
        int actual = limit > 0 ? Math.Min(gain, Math.Max(0, limit - run.Energy)) : gain;
        if (actual <= 0) return;
        run.Energy += actual;
        run.ObtainEnergy += actual;
        ctx.Actions.Add(new SameColorGameAction
        {
            ActionType = SameColorGameActionType.EnergyChange,
            EnergyChange = actual,
            EnergyChangeType = SameColorGameEnergyReason.Combo
        });
    }

    /// <summary>Authored ambient per-round energy (AddEnergyStartRound/RoundAddEnergyType/Count).</summary>
    internal static void AppendRoundEnergy(SameColorGameRun run, SameColorResponseContext ctx, SameColorGameRoleTable role)
    {
        int rows = Math.Min(role.AddEnergyStartRound.Count, Math.Min(role.RoundAddEnergyType.Count, role.RoundAddEnergyCount.Count));
        for (int i = 0; i < rows; i++)
        {
            if (role.RoundAddEnergyCount[i] == 0) continue;
            if (run.CurRound < role.AddEnergyStartRound[i]) continue;
            int limit = role.EnergyLimit;
            int actual = limit > 0
                ? Math.Min(role.RoundAddEnergyCount[i], Math.Max(0, limit - run.Energy))
                : role.RoundAddEnergyCount[i];
            if (actual == 0) continue;
            run.Energy += actual;
            run.ObtainEnergy += actual;
            ctx.Actions.Add(new SameColorGameAction
            {
                ActionType = SameColorGameActionType.EnergyChange,
                EnergyChange = actual,
                EnergyChangeType = SameColorGameEnergyReason.Round
            });
        }
    }

    /// <summary>
    /// Starts an accepted move: every running cooldown advances one turn and publishes its new value
    /// before the swap, which is the ordering the observed action stream shows (round 6 opens with the
    /// skill's remaining cooldown, then the swap).
    /// </summary>
    internal static void BeginTurn(SameColorGameRun run, SameColorResponseContext ctx)
    {
        foreach (SameColorGameSkillState skill in run.Skills.Where(skill => skill.LeftCd > 0).ToList())
        {
            skill.LeftCd--;
            ctx.Actions.Add(new SameColorGameAction
            {
                ActionType = SameColorGameActionType.SkillCooldownChange,
                SkillId = skill.GroupId,
                LeftCd = skill.LeftCd
            });
        }
    }

    /// <summary>Ends a played turn: buff durations advance and the authored ambient energy is granted.</summary>
    internal static void TickTurn(SameColorGameRun run, SameColorResponseContext ctx, SameColorGameRoleTable role)
    {
        TickBuffs(run, ctx);
        AppendRoundEnergy(run, ctx, role);
    }

    #endregion
}
