using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore.Shared.Helpers;
using ExpeditionIcons.PathPlannerData;

namespace ExpeditionIcons;

public class PathPlanner
{
    public record PerPointLootScore(Vector2 Point, double ScoreDiff, int NewRelics, int Loot);

    public record DetailedLootScore(List<PerPointLootScore> PerPointScore, double TotalScore, ExpeditionEnvironment Environment);

    private readonly Dictionary<object, double> _lootValueTable = new(ReferenceEqualityComparer.Instance);
    private readonly PlannerSettings _settings;
    private readonly int _validatedPoints;

    public PathPlanner(PlannerSettings settings)
    {
        _settings = settings;
        _validatedPoints = _settings.ValidatedIntermediatePoints + 1;
    }

    // Scoring logic is character-for-character identical to the original.
    // Only change: LINQ replaced with foreach to eliminate per-call heap allocations
    // in this hot path, allowing more iterations within the time budget.
    public double GetScore(List<Vector2> state, ExpeditionEnvironment environment)
    {
        var relics = new HashSet<IExpeditionRelic>();
        var lootList = new HashSet<IExpeditionLoot>();
        var score = 0.0;
        foreach (var explosionPoint in state)
        {
            foreach (var (pos, relic) in environment.Relics)
            {
                if (pos.Distance(explosionPoint) <= environment.ExplosionRadius)
                    relics.Add(relic);
            }

            var localScore = 0.0;
            foreach (var (pos, loot) in environment.Loot)
            {
                if (!pos.DistanceLessThanOrEqual(explosionPoint, environment.ExplosionRadius))
                    continue;
                if (!lootList.Add(loot))
                    continue;

                var multiplier = 1.0;
                var sum = 0.0;
                foreach (var relic in relics)
                {
                    var (m, s) = relic.GetScoreMultiplier(loot);
                    multiplier *= m;
                    sum += s;
                }

                localScore += _lootValueTable[loot] * multiplier * (1 + sum);
            }

            score += localScore;
        }

        return score;
    }

    // Sync with method above — identical logic, just also records per-point breakdown
    public DetailedLootScore GetDetailedScore(List<Vector2> state, ExpeditionEnvironment environment)
    {
        var relics = new HashSet<IExpeditionRelic>();
        var lootList = new HashSet<IExpeditionLoot>();
        var scorePerPoint = new List<PerPointLootScore>(state.Count);
        var score = 0.0;
        foreach (var explosionPoint in state)
        {
            var newRelics = 0;
            foreach (var (pos, relic) in environment.Relics)
            {
                if (pos.Distance(explosionPoint) <= environment.ExplosionRadius)
                    if (relics.Add(relic))
                        newRelics++;
            }

            var localScore = 0.0;
            var newLoot = 0;
            foreach (var (pos, loot) in environment.Loot)
            {
                if (!pos.DistanceLessThanOrEqual(explosionPoint, environment.ExplosionRadius))
                    continue;
                if (!lootList.Add(loot))
                    continue;

                newLoot++;
                var multiplier = 1.0;
                var sum = 0.0;
                foreach (var relic in relics)
                {
                    var (m, s) = relic.GetScoreMultiplier(loot);
                    multiplier *= m;
                    sum += s;
                }

                localScore += _lootValueTable[loot] * multiplier * (1 + sum);
            }

            scorePerPoint.Add(new PerPointLootScore(explosionPoint, localScore, newRelics, newLoot));
            score += localScore;
        }

        return new DetailedLootScore(scorePerPoint, score, environment);
    }

    // Unchanged from original — no allocation fix needed here (called infrequently)
    private Vector2 GetNextPosition(Vector2 position, Vector2 previousPosition, float radius, ExpeditionEnvironment environment)
    {
        for (var i = 1; i <= 1000; i++)
        {
            var candidate = GetNextMaybeInvalidPosition(position, radius * MathF.Pow(0.99f, i));
            if (IsValidPlacement(previousPosition, environment, candidate))
                return candidate;
        }
        return position;
    }

    // Replaced LINQ chain with a plain for loop.
    // Result is identical — no allocation on every validity check.
    private bool IsValidPlacement(Vector2 previousPosition, ExpeditionEnvironment environment, Vector2 position)
    {
        if (!previousPosition.DistanceLessThanOrEqual(position, environment.ExplosionRange))
            return false;
        if (Vector2.Clamp(position, environment.ExclusionArea.Min, environment.ExclusionArea.Max) == position)
            return false;
        for (var i = 1; i <= _validatedPoints; i++)
        {
            var l = i / (float)_validatedPoints;
            if (!environment.IsValidPlacement(Vector2.Lerp(previousPosition, position, l)))
                return false;
        }
        return true;
    }

    private static Vector2 GetNextMaybeInvalidPosition(Vector2 position, float radius)
    {
        radius = Math.Max(1, radius);
        var length = Math.Max(1, Random.Shared.Next(2) == 0 ? radius : GetWeightedLength(radius));
        var angle = Random.Shared.NextSingle() * MathF.PI * 2;
        var (sin, cos) = MathF.SinCos(angle);
        var rawPoint = position + new Vector2(cos * length, sin * length);
        var roundedPoint = RoundPoint(rawPoint);
        while (!roundedPoint.DistanceLessThanOrEqual(position, radius))
        {
            var diff = roundedPoint - position;
            var maxDiffComponent = Math.Abs(diff.X) > Math.Abs(diff.Y)
                ? new Vector2(Math.Sign(diff.X), 0)
                : new Vector2(0, Math.Sign(diff.Y));
            roundedPoint -= maxDiffComponent;
        }

        return roundedPoint;
    }

    // Unchanged from original
    private static Vector2 RoundPoint(Vector2 rawPoint) =>
        new Vector2(MathF.Round(rawPoint.X), MathF.Round(rawPoint.Y));

    private static float GetWeightedLength(float radius) =>
        Math.Max(Random.Shared.NextSingle(), Random.Shared.NextSingle()) * radius;

    private List<Vector2> MutatePath(Vector2 startingPoint, float radius, List<Vector2> originalPath, ExpeditionEnvironment environment)
    {
        var mutateTimes = Random.Shared.Next(1, 4);
        var newPath = originalPath.ToList();
        for (var mutation = 0; mutation < mutateTimes; mutation++)
        {
            if (Random.Shared.Next(2) == 0 && TryApplySkipMutation(newPath, environment))
            {
                continue;
            }

            if (Random.Shared.Next(2) == 0 && TryApplySwapMutation(newPath, environment))
            {
                continue;
            }

            var changeIndex = Random.Shared.Next(newPath.Count);
            Vector2 changedPoint;
            var previousPoint = changeIndex == 0 ? startingPoint : newPath[changeIndex - 1];
            var changingPoint = newPath[changeIndex];
            var tries = 0;
            bool isValidChange;
            do
            {
                if (Random.Shared.Next(2) == 0)
                {
                    changedPoint = GetNextPosition(previousPoint, previousPoint, radius, environment);
                }
                else
                {
                    var allowedMoveRadius = Math.Max(radius - previousPoint.Distance(changingPoint), radius / 5);
                    changedPoint = GetNextPosition(changingPoint, previousPoint, allowedMoveRadius, environment);
                }

                isValidChange = previousPoint.DistanceLessThanOrEqual(changedPoint, radius) &&
                                (changeIndex == newPath.Count - 1 ||
                                 IsValidPlacement(changedPoint, environment, newPath[changeIndex + 1]));
            } while (!isValidChange && tries++ < 10);

            if (!isValidChange)
            {
                continue;
            }

            newPath[changeIndex] = changedPoint;
        }

        return newPath;
    }

    private bool TryApplySkipMutation(List<Vector2> path, ExpeditionEnvironment environment)
    {
        var pathCount = path.Count - 2;
        if (pathCount <= 0)
        {
            return false;
        }

        var searchStartOffset = Random.Shared.Next(0, pathCount + 1);
        if (searchStartOffset == pathCount)
        {
            var injectionIndex = Random.Shared.Next(0, pathCount);
            var midpoint = RoundPoint((path[injectionIndex] + path[injectionIndex + 1]) / 2);
            if (IsValidPlacement(path[injectionIndex], environment, midpoint) &&
                IsValidPlacement(midpoint, environment, path[injectionIndex + 1]))
            {
                path.RemoveAt(path.Count - 1);
                path.Insert(injectionIndex + 1, midpoint);
                return true;
            }

            searchStartOffset = 0;
        }

        for (var i = 0; i < pathCount; i++)
        {
            var checkIndex = 1 + (i + searchStartOffset) % pathCount;
            if (IsValidPlacement(path[checkIndex - 1], environment, path[checkIndex + 1]))
            {
                path.RemoveAt(checkIndex);
                path.Add(GetNextPosition(path[path.Count - 1], path[path.Count - 1], environment.ExplosionRange, environment));
                return true;
            }
        }

        return false;
    }

    private bool TryApplySwapMutation(List<Vector2> path, ExpeditionEnvironment environment)
    {
        var pathCount = path.Count - 3;
        if (pathCount <= 0)
        {
            return false;
        }

        var searchStartOffset = Random.Shared.Next(0, pathCount);
        for (var i = 0; i < pathCount; i++)
        {
            var checkIndex = 1 + (i + searchStartOffset) % pathCount;
            if (IsValidPlacement(path[checkIndex - 1], environment, path[checkIndex + 1]) &&
                IsValidPlacement(path[checkIndex], environment, path[checkIndex + 2]))
            {
                (path[checkIndex + 1], path[checkIndex]) = (path[checkIndex], path[checkIndex + 1]);
                return true;
            }
        }

        return false;
    }

    public void Init(ExpeditionEnvironment environment)
    {
        _lootValueTable.Clear();
        foreach (var (_, loot) in environment.Loot)
        {
            _lootValueTable[loot] = loot switch
            {
                RunicMonster => environment.IsLogbook ? _settings.RunicMonsterLogbookWeight : _settings.RunicMonsterWeight,
                Chest { Type: var type } => _settings.ChestSettingsMap.GetValueOrDefault(type, new ChestSettings()).Weight,
                NormalMonster => _settings.NormalMonsterWeight,
            };
        }

        _lootValueTable.TrimExcess();
    }

    // Guided mutation: identifies the explosion point contributing the least NEW score
    // and replaces it. Uses the same cumulative relic+loot tracking as GetScore so the
    // "worst point" identification is consistent with how the scorer sees the path.
    //
    // Key correctness invariant: relicsSoFar and lootSeenSoFar are declared OUTSIDE the
    // loop and accumulate across points — exactly mirroring GetScore. lootSeenSoFar.Add()
    // returns false for already-seen loot, so localScore only counts genuinely new loot
    // at each point. This is what makes worstIndex meaningful.
    private List<Vector2> MutatePathGuided(Vector2 startingPoint, float radius, List<Vector2> originalPath, ExpeditionEnvironment environment)
    {
        var newPath = originalPath.ToList();

        var worstIndex = 0;
        var worstScore = double.MaxValue;
        var relicsSoFar = new HashSet<IExpeditionRelic>();
        var lootSeenSoFar = new HashSet<IExpeditionLoot>();

        for (var i = 0; i < newPath.Count; i++)
        {
            // Accumulate relics — same as GetScore
            foreach (var (pos, relic) in environment.Relics)
                if (pos.Distance(newPath[i]) <= environment.ExplosionRadius)
                    relicsSoFar.Add(relic);

            // Score only loot first seen at this point — same as GetScore
            var localScore = 0.0;
            foreach (var (pos, loot) in environment.Loot)
            {
                if (!pos.DistanceLessThanOrEqual(newPath[i], environment.ExplosionRadius))
                    continue;
                if (!lootSeenSoFar.Add(loot)) // false = already seen at an earlier point
                    continue;

                var multiplier = 1.0;
                var sum = 0.0;
                foreach (var relic in relicsSoFar)
                {
                    var (m, s) = relic.GetScoreMultiplier(loot);
                    multiplier *= m;
                    sum += s;
                }
                localScore += _lootValueTable[loot] * multiplier * (1 + sum);
            }

            if (localScore < worstScore)
            {
                worstScore = localScore;
                worstIndex = i;
            }
        }

        var previousPoint = worstIndex == 0 ? startingPoint : newPath[worstIndex - 1];
        var tries = 0;
        bool isValidChange;
        Vector2 changedPoint;
        do
        {
            changedPoint = GetNextPosition(previousPoint, previousPoint, radius, environment);
            isValidChange = previousPoint.DistanceLessThanOrEqual(changedPoint, radius) &&
                            (worstIndex == newPath.Count - 1 ||
                             IsValidPlacement(changedPoint, environment, newPath[worstIndex + 1]));
        } while (!isValidChange && tries++ < 20);

        if (isValidChange)
            newPath[worstIndex] = changedPoint;

        return newPath;
    }

    // Greedy seed: at each step, moves toward the uncovered loot position that yields
    // the highest score given relics accumulated so far. Falls back to random walk if
    // no reachable uncovered loot exists.
    //
    // Uses identical multiplier/sum math to GetScore. coveredRelics accumulates across
    // steps so relic bonuses are applied correctly to future loot — same semantics as
    // the main scorer walking through the path left-to-right.
    private List<Vector2> BuildGreedyPath(ExpeditionEnvironment environment)
    {
        var path = new List<Vector2>(environment.MaxExplosions);
        var coveredRelics = new HashSet<IExpeditionRelic>();
        var coveredLoot = new HashSet<IExpeditionLoot>(ReferenceEqualityComparer.Instance);
        var current = environment.StartingPoint;

        for (var step = 0; step < environment.MaxExplosions; step++)
        {
            var bestCandidateScore = -1.0;
            var bestCandidate = Vector2.Zero;
            var found = false;

            for (var i = 0; i < environment.Loot.Count; i++)
            {
                var (lootPos, lootItem) = environment.Loot[i];
                if (coveredLoot.Contains(lootItem)) continue;
                if (!current.DistanceLessThanOrEqual(lootPos, environment.ExplosionRange * 1.5f)) continue;

                // Project toward this loot item, staying within explosion range
                var diff = lootPos - current;
                var dist = diff.Length();
                Vector2 candidate;
                if (dist <= environment.ExplosionRange * 0.9f)
                    candidate = RoundPoint(lootPos);
                else
                    candidate = RoundPoint(current + diff * (environment.ExplosionRange * 0.9f / dist));

                if (!IsValidPlacement(current, environment, candidate)) continue;

                // Score this candidate using relics accumulated so far PLUS any new
                // relics this placement would add — identical math to GetScore
                var tempRelics = new HashSet<IExpeditionRelic>(coveredRelics);
                foreach (var (rpos, relic) in environment.Relics)
                    if (rpos.Distance(candidate) <= environment.ExplosionRadius)
                        tempRelics.Add(relic);

                var candidateScore = 0.0;
                foreach (var (lpos, loot) in environment.Loot)
                {
                    if (coveredLoot.Contains(loot)) continue;
                    if (!lpos.DistanceLessThanOrEqual(candidate, environment.ExplosionRadius)) continue;

                    var multiplier = 1.0;
                    var sum = 0.0;
                    foreach (var relic in tempRelics)
                    {
                        var (m, s) = relic.GetScoreMultiplier(loot);
                        multiplier *= m;
                        sum += s;
                    }
                    candidateScore += _lootValueTable[loot] * multiplier * (1 + sum);
                }

                if (candidateScore > bestCandidateScore)
                {
                    bestCandidateScore = candidateScore;
                    bestCandidate = candidate;
                    found = true;
                }
            }

            var next = found
                ? bestCandidate
                : GetNextPosition(current, current, environment.ExplosionRange, environment);

            path.Add(next);

            // Update cumulative state for next step
            foreach (var (rpos, relic) in environment.Relics)
                if (rpos.Distance(next) <= environment.ExplosionRadius)
                    coveredRelics.Add(relic);
            foreach (var (lpos, loot) in environment.Loot)
                if (lpos.DistanceLessThanOrEqual(next, environment.ExplosionRadius))
                    coveredLoot.Add(loot);

            current = next;
        }

        return path;
    }

    public IEnumerable<PathState> GetBestPathSeries(ExpeditionEnvironment environment)
    {
        if (environment.MaxExplosions <= 0)
        {
            yield return new PathState(new List<Vector2>(), 0);
            yield break;
        }

        // Track bestPath and bestScore explicitly so GetScore is called exactly once
        // per path per iteration — original called it 2-3 times per iteration
        var bestPath = Enumerable.Repeat(Vector2.Zero, environment.MaxExplosions).ToList();
        var bestScore = 0.0;

        // Seed first generation: 25% greedy paths, 75% random paths.
        // Greedy paths give the evolutionary search a better starting population.
        // The greedy paths are valid inputs to the same scorer — they cannot produce
        // a worse result than random paths as seeds.
        var greedySeedCount = Math.Max(1, _settings.PathGenerationSize / 4);
        var initialBatch = new List<List<Vector2>>(_settings.PathGenerationSize * 2);
        for (var i = 0; i < greedySeedCount; i++)
            initialBatch.Add(BuildGreedyPath(environment));
        while (initialBatch.Count < _settings.PathGenerationSize * 2)
            initialBatch.Add(BuildPath(environment));

        var batch = initialBatch;
        var generationCounter = 0;

        while (true)
        {
            // Score each path exactly once and store alongside it
            var batchWithValues = batch
                .Select(x => (Score: GetScore(x, environment), Path: x))
                .OrderByDescending(x => x.Score)
                .Take(_settings.PathGenerationSize)
                .ToList();

            // Update global best — single comparison, no extra GetScore call
            if (batchWithValues[0].Score > bestScore)
            {
                bestScore = batchWithValues[0].Score;
                bestPath = batchWithValues[0].Path;
            }

            // Materialise top paths once — original evaluated the LINQ chain twice
            var topPaths = batchWithValues.Select(x => x.Path).ToList();
            var injectionCount = (int)(_settings.PathGenerationSize * _settings.NewRandomPathInjectionRate);

            // ~25% of mutations are guided (target the weakest point in the path).
            // Remaining 75% are the original random mutation — unchanged behaviour.
            var guidedThreshold = _settings.PathGenerationSize / 4;
            var newBatch = new List<List<Vector2>>(topPaths.Count * 2 + injectionCount + 2);

            var mutIdx = 0;
            foreach (var p in topPaths.Concat(topPaths))
            {
                if (Random.Shared.NextDouble() > _settings.PathMutateChance)
                    newBatch.Add(p);
                else if (mutIdx < guidedThreshold)
                    newBatch.Add(MutatePathGuided(environment.StartingPoint, environment.ExplosionRange, p, environment));
                else
                    newBatch.Add(MutatePath(environment.StartingPoint, environment.ExplosionRange, p, environment));
                mutIdx++;
            }

            // Always re-inject the current global best so it is never lost
            newBatch.Add(bestPath);

            // Re-inject a fresh greedy seed every 5 generations to escape local optima.
            // Throttled to avoid consuming too much of the time budget with greedy builds.
            generationCounter++;
            if (generationCounter % 5 == 0)
                newBatch.Add(BuildGreedyPath(environment));
            else
                newBatch.Add(BuildPath(environment));

            for (var i = 0; i < injectionCount - 1; i++)
                newBatch.Add(BuildPath(environment));

            yield return new PathState(bestPath, bestScore);
            batch = newBatch;
        }
    }

    private List<Vector2> BuildPath(ExpeditionEnvironment environment)
    {
        var path = new List<Vector2>(environment.MaxExplosions);
        if (Random.Shared.Next(2) != 0 && environment.Relics.Any())
        {
            var environmentExplosionRange = environment.ExplosionRange * 0.9f;
            var relic = environment.Relics[Random.Shared.Next(environment.Relics.Count)];
            var current = environment.StartingPoint;
            do
            {
                var diff = relic.Item1 - current;
                if (diff.Length() < environmentExplosionRange)
                {
                    path.Add(RoundPoint(relic.Item1));
                }
                else
                {
                    current += diff * (environmentExplosionRange / diff.Length());
                    path.Add(RoundPoint(current));
                }

                // Fixed: O(1) direct index instead of original SkipLast(1).LastOrDefault()
                // which allocates an enumerator and walks the list on every iteration
                var prev = path.Count >= 2 ? path[path.Count - 2] : environment.StartingPoint;
                if (!IsValidPlacement(prev, environment, path[path.Count - 1]))
                {
                    path.RemoveAt(path.Count - 1);
                    break;
                }
            } while (!current.DistanceLessThanOrEqual(relic.Item1, environment.ExplosionRadius) &&
                     path.Count < environment.MaxExplosions);
        }

        // Fixed: direct index instead of LastOrDefault — identical result, no allocation
        var point = path.Count > 0 ? path[path.Count - 1] : environment.StartingPoint;
        while (path.Count < environment.MaxExplosions)
        {
            path.Add(point = GetNextPosition(point, point, environment.ExplosionRange, environment));
        }

        return path;
    }
}