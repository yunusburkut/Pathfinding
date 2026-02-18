using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class Pathfinder : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private GridManager grid;

    [Header("BFS Visual Settings")]
    [Tooltip("How long should we wait each time a new cell is tried while BFS is running? (0 = no wait)")]
    [SerializeField] private float bfsStepDelaySeconds = 0.03f;

    [Tooltip("Color of the 'tried/visited' cells during BFS")]
    [SerializeField] private Color searchedCellColor = Color.yellow;

    [Tooltip("Color of the shortest path drawn when the target is found")]
    [SerializeField] private Color finalPathColor = Color.green;

    private Coroutine findPathCoroutine;

    // --------------------------------------------------------------------
    // PERF: Reusable buffers (to reduce GC allocations)
    // --------------------------------------------------------------------
    // We traverse the grid using a 1D index instead of 2D (x,y): index = y * width + x
    //
    // visitedMarkByIndex:
    //  - Stores whether each cell was visited "in this run".
    //  - Instead of a classic bool[] visited, we use the "stamp/mark" technique:
    //      currentVisitMark increments each run,
    //      if visitedMarkByIndex[i] == currentVisitMark, it was visited in this run.
    //  - This way we don't have to clear the whole array with Array.Clear every time.
    private int[] visitedMarkByIndex;      // length = width*height

    // parentIndexByIndex:
    //  - Stores the "parent" so we can reconstruct the path after BFS reaches the target.
    //  - parentIndexByIndex[child] = parentIndex
    //  - The start cell's parent is -1.
    private int[] parentIndexByIndex;      // length = width*height

    // bfsQueue:
    //  - We use an int[] for the BFS queue (to avoid List/Queue allocations).
    //  - Works with head/tail pointers.
    private int[] bfsQueue;                // length = width*height

    private int currentVisitMark;

    // coloredIndicesThisRun:
    //  - Stores indices of cells whose color we changed in this run.
    //  - Before the next run, we reset only these cells (we don't scan the whole grid).
    private readonly List<int> coloredIndicesThisRun = new List<int>(256);

    // WaitForSeconds cache:
    //  - Continuously doing "new WaitForSeconds(x)" inside a coroutine creates unnecessary allocations.
    //  - We cache a single object for the same delay value and reuse it.
    private WaitForSeconds cachedStepWait;
    private float cachedStepWaitSeconds = -1f;

    // Four directions (right, left, up, down). No diagonals.
    private static readonly Vector2Int[] FourDirections =
    {
        new Vector2Int(1, 0),
        new Vector2Int(-1, 0),
        new Vector2Int(0, 1),
        new Vector2Int(0, -1),
    };

    private void Update()
    {
        // Press Space to start/stop BFS (toggle).
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            TogglePathfinding();
    }

    private void TogglePathfinding()
    {
        // If it's already running: stop.
        if (findPathCoroutine != null)
        {
            StopCoroutine(findPathCoroutine);
            findPathCoroutine = null;
            return;
        }

        // Otherwise: start.
        findPathCoroutine = StartCoroutine(FindPathWithBfsRoutine(bfsStepDelaySeconds));
    }

    private void OnDisable()
    {
        // Don't leave the coroutine running when the object gets disabled.
        if (findPathCoroutine != null)
        {
            StopCoroutine(findPathCoroutine);
            findPathCoroutine = null;
        }
    }

    private IEnumerator FindPathWithBfsRoutine(float stepDelay)
    {
        // Safety checks: do we have references and a valid start/end selection?
        if (grid == null) yield break;
        if (!grid.StartCell.HasValue || !grid.EndCell.HasValue) yield break;

        int gridWidth = grid.Width;
        int gridHeight = grid.Height;
        int gridSize = gridWidth * gridHeight;

        EnsureReusableBuffers(gridSize);

        Vector2Int startCell = grid.StartCell.Value;
        Vector2Int endCell = grid.EndCell.Value;

        // Are Start/End within bounds?
        if ((uint)startCell.x >= (uint)gridWidth || (uint)startCell.y >= (uint)gridHeight) yield break;
        if ((uint)endCell.x >= (uint)gridWidth || (uint)endCell.y >= (uint)gridHeight) yield break;

        // If Start/End are blocked, searching for a path is meaningless.
        if (grid.IsBlocked(startCell.x, startCell.y) || grid.IsBlocked(endCell.x, endCell.y)) yield break;

        int startIndex = ToIndex(startCell.x, startCell.y, gridWidth);
        int endIndex = ToIndex(endCell.x, endCell.y, gridWidth);

        // Clear paints from the previous run (instead of scanning the whole grid, we only reset what we touched).
        ResetPreviouslyColoredCells(startCell, endCell);

        // ------------------------------------------------------------
        // Clearing visited using the "stamp" technique:
        //  - When we do currentVisitMark++, we create a new "label" for this run.
        //  - visitedMarkByIndex[i] == currentVisitMark => visited in this run.
        //  - To avoid int.MaxValue overflow, we occasionally do a full clear.
        // ------------------------------------------------------------
        currentVisitMark++;
        if (currentVisitMark == int.MaxValue)
        {
            System.Array.Clear(visitedMarkByIndex, 0, visitedMarkByIndex.Length);
            currentVisitMark = 1;
        }

        // The start cell has no parent.
        parentIndexByIndex[startIndex] = -1;

        // Initialize the BFS queue.
        int queueHead = 0;
        int queueTail = 0;

        bfsQueue[queueTail++] = startIndex;
        visitedMarkByIndex[startIndex] = currentVisitMark;

        bool pathFound = false;

        // If there is a step delay, cache WaitForSeconds.
        WaitForSeconds stepWait = null;
        if (stepDelay > 0f)
        {
            if (cachedStepWaitSeconds != stepDelay)
            {
                cachedStepWaitSeconds = stepDelay;
                cachedStepWait = new WaitForSeconds(stepDelay);
            }
            stepWait = cachedStepWait;
        }

        // ------------------------------------------------------------
        // BFS:
        //  - Dequeue a cell
        //  - Visit its 4 neighbors
        //  - Enqueue a neighbor the first time we see it
        //  - Stop when we reach endIndex
        // BFS guarantee: in an unweighted grid, the first path found is the "shortest" path.
        // ------------------------------------------------------------
        while (queueHead < queueTail && !pathFound)
        {
            int currentIndex = bfsQueue[queueHead++];

            int currentX = currentIndex % gridWidth;
            int currentY = currentIndex / gridWidth;

            for (int dir = 0; dir < 4; dir++)
            {
                int neighborX = currentX + FourDirections[dir].x;
                int neighborY = currentY + FourDirections[dir].y;

                // If out of bounds, skip.
                if ((uint)neighborX >= (uint)gridWidth || (uint)neighborY >= (uint)gridHeight)
                    continue;

                // If it's a wall/blocked cell, skip.
                if (grid.IsBlocked(neighborX, neighborY))
                    continue;

                int neighborIndex = ToIndex(neighborX, neighborY, gridWidth);

                // If we already visited it in this run, don't enqueue again.
                if (visitedMarkByIndex[neighborIndex] == currentVisitMark)
                    continue;

                // Visit and store parent info (critical for reconstructing the path).
                visitedMarkByIndex[neighborIndex] = currentVisitMark;
                parentIndexByIndex[neighborIndex] = currentIndex;

                // Did we reach the target?
                if (neighborIndex == endIndex)
                {
                    pathFound = true;
                    break;
                }

                // Enqueue.
                bfsQueue[queueTail++] = neighborIndex;

                // Visualization: paint tried cells (except start) yellow.
                // (We already did bounds checks, so TryGetTile won't go out of range.)
                if (neighborIndex != startIndex)
                {
                    if (grid.TryGetTile(neighborX, neighborY, out var tile))
                    {
                        tile.SetColor(searchedCellColor);
                        coloredIndicesThisRun.Add(neighborIndex);
                    }
                }

                // Wait to make it easier to follow step-by-step.
                if (stepWait != null)
                    yield return stepWait;
            }
        }

        // If we couldn't reach the target, exit.
        if (!pathFound)
            yield break;

        // ------------------------------------------------------------
        // RECONSTRUCT PATH (end -> start):
        //  - Walk backwards from end to start using parentIndexByIndex.
        //  - To avoid allocating a new List, we reuse the bfsQueue array as a "path buffer" this time.
        // ------------------------------------------------------------
        int pathLength = 0;
        int walkerIndex = endIndex;

        while (walkerIndex != startIndex)
        {
            bfsQueue[pathLength++] = walkerIndex;

            int parent = parentIndexByIndex[walkerIndex];
            if (parent < 0) yield break; // should not happen in theory; safety.
            walkerIndex = parent;
        }

        bfsQueue[pathLength++] = startIndex;

        // Walk in reverse order to paint the path as start -> end.
        for (int i = pathLength - 1; i >= 0; i--)
        {
            int indexOnPath = bfsQueue[i];
            int x = indexOnPath % gridWidth;
            int y = indexOnPath / gridWidth;

            if (grid.TryGetTile(x, y, out var tile))
            {
                tile.SetColor(finalPathColor);
                coloredIndicesThisRun.Add(indexOnPath);
            }

            // Drawing the path "frame by frame" usually looks nicer.
            yield return null;
        }

        // Optionally re-apply endpoint colors for safety (UI might have repainted them).
        if (grid.TryGetTile(startCell.x, startCell.y, out var startTile)) startTile.SetColor(Color.green);
        if (grid.TryGetTile(endCell.x, endCell.y, out var endTile)) endTile.SetColor(Color.red);
    }

    private void EnsureReusableBuffers(int gridSize)
    {
        // If the grid size changed, recreate the buffers.
        if (visitedMarkByIndex == null || visitedMarkByIndex.Length != gridSize)
        {
            visitedMarkByIndex = new int[gridSize];
            parentIndexByIndex = new int[gridSize];
            bfsQueue = new int[gridSize];
            currentVisitMark = 0;
        }
    }

    private void ResetPreviouslyColoredCells(Vector2Int startCell, Vector2Int endCell)
    {
        int gridWidth = grid.Width;

        // Reset only what we painted before -> O(k) (k = number of colored cells)
        // Resetting the entire grid would be O(n).
        for (int i = 0; i < coloredIndicesThisRun.Count; i++)
        {
            int idx = coloredIndicesThisRun[i];
            int x = idx % gridWidth;
            int y = idx / gridWidth;

            // We don't reset Start/End cells; their colors should stay fixed.
            if (x == startCell.x && y == startCell.y) continue;
            if (x == endCell.x && y == endCell.y) continue;

            if (grid.TryGetTile(x, y, out var tile))
                tile.ResetColor();
        }

        coloredIndicesThisRun.Clear();

        // Re-apply endpoint colors (guarantee).
        if (grid.TryGetTile(startCell.x, startCell.y, out var startTile)) startTile.SetColor(Color.green);
        if (grid.TryGetTile(endCell.x, endCell.y, out var endTile)) endTile.SetColor(Color.red);
    }

    // Converts 2D coordinates to a 1D index (row-major).
    private static int ToIndex(int x, int y, int gridWidth) => y * gridWidth + x;
}