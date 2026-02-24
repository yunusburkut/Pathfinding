using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class Pathfinder : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GridManager grid;

    [Header("Visualization Settings")]
    [Tooltip("How long should we wait each time a new cell is tried while algorithm is running? (0 = no wait)")]
    [SerializeField] private float stepDelaySeconds = 0.03f;

    [Tooltip("Color of the 'tried/visited' cells during search")]
    [SerializeField] private Color searchedCellColor = Color.yellow;

    [Tooltip("Color of the shortest path drawn when the target is found")]
    [SerializeField] private Color finalPathColor = Color.green;

    
    [Header("Block Density")]
    [SerializeField] private Button generateBlocksButton;
    [SerializeField] private Slider blockDensitySlider;
    
    [Header("Algo Buttons")]
    [SerializeField] private Button bfsButton;
    [SerializeField] private Button aStarButton;

    private Coroutine findPathCoroutine;

    // --------------------------------------------------------------------
    // Reusable buffers (performance / GC friendly)
    // --------------------------------------------------------------------
    // We address the grid as a single 1D array:
    //   index = y * width + x
    //
    // visitedMarkByIndex uses a "stamp" technique:
    // - currentVisitMark increments each run
    // - visitedMarkByIndex[i] == currentVisitMark means "visited in this run"
    // This avoids clearing a bool[] every time.
    private int[] visitedMarkByIndex;

    // parentIndexByIndex stores the predecessor for each node so we can rebuild
    // the final shortest path after the search finishes.
    private int[] parentIndexByIndex;

    // Used as:
    // - BFS queue during BFS
    // - Path buffer during path reconstruction (end -> start)
    private int[] queueOrPathBuffer;

    private int currentVisitMark;

    // Tracks which cells we painted, so we can reset only those next run (O(k) not O(n)).
    private readonly List<int> coloredIndicesThisRun = new List<int>(256);

    // WaitForSeconds caching prevents allocations inside coroutines.
    private WaitForSeconds cachedStepWait;
    private float cachedStepWaitSeconds = -1f;

    // --------------------------------------------------------------------
    // A* state (priority queue + scores)
    // --------------------------------------------------------------------
    // gScore: best-known cost from start to this node
    // fScore: heap priority = g + h (Manhattan heuristic)
    private int[] gScoreByIndex;
    private int[] fScoreByIndex;

    // Min-heap (priority queue) for the "open set".
    // We store node indices in the heap and compare them by fScoreByIndex.
    private int[] openHeap;
    private int[] openHeapPosByIndex; // nodeIndex -> heap position, -1 if not in heap
    private int openHeapSize;

    // Used by heap tie-breaks without querying GridManager repeatedly.
    private int heapGridWidth;
    private int heapEndX;
    private int heapEndY;

    // 4-neighborhood movement (no diagonals).
    private static readonly Vector2Int[] FourDirections =
    {
        new Vector2Int(1, 0),
        new Vector2Int(-1, 0),
        new Vector2Int(0, 1),
        new Vector2Int(0, -1),
    };

    private void Start()
    {
        bfsButton.onClick.AddListener(RunBfs);
        aStarButton.onClick.AddListener(RunAStar);
        generateBlocksButton.onClick.AddListener(GenerateBlocks);
    }

    
    private void GenerateBlocks()
    {
        grid.RandomizeBlocksEnsuringPath(blockDensitySlider.value, maxAttempts: 200);
    }
    
    private void OnDestroy()
    {
        // Always unhook listeners to avoid leaks / double subscriptions in domain reload scenarios.
        bfsButton.onClick.RemoveListener(RunBfs);  
        aStarButton.onClick.RemoveListener(RunAStar);
        generateBlocksButton.onClick.RemoveListener(GenerateBlocks);
    }

    public void RunBfs()
    {
        StopCurrentPathfindingIfAny();
        findPathCoroutine = StartCoroutine(FindPathWithBfsRoutine(stepDelaySeconds));
    }

    public void RunAStar()
    {
        StopCurrentPathfindingIfAny();
        findPathCoroutine = StartCoroutine(FindPathWithAStarRoutine(stepDelaySeconds));
    }

    private void StopCurrentPathfindingIfAny()
    {
        // Only one search coroutine should be active at a time.
        if (findPathCoroutine != null)
        {
            StopCoroutine(findPathCoroutine);
            findPathCoroutine = null;
        }
    }

    private void OnDisable()
    {
        // Safety: stop running coroutine when the object becomes inactive.
        StopCurrentPathfindingIfAny();
    }

    private IEnumerator FindPathWithBfsRoutine(float stepDelay)
    {
        // Validate references + start/end selection + bounds + blocked state.
        if (!TryPrepareRun(out int gridWidth, out int gridHeight, out int gridSize,
                out Vector2Int startCell, out Vector2Int endCell,
                out int startIndex, out int endIndex))
            yield break;

        EnsureReusableBuffers(gridSize);

        // Only reset cells we previously colored (fast for large grids).
        ResetPreviouslyColoredCells(startCell, endCell);

        // New "visit stamp" for this run.
        currentVisitMark++;
        if (currentVisitMark == int.MaxValue)
        {
            // Rare overflow guard: reset array and restart stamps.
            System.Array.Clear(visitedMarkByIndex, 0, visitedMarkByIndex.Length);
            currentVisitMark = 1;
        }

        parentIndexByIndex[startIndex] = -1;

        // BFS queue implemented on top of an int[] with head/tail pointers.
        int queueHead = 0;
        int queueTail = 0;

        queueOrPathBuffer[queueTail++] = startIndex;
        visitedMarkByIndex[startIndex] = currentVisitMark;

        WaitForSeconds stepWait = GetStepWait(stepDelay);

        bool pathFound = false;

        // BFS guarantees shortest path on an unweighted grid.
        while (queueHead < queueTail && !pathFound)
        {
            int currentIndex = queueOrPathBuffer[queueHead++];

            int currentX = currentIndex % gridWidth;
            int currentY = currentIndex / gridWidth;

            for (int dir = 0; dir < 4; dir++)
            {
                int neighborX = currentX + FourDirections[dir].x;
                int neighborY = currentY + FourDirections[dir].y;

                if ((uint)neighborX >= (uint)gridWidth || (uint)neighborY >= (uint)gridHeight)
                    continue;

                if (grid.IsBlocked(neighborX, neighborY))
                    continue;

                int neighborIndex = ToIndex(neighborX, neighborY, gridWidth);

                if (visitedMarkByIndex[neighborIndex] == currentVisitMark)
                    continue;

                visitedMarkByIndex[neighborIndex] = currentVisitMark;
                parentIndexByIndex[neighborIndex] = currentIndex;

                if (neighborIndex == endIndex)
                {
                    pathFound = true;
                    break;
                }

                queueOrPathBuffer[queueTail++] = neighborIndex;

                // Visualize explored cells (skip start cell so it stays green).
                if (neighborIndex != startIndex && grid.TryGetTile(neighborX, neighborY, out var tile))
                {
                    tile.SetColor(searchedCellColor);
                    coloredIndicesThisRun.Add(neighborIndex);
                }

                if (stepWait != null)
                    yield return stepWait;
            }
        }

        if (!pathFound)
            yield break;

        // Paint final path from start -> end.
        yield return ReconstructAndPaintPathRoutine(gridWidth, startCell, endCell, startIndex, endIndex);
    }

    private IEnumerator FindPathWithAStarRoutine(float stepDelay)
    {
        // A* uses a heuristic (Manhattan distance) to guide the search.
        // With 4-direction movement and uniform costs, Manhattan is admissible and consistent.
        if (!TryPrepareRun(out int gridWidth, out int gridHeight, out int gridSize,
                out Vector2Int startCell, out Vector2Int endCell,
                out int startIndex, out int endIndex))
            yield break;

        EnsureReusableBuffers(gridSize);
        ResetPreviouslyColoredCells(startCell, endCell);

        currentVisitMark++;
        if (currentVisitMark == int.MaxValue)
        {
            System.Array.Clear(visitedMarkByIndex, 0, visitedMarkByIndex.Length);
            currentVisitMark = 1;
        }

        heapGridWidth = gridWidth;
        heapEndX = endCell.x;
        heapEndY = endCell.y;

        for (int i = 0; i < gridSize; i++)
        {
            gScoreByIndex[i] = int.MaxValue;
            fScoreByIndex[i] = int.MaxValue;
            openHeapPosByIndex[i] = -1;
            parentIndexByIndex[i] = -1;
        }

        openHeapSize = 0;

        gScoreByIndex[startIndex] = 0;
        fScoreByIndex[startIndex] = Manhattan(startCell.x, startCell.y, endCell.x, endCell.y);
        HeapPush(startIndex);

        WaitForSeconds stepWait = GetStepWait(stepDelay);

        bool pathFound = false;

        while (openHeapSize > 0)
        {
            int currentIndex = HeapPopMin();

            if (visitedMarkByIndex[currentIndex] == currentVisitMark)
                continue;

            visitedMarkByIndex[currentIndex] = currentVisitMark;

            if (currentIndex == endIndex)
            {
                pathFound = true;
                break;
            }

            int cx = currentIndex % gridWidth;
            int cy = currentIndex / gridWidth;

            if (currentIndex != startIndex && grid.TryGetTile(cx, cy, out var curTile))
            {
                curTile.SetColor(searchedCellColor);
                coloredIndicesThisRun.Add(currentIndex);
            }

            int currentG = gScoreByIndex[currentIndex];
            if (currentG == int.MaxValue)
                continue;

            for (int dir = 0; dir < 4; dir++)
            {
                int nx = cx + FourDirections[dir].x;
                int ny = cy + FourDirections[dir].y;

                if ((uint)nx >= (uint)gridWidth || (uint)ny >= (uint)gridHeight)
                    continue;

                if (grid.IsBlocked(nx, ny))
                    continue;

                int neighborIndex = ToIndex(nx, ny, gridWidth);

                if (visitedMarkByIndex[neighborIndex] == currentVisitMark)
                    continue;

                // For weighted grids, replace "+ 1" with "+ moveCost".
                int tentativeG = currentG + 1;

                if (tentativeG < gScoreByIndex[neighborIndex])
                {
                    parentIndexByIndex[neighborIndex] = currentIndex;
                    gScoreByIndex[neighborIndex] = tentativeG;

                    int h = Manhattan(nx, ny, endCell.x, endCell.y);
                    fScoreByIndex[neighborIndex] = tentativeG + h;

                    if (openHeapPosByIndex[neighborIndex] < 0)
                        HeapPush(neighborIndex);
                    else
                        HeapDecreaseKey(neighborIndex);
                }
            }

            if (stepWait != null)
                yield return stepWait;
        }

        if (!pathFound)
            yield break;

        yield return ReconstructAndPaintPathRoutine(gridWidth, startCell, endCell, startIndex, endIndex);
    }

    private IEnumerator ReconstructAndPaintPathRoutine(int gridWidth, Vector2Int startCell, Vector2Int endCell, int startIndex, int endIndex)
    {
        // Rebuild path backwards using parent links:
        // end -> ... -> start
        // We reuse queueOrPathBuffer to avoid allocating a List.
        int pathLength = 0;
        int walkerIndex = endIndex;

        while (walkerIndex != startIndex)
        {
            queueOrPathBuffer[pathLength++] = walkerIndex;

            int parent = parentIndexByIndex[walkerIndex];
            if (parent < 0) yield break; // Safety: no path chain.
            walkerIndex = parent;
        }

        queueOrPathBuffer[pathLength++] = startIndex;

        // Paint in reverse so it becomes start -> end.
        for (int i = pathLength - 1; i >= 0; i--)
        {
            int indexOnPath = queueOrPathBuffer[i];
            int x = indexOnPath % gridWidth;
            int y = indexOnPath / gridWidth;

            if (grid.TryGetTile(x, y, out var tile))
            {
                tile.SetColor(finalPathColor);
                coloredIndicesThisRun.Add(indexOnPath);
            }

            // Drawing one cell per frame reads nicely in the UI.
            yield return null;
        }

        // Re-apply endpoint colors to keep them stable.
        if (grid.TryGetTile(startCell.x, startCell.y, out var startTile)) startTile.SetColor(Color.green);
        if (grid.TryGetTile(endCell.x, endCell.y, out var endTile)) endTile.SetColor(Color.red);
    }

    private bool TryPrepareRun(out int gridWidth, out int gridHeight, out int gridSize,
        out Vector2Int startCell, out Vector2Int endCell,
        out int startIndex, out int endIndex)
    {
        // Centralized validation so BFS/A* stay small and consistent.
        gridWidth = 0;
        gridHeight = 0;
        gridSize = 0;
        startCell = default;
        endCell = default;
        startIndex = -1;
        endIndex = -1;

        if (grid == null) return false;
        if (!grid.StartCell.HasValue || !grid.EndCell.HasValue) return false;

        gridWidth = grid.Width;
        gridHeight = grid.Height;

        if (gridWidth <= 0 || gridHeight <= 0) return false;

        gridSize = gridWidth * gridHeight;

        startCell = grid.StartCell.Value;
        endCell = grid.EndCell.Value;

        // Unsigned bounds checks are branch-friendly and safe.
        if ((uint)startCell.x >= (uint)gridWidth || (uint)startCell.y >= (uint)gridHeight) return false;
        if ((uint)endCell.x >= (uint)gridWidth || (uint)endCell.y >= (uint)gridHeight) return false;

        if (grid.IsBlocked(startCell.x, startCell.y) || grid.IsBlocked(endCell.x, endCell.y)) return false;

        startIndex = ToIndex(startCell.x, startCell.y, gridWidth);
        endIndex = ToIndex(endCell.x, endCell.y, gridWidth);

        return true;
    }

    private WaitForSeconds GetStepWait(float stepDelay)
    {
        // Cache WaitForSeconds to avoid allocations in tight loops.
        if (stepDelay <= 0f) return null;

        if (cachedStepWaitSeconds != stepDelay)
        {
            cachedStepWaitSeconds = stepDelay;
            cachedStepWait = new WaitForSeconds(stepDelay);
        }

        return cachedStepWait;
    }

    private void EnsureReusableBuffers(int gridSize)
    {
        // Allocate all buffers once and reuse; recreate only if grid size changes.
        if (visitedMarkByIndex == null || visitedMarkByIndex.Length != gridSize)
        {
            visitedMarkByIndex = new int[gridSize];
            parentIndexByIndex = new int[gridSize];
            queueOrPathBuffer = new int[gridSize];
            currentVisitMark = 0;

            gScoreByIndex = new int[gridSize];
            fScoreByIndex = new int[gridSize];
            openHeap = new int[gridSize];
            openHeapPosByIndex = new int[gridSize];
            openHeapSize = 0;
        }
    }

    private void ResetPreviouslyColoredCells(Vector2Int startCell, Vector2Int endCell)
    {
        // Reset only touched tiles for better performance on larger grids.
        int gridWidth = grid.Width;

        for (int i = 0; i < coloredIndicesThisRun.Count; i++)
        {
            int idx = coloredIndicesThisRun[i];
            int x = idx % gridWidth;
            int y = idx / gridWidth;

            // Keep start/end colors stable.
            if (x == startCell.x && y == startCell.y) continue;
            if (x == endCell.x && y == endCell.y) continue;

            if (grid.TryGetTile(x, y, out var tile))
                tile.ResetColor();
        }

        coloredIndicesThisRun.Clear();

        if (grid.TryGetTile(startCell.x, startCell.y, out var startTile)) startTile.SetColor(Color.green);
        if (grid.TryGetTile(endCell.x, endCell.y, out var endTile)) endTile.SetColor(Color.red);
    }

    // -------------------------
    // Min-Heap (Open Set)
    // -------------------------
    // This heap is a lightweight priority queue implementation specialized for this grid:
    // - nodes are int indices
    // - priority is fScoreByIndex[index]
    // - we keep openHeapPosByIndex so we can do DecreaseKey in O(log n)
    private void HeapPush(int nodeIndex)
    {
        int pos = openHeapSize;
        openHeap[openHeapSize++] = nodeIndex;
        openHeapPosByIndex[nodeIndex] = pos;
        HeapSiftUp(pos);
    }

    private int HeapPopMin()
    {
        // Removes and returns the node with smallest fScore (tie-broken by heuristic closeness).
        int min = openHeap[0];
        openHeapPosByIndex[min] = -1;

        openHeapSize--;
        if (openHeapSize > 0)
        {
            int last = openHeap[openHeapSize];
            openHeap[0] = last;
            openHeapPosByIndex[last] = 0;
            HeapSiftDown(0);
        }

        return min;
    }

    private void HeapDecreaseKey(int nodeIndex)
    {
        // The node's fScore got smaller; bubble it up.
        int pos = openHeapPosByIndex[nodeIndex];
        if (pos >= 0)
            HeapSiftUp(pos);
    }

    private void HeapSiftUp(int pos)
    {
        while (pos > 0)
        {
            int parent = (pos - 1) >> 1;
            if (HeapIsLessOrEqual(openHeap[parent], openHeap[pos]))
                break;

            HeapSwap(parent, pos);
            pos = parent;
        }
    }

    private void HeapSiftDown(int pos)
    {
        while (true)
        {
            int left = (pos << 1) + 1;
            if (left >= openHeapSize) break;

            int right = left + 1;
            int smallest = left;

            if (right < openHeapSize && HeapIsLess(openHeap[right], openHeap[left]))
                smallest = right;

            if (HeapIsLessOrEqual(openHeap[pos], openHeap[smallest]))
                break;

            HeapSwap(pos, smallest);
            pos = smallest;
        }
    }

    private bool HeapIsLess(int aIndex, int bIndex)
    {
        // Primary key: smaller fScore first.
        int fa = fScoreByIndex[aIndex];
        int fb = fScoreByIndex[bIndex];
        if (fa != fb) return fa < fb;

        // Tie-break: prefer nodes closer to the goal (helps A* produce cleaner fronts).
        int ax = aIndex % heapGridWidth;
        int ay = aIndex / heapGridWidth;
        int bx = bIndex % heapGridWidth;
        int by = bIndex / heapGridWidth;

        int ha = Manhattan(ax, ay, heapEndX, heapEndY);
        int hb = Manhattan(bx, by, heapEndX, heapEndY);

        return ha < hb;
    }

    private bool HeapIsLessOrEqual(int aIndex, int bIndex)
        => aIndex == bIndex || !HeapIsLess(bIndex, aIndex);

    private void HeapSwap(int posA, int posB)
    {
        int a = openHeap[posA];
        int b = openHeap[posB];

        openHeap[posA] = b;
        openHeap[posB] = a;

        openHeapPosByIndex[a] = posB;
        openHeapPosByIndex[b] = posA;
    }

    private static int Manhattan(int x0, int y0, int x1, int y1)
        => Mathf.Abs(x0 - x1) + Mathf.Abs(y0 - y1);

    private static int ToIndex(int x, int y, int gridWidth) => y * gridWidth + x;
}