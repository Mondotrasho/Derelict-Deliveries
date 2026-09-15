using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Changes cloud material while keeping the high-resolution mask connected and as
/// close as physically possible to its requested target mass.
/// </summary>
public sealed class CloudSolver
{
    private static readonly Vector2Int[] CardinalDirections =
    {
        new Vector2Int(1, 0),
        new Vector2Int(-1, 0),
        new Vector2Int(0, 1),
        new Vector2Int(0, -1)
    };

    private static readonly Vector2Int[] EightDirections =
    {
        new Vector2Int(1, 0),
        new Vector2Int(-1, 0),
        new Vector2Int(0, 1),
        new Vector2Int(0, -1),
        new Vector2Int(1, 1),
        new Vector2Int(1, -1),
        new Vector2Int(-1, 1),
        new Vector2Int(-1, -1)
    };

    private readonly CloudMass mass;
    private readonly CloudNoiseField field;
    private readonly float cohesion;

    public CloudSolver(CloudMass mass, CloudNoiseField field, float cohesion)
    {
        this.mass = mass ?? throw new ArgumentNullException(nameof(mass));
        this.field = field ?? throw new ArgumentNullException(nameof(field));
        this.cohesion = Mathf.Max(0f, cohesion);

        if (mass.Width != field.Width || mass.Height != field.Height)
            throw new ArgumentException("Cloud mass and noise field dimensions must match.");
    }

    /// <summary>
    /// Creates the thresholded starting mask, connects it, reaches the exact requested
    /// mass when possible, fills enclosed holes, then performs shape relaxation.
    /// </summary>
    public void GenerateInitialMask(float thresholdBias, bool fillEnclosedHoles, int relaxationPasses)
    {
        mass.ClearCloud();

        int desired = Mathf.Min(mass.TargetPixelCount, mass.AvailableCapacity);
        if (desired <= 0)
            return;

        float threshold = FindThresholdForDesiredCount(desired);
        threshold = Mathf.Clamp01(threshold + thresholdBias);

        for (int y = 0; y < mass.Height; y++)
        {
            for (int x = 0; x < mass.Width; x++)
            {
                if (!mass.IsBlocked(x, y) && field.GetValue(x, y) >= threshold)
                    mass.SetCloud(x, y, true);
            }
        }

        // A very aggressive bias could threshold everything away. Seed the strongest
        // available pixel so growth always has a valid connected starting point.
        if (mass.CountCloudPixels() == 0)
            AddStrongestAvailablePixel();

        ConnectComponents();
        MatchTargetMass(null, 0f);

        if (fillEnclosedHoles)
        {
            FillEnclosedHoles();
            MatchTargetMass(null, 0f);
        }

        RelaxSurface(Mathf.Max(0, relaxationPasses), null, 0f);
        MatchTargetMass(null, 0f);
    }

    /// <summary>
    /// Reforms the remaining cloud after material has been removed and blocked.
    /// The original target mass is preserved whenever enough connected space remains.
    /// </summary>
    public void RepairAfterDamage(RectInt damageArea, int reflowIterations, float localRepairBias)
    {
        if (mass.CountCloudPixels() == 0)
        {
            AddStrongestAvailablePixelNear(damageArea, localRepairBias);
        }

        ConnectComponents();
        MatchTargetMass(damageArea, localRepairBias);
        RelaxSurface(Mathf.Max(0, reflowIterations), damageArea, localRepairBias);
        MatchTargetMass(damageArea, localRepairBias);
    }

    /// <summary>
    /// Uses repeated noise-weighted shortest paths to join every reachable component
    /// into one connected cloud mass.
    /// </summary>
    public bool ConnectComponents()
    {
        int safety = 0;

        while (safety++ < 1024)
        {
            ComponentMap components = BuildComponentMap();

            if (components.ComponentCount <= 1)
                return true;

            int mainId = components.GetLargestComponentId();
            if (mainId < 0)
                return true;

            if (!ConnectMainComponentToNearest(components, mainId))
                return false;
        }

        return false;
    }

    public void GrowToTargetMass(RectInt? damageArea, float localRepairBias)
    {
        int goal = Mathf.Min(mass.TargetPixelCount, mass.AvailableCapacity);
        int current = mass.CountCloudPixels();

        if (current >= goal)
            return;

        BinaryHeap frontier = new BinaryHeap(isMinHeap: false);

        for (int y = 0; y < mass.Height; y++)
        {
            for (int x = 0; x < mass.Width; x++)
            {
                if (IsGrowthBoundary(x, y))
                    frontier.Push(IndexOf(x, y), ScoreGrowthCandidate(x, y, damageArea, localRepairBias));
            }
        }

        int safety = mass.Width * mass.Height * 16;

        while (current < goal && frontier.Count > 0 && safety-- > 0)
        {
            HeapNode node = frontier.Pop();
            FromIndex(node.Index, out int x, out int y);

            if (!IsGrowthBoundary(x, y))
                continue;

            float updatedScore = ScoreGrowthCandidate(x, y, damageArea, localRepairBias);

            // Neighbouring cloud may have changed after this candidate was queued.
            // Reinsert it with its current score when that change is meaningful.
            if (Mathf.Abs(updatedScore - node.Priority) > 0.01f)
            {
                frontier.Push(node.Index, updatedScore);
                continue;
            }

            mass.SetCloud(x, y, true);
            current++;

            for (int i = 0; i < CardinalDirections.Length; i++)
            {
                int nx = x + CardinalDirections[i].x;
                int ny = y + CardinalDirections[i].y;

                if (IsGrowthBoundary(nx, ny))
                    frontier.Push(IndexOf(nx, ny), ScoreGrowthCandidate(nx, ny, damageArea, localRepairBias));
            }
        }
    }

    public void RemoveToTargetMass()
    {
        int goal = Mathf.Min(mass.TargetPixelCount, mass.AvailableCapacity);
        int current = mass.CountCloudPixels();

        if (current <= goal)
            return;

        BinaryHeap frontier = BuildRemovalFrontier();
        int safety = mass.Width * mass.Height * 32;
        int stalledRebuilds = 0;

        while (current > goal && safety-- > 0)
        {
            if (frontier.Count == 0)
            {
                // A pixel which was an articulation point earlier may become safe after
                // the outer surface changes. Rebuild occasionally rather than giving up
                // on a still-solvable exact target.
                frontier = BuildRemovalFrontier();
                stalledRebuilds++;

                if (frontier.Count == 0 || stalledRebuilds > 4)
                    break;
            }

            HeapNode node = frontier.Pop();
            FromIndex(node.Index, out int x, out int y);

            if (!mass.HasCloud(x, y) || !IsCloudBoundary(x, y))
                continue;

            float updatedScore = ScoreOccupiedPixel(x, y);
            if (Mathf.Abs(updatedScore - node.Priority) > 0.01f)
            {
                frontier.Push(node.Index, updatedScore);
                continue;
            }

            if (!CanRemoveWithoutDisconnecting(x, y))
                continue;

            mass.SetCloud(x, y, false);
            current--;
            stalledRebuilds = 0;

            // Removing one pixel only changes the local surface directly, so refresh
            // nearby candidates instead of sorting the whole cloud again.
            for (int i = 0; i < EightDirections.Length; i++)
            {
                int nx = x + EightDirections[i].x;
                int ny = y + EightDirections[i].y;

                if (mass.HasCloud(nx, ny) && IsCloudBoundary(nx, ny))
                    frontier.Push(IndexOf(nx, ny), ScoreOccupiedPixel(nx, ny));
            }
        }
    }

    private BinaryHeap BuildRemovalFrontier()
    {
        BinaryHeap frontier = new BinaryHeap(isMinHeap: true);

        for (int y = 0; y < mass.Height; y++)
        {
            for (int x = 0; x < mass.Width; x++)
            {
                if (mass.HasCloud(x, y) && IsCloudBoundary(x, y))
                    frontier.Push(IndexOf(x, y), ScoreOccupiedPixel(x, y));
            }
        }

        return frontier;
    }

    /// <summary>
    /// Fills all enclosed, unblocked empty regions. Blocked pixels themselves remain
    /// empty and can never be filled.
    /// </summary>
    public int FillEnclosedHoles()
    {
        bool[,] outside = new bool[mass.Width, mass.Height];
        Queue<int> queue = new Queue<int>();

        void TryQueue(int x, int y)
        {
            if (!mass.IsInside(x, y) || outside[x, y] || mass.HasCloud(x, y))
                return;

            // Blocked cells are allowed to carry the outside flood. They still cannot
            // themselves be filled. This keeps a carved channel to the exterior open.
            outside[x, y] = true;
            queue.Enqueue(IndexOf(x, y));
        }

        for (int x = 0; x < mass.Width; x++)
        {
            TryQueue(x, 0);
            TryQueue(x, mass.Height - 1);
        }

        for (int y = 0; y < mass.Height; y++)
        {
            TryQueue(0, y);
            TryQueue(mass.Width - 1, y);
        }

        while (queue.Count > 0)
        {
            int index = queue.Dequeue();
            FromIndex(index, out int x, out int y);

            for (int i = 0; i < CardinalDirections.Length; i++)
                TryQueue(x + CardinalDirections[i].x, y + CardinalDirections[i].y);
        }

        int filled = 0;

        for (int y = 0; y < mass.Height; y++)
        {
            for (int x = 0; x < mass.Width; x++)
            {
                if (!outside[x, y] && !mass.HasCloud(x, y) && !mass.IsBlocked(x, y))
                {
                    mass.SetCloud(x, y, true);
                    filled++;
                }
            }
        }

        return filled;
    }

    /// <summary>
    /// Performs mass-conserving surface swaps. A weak occupied boundary pixel is
    /// removed only when connectivity is preserved, then a stronger boundary pixel
    /// is added elsewhere.
    /// </summary>
    public int RelaxSurface(int passes, RectInt? damageArea, float localRepairBias)
    {
        return RelaxSurfaceInternal(passes, damageArea, localRepairBias, 0.04f);
    }

    /// <summary>
    /// Moves a limited amount of cloud material toward the current animated scalar
    /// field. Each successful step removes exactly one boundary pixel and adds exactly
    /// one boundary pixel, so the cloud volume never changes.
    /// </summary>
    public int AnimateTowardsField(int maxSwaps, float minimumImprovement)
    {
        return RelaxSurfaceInternal(
            Mathf.Max(0, maxSwaps),
            null,
            0f,
            Mathf.Max(0f, minimumImprovement));
    }

    private int RelaxSurfaceInternal(
        int passes,
        RectInt? damageArea,
        float localRepairBias,
        float minimumImprovement)
    {
        int swaps = 0;

        for (int pass = 0; pass < passes; pass++)
        {
            ScoredPixel? weakest = FindWeakestRemovableBoundaryPixel();
            ScoredPixel? strongest = FindStrongestGrowthBoundaryPixel(damageArea, localRepairBias);

            if (!weakest.HasValue || !strongest.HasValue)
                break;

            if (strongest.Value.Score <= weakest.Value.Score + minimumImprovement)
                break;

            ScoredPixel remove = weakest.Value;
            mass.SetCloud(remove.X, remove.Y, false);

            ScoredPixel add = strongest.Value;
            if (!IsGrowthBoundary(add.X, add.Y))
            {
                // Removing the old pixel can occasionally change the selected growth
                // candidate's boundary status. Restore the pixel instead of losing mass.
                mass.SetCloud(remove.X, remove.Y, true);
                continue;
            }

            mass.SetCloud(add.X, add.Y, true);
            swaps++;
        }

        return swaps;
    }

    private void MatchTargetMass(RectInt? damageArea, float localRepairBias)
    {
        int current = mass.CountCloudPixels();
        int goal = Mathf.Min(mass.TargetPixelCount, mass.AvailableCapacity);

        if (current < goal)
            GrowToTargetMass(damageArea, localRepairBias);
        else if (current > goal)
            RemoveToTargetMass();
    }

    private float FindThresholdForDesiredCount(int desiredCount)
    {
        List<float> availableValues = new List<float>(mass.AvailableCapacity);

        for (int y = 0; y < mass.Height; y++)
        {
            for (int x = 0; x < mass.Width; x++)
            {
                if (!mass.IsBlocked(x, y))
                    availableValues.Add(field.GetValue(x, y));
            }
        }

        if (availableValues.Count == 0)
            return 1f;

        availableValues.Sort();

        int keep = Mathf.Clamp(desiredCount, 1, availableValues.Count);
        int thresholdIndex = Mathf.Clamp(availableValues.Count - keep, 0, availableValues.Count - 1);
        return availableValues[thresholdIndex];
    }

    private void AddStrongestAvailablePixel()
    {
        float best = float.NegativeInfinity;
        int bestX = -1;
        int bestY = -1;

        for (int y = 0; y < mass.Height; y++)
        {
            for (int x = 0; x < mass.Width; x++)
            {
                if (mass.IsBlocked(x, y))
                    continue;

                float score = field.GetValue(x, y);
                if (score > best)
                {
                    best = score;
                    bestX = x;
                    bestY = y;
                }
            }
        }

        if (bestX >= 0)
            mass.SetCloud(bestX, bestY, true);
    }

    private void AddStrongestAvailablePixelNear(RectInt damageArea, float localRepairBias)
    {
        float best = float.NegativeInfinity;
        int bestX = -1;
        int bestY = -1;

        for (int y = 0; y < mass.Height; y++)
        {
            for (int x = 0; x < mass.Width; x++)
            {
                if (mass.IsBlocked(x, y))
                    continue;

                float score = field.GetValue(x, y) + DamageProximity(x, y, damageArea) * localRepairBias;
                if (score > best)
                {
                    best = score;
                    bestX = x;
                    bestY = y;
                }
            }
        }

        if (bestX >= 0)
            mass.SetCloud(bestX, bestY, true);
    }

    private bool ConnectMainComponentToNearest(ComponentMap components, int mainId)
    {
        int total = mass.Width * mass.Height;
        float[] distances = new float[total];
        int[] previous = new int[total];
        bool[] closed = new bool[total];

        for (int i = 0; i < total; i++)
        {
            distances[i] = float.PositiveInfinity;
            previous[i] = -1;
        }

        BinaryHeap open = new BinaryHeap(isMinHeap: true);

        for (int y = 0; y < mass.Height; y++)
        {
            for (int x = 0; x < mass.Width; x++)
            {
                if (components.Labels[x, y] != mainId)
                    continue;

                int index = IndexOf(x, y);
                distances[index] = 0f;
                open.Push(index, 0f);
            }
        }

        int destination = -1;

        while (open.Count > 0)
        {
            HeapNode node = open.Pop();
            int current = node.Index;

            if (closed[current])
                continue;

            closed[current] = true;
            FromIndex(current, out int x, out int y);

            int label = components.Labels[x, y];
            if (label >= 0 && label != mainId)
            {
                destination = current;
                break;
            }

            for (int i = 0; i < CardinalDirections.Length; i++)
            {
                int nx = x + CardinalDirections[i].x;
                int ny = y + CardinalDirections[i].y;

                if (!mass.IsInside(nx, ny) || mass.IsBlocked(nx, ny))
                    continue;

                int next = IndexOf(nx, ny);
                if (closed[next])
                    continue;

                float fieldValue = field.GetValue(nx, ny);
                float stepCost = 0.04f + Mathf.Pow(1f - fieldValue, 2f) * 3.0f;

                // Existing cloud is extremely cheap to traverse, so the path only pays
                // for newly created bridge pixels.
                if (mass.HasCloud(nx, ny))
                    stepCost *= 0.05f;

                float candidateDistance = distances[current] + stepCost;
                if (candidateDistance >= distances[next])
                    continue;

                distances[next] = candidateDistance;
                previous[next] = current;
                open.Push(next, candidateDistance);
            }
        }

        if (destination < 0)
            return false;

        int path = destination;
        while (path >= 0)
        {
            FromIndex(path, out int x, out int y);
            mass.SetCloud(x, y, true);

            if (components.Labels[x, y] == mainId)
                break;

            path = previous[path];
        }

        return true;
    }

    private ComponentMap BuildComponentMap()
    {
        int[,] labels = new int[mass.Width, mass.Height];

        for (int y = 0; y < mass.Height; y++)
        {
            for (int x = 0; x < mass.Width; x++)
                labels[x, y] = -1;
        }

        List<int> sizes = new List<int>();
        Queue<int> queue = new Queue<int>();
        int componentId = 0;

        for (int y = 0; y < mass.Height; y++)
        {
            for (int x = 0; x < mass.Width; x++)
            {
                if (!mass.HasCloud(x, y) || labels[x, y] >= 0)
                    continue;

                int size = 0;
                labels[x, y] = componentId;
                queue.Enqueue(IndexOf(x, y));

                while (queue.Count > 0)
                {
                    int index = queue.Dequeue();
                    FromIndex(index, out int cx, out int cy);
                    size++;

                    for (int i = 0; i < CardinalDirections.Length; i++)
                    {
                        int nx = cx + CardinalDirections[i].x;
                        int ny = cy + CardinalDirections[i].y;

                        if (!mass.IsInside(nx, ny) || !mass.HasCloud(nx, ny) || labels[nx, ny] >= 0)
                            continue;

                        labels[nx, ny] = componentId;
                        queue.Enqueue(IndexOf(nx, ny));
                    }
                }

                sizes.Add(size);
                componentId++;
            }
        }

        return new ComponentMap(labels, sizes);
    }

    private bool CanRemoveWithoutDisconnecting(int x, int y)
    {
        if (!mass.HasCloud(x, y))
            return false;

        List<Vector2Int> neighbours = new List<Vector2Int>(4);

        for (int i = 0; i < CardinalDirections.Length; i++)
        {
            int nx = x + CardinalDirections[i].x;
            int ny = y + CardinalDirections[i].y;

            if (mass.HasCloud(nx, ny))
                neighbours.Add(new Vector2Int(nx, ny));
        }

        if (neighbours.Count <= 1)
            return true;

        // Removing a pixel disconnects the cloud exactly when its occupied neighbours
        // can no longer reach one another without travelling through that pixel.
        bool[,] visited = new bool[mass.Width, mass.Height];
        Queue<int> queue = new Queue<int>();
        Vector2Int start = neighbours[0];
        visited[start.x, start.y] = true;
        queue.Enqueue(IndexOf(start.x, start.y));

        int remainingNeighbours = neighbours.Count - 1;
        bool[] reached = new bool[neighbours.Count];
        reached[0] = true;

        while (queue.Count > 0 && remainingNeighbours > 0)
        {
            int index = queue.Dequeue();
            FromIndex(index, out int cx, out int cy);

            for (int target = 1; target < neighbours.Count; target++)
            {
                if (!reached[target] && neighbours[target].x == cx && neighbours[target].y == cy)
                {
                    reached[target] = true;
                    remainingNeighbours--;
                }
            }

            for (int i = 0; i < CardinalDirections.Length; i++)
            {
                int nx = cx + CardinalDirections[i].x;
                int ny = cy + CardinalDirections[i].y;

                if (!mass.IsInside(nx, ny) || visited[nx, ny] || !mass.HasCloud(nx, ny))
                    continue;

                if (nx == x && ny == y)
                    continue;

                visited[nx, ny] = true;
                queue.Enqueue(IndexOf(nx, ny));
            }
        }

        return remainingNeighbours == 0;
    }

    private ScoredPixel? FindWeakestRemovableBoundaryPixel()
    {
        List<ScoredPixel> candidates = new List<ScoredPixel>();

        for (int y = 0; y < mass.Height; y++)
        {
            for (int x = 0; x < mass.Width; x++)
            {
                if (mass.HasCloud(x, y) && IsCloudBoundary(x, y))
                    candidates.Add(new ScoredPixel(x, y, ScoreOccupiedPixel(x, y)));
            }
        }

        candidates.Sort((a, b) => a.Score.CompareTo(b.Score));

        for (int i = 0; i < candidates.Count; i++)
        {
            ScoredPixel candidate = candidates[i];
            if (CanRemoveWithoutDisconnecting(candidate.X, candidate.Y))
                return candidate;
        }

        return null;
    }

    private ScoredPixel? FindStrongestGrowthBoundaryPixel(RectInt? damageArea, float localRepairBias)
    {
        bool found = false;
        ScoredPixel best = default;

        for (int y = 0; y < mass.Height; y++)
        {
            for (int x = 0; x < mass.Width; x++)
            {
                if (!IsGrowthBoundary(x, y))
                    continue;

                float score = ScoreGrowthCandidate(x, y, damageArea, localRepairBias);

                if (!found || score > best.Score)
                {
                    best = new ScoredPixel(x, y, score);
                    found = true;
                }
            }
        }

        return found ? best : (ScoredPixel?)null;
    }

    private float ScoreGrowthCandidate(int x, int y, RectInt? damageArea, float localRepairBias)
    {
        int cardinal = CountCloudNeighbours(x, y, CardinalDirections);
        int diagonal = CountCloudNeighbours(x, y, EightDirections) - cardinal;

        float score =
            (field.GetValue(x, y) * 2.0f) +
            (cardinal * cohesion * 0.35f) +
            (diagonal * 0.06f);

        // Prefer candidates which thicken an existing surface rather than extending a
        // single-pixel spike.
        if (cardinal == 1)
            score -= 0.35f;
        else if (cardinal >= 3)
            score += 0.18f;

        if (damageArea.HasValue && localRepairBias > 0f)
            score += DamageProximity(x, y, damageArea.Value) * localRepairBias;

        return score;
    }

    private float ScoreOccupiedPixel(int x, int y)
    {
        int cardinal = CountCloudNeighbours(x, y, CardinalDirections);
        int diagonal = CountCloudNeighbours(x, y, EightDirections) - cardinal;

        float score =
            (field.GetValue(x, y) * 2.0f) +
            (cardinal * cohesion * 0.35f) +
            (diagonal * 0.06f);

        if (cardinal <= 1)
            score -= 0.75f;

        return score;
    }

    private float DamageProximity(int x, int y, RectInt damageArea)
    {
        float closestX = Mathf.Clamp(x, damageArea.xMin, damageArea.xMax - 1);
        float closestY = Mathf.Clamp(y, damageArea.yMin, damageArea.yMax - 1);
        float dx = x - closestX;
        float dy = y - closestY;
        float distance = Mathf.Sqrt((dx * dx) + (dy * dy));

        float radius = Mathf.Max(8f, Mathf.Min(mass.Width, mass.Height) * 0.18f);
        return 1f - Mathf.Clamp01(distance / radius);
    }

    private bool IsGrowthBoundary(int x, int y)
    {
        if (!mass.IsInside(x, y) || mass.HasCloud(x, y) || mass.IsBlocked(x, y))
            return false;

        for (int i = 0; i < CardinalDirections.Length; i++)
        {
            if (mass.HasCloud(x + CardinalDirections[i].x, y + CardinalDirections[i].y))
                return true;
        }

        return false;
    }

    private bool IsCloudBoundary(int x, int y)
    {
        if (!mass.HasCloud(x, y))
            return false;

        for (int i = 0; i < CardinalDirections.Length; i++)
        {
            int nx = x + CardinalDirections[i].x;
            int ny = y + CardinalDirections[i].y;

            if (!mass.IsInside(nx, ny) || !mass.HasCloud(nx, ny))
                return true;
        }

        return false;
    }

    private int CountCloudNeighbours(int x, int y, Vector2Int[] directions)
    {
        int count = 0;

        for (int i = 0; i < directions.Length; i++)
        {
            if (mass.HasCloud(x + directions[i].x, y + directions[i].y))
                count++;
        }

        return count;
    }

    private int IndexOf(int x, int y)
    {
        return x + (y * mass.Width);
    }

    private void FromIndex(int index, out int x, out int y)
    {
        x = index % mass.Width;
        y = index / mass.Width;
    }

    private readonly struct ScoredPixel
    {
        public int X { get; }
        public int Y { get; }
        public float Score { get; }

        public ScoredPixel(int x, int y, float score)
        {
            X = x;
            Y = y;
            Score = score;
        }
    }

    private sealed class ComponentMap
    {
        public int[,] Labels { get; }
        public List<int> Sizes { get; }
        public int ComponentCount => Sizes.Count;

        public ComponentMap(int[,] labels, List<int> sizes)
        {
            Labels = labels;
            Sizes = sizes;
        }

        public int GetLargestComponentId()
        {
            int bestId = -1;
            int bestSize = -1;

            for (int i = 0; i < Sizes.Count; i++)
            {
                if (Sizes[i] > bestSize)
                {
                    bestSize = Sizes[i];
                    bestId = i;
                }
            }

            return bestId;
        }
    }

    private readonly struct HeapNode
    {
        public int Index { get; }
        public float Priority { get; }

        public HeapNode(int index, float priority)
        {
            Index = index;
            Priority = priority;
        }
    }

    /// <summary>
    /// Small binary heap used instead of System.PriorityQueue so the code works with
    /// Unity versions whose scripting profile does not provide that type.
    /// </summary>
    private sealed class BinaryHeap
    {
        private readonly List<HeapNode> items = new List<HeapNode>();
        private readonly bool isMinHeap;

        public int Count => items.Count;

        public BinaryHeap(bool isMinHeap)
        {
            this.isMinHeap = isMinHeap;
        }

        public void Push(int index, float priority)
        {
            HeapNode node = new HeapNode(index, priority);
            items.Add(node);
            int child = items.Count - 1;

            while (child > 0)
            {
                int parent = (child - 1) / 2;
                if (!ComesBefore(items[child], items[parent]))
                    break;

                Swap(child, parent);
                child = parent;
            }
        }

        public HeapNode Pop()
        {
            if (items.Count == 0)
                throw new InvalidOperationException("Heap is empty.");

            HeapNode root = items[0];
            int last = items.Count - 1;
            items[0] = items[last];
            items.RemoveAt(last);

            int parent = 0;

            while (true)
            {
                int left = (parent * 2) + 1;
                int right = left + 1;
                int best = parent;

                if (left < items.Count && ComesBefore(items[left], items[best]))
                    best = left;

                if (right < items.Count && ComesBefore(items[right], items[best]))
                    best = right;

                if (best == parent)
                    break;

                Swap(parent, best);
                parent = best;
            }

            return root;
        }

        private bool ComesBefore(HeapNode a, HeapNode b)
        {
            return isMinHeap ? a.Priority < b.Priority : a.Priority > b.Priority;
        }

        private void Swap(int a, int b)
        {
            HeapNode temp = items[a];
            items[a] = items[b];
            items[b] = temp;
        }
    }
}
