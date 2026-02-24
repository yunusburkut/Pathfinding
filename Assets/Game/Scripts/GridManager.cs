using System;
using Game.Scripts;
using UnityEngine;
using UnityEngine.Serialization;

public class GridManager : MonoBehaviour
{
    [Header("References")]
    [FormerlySerializedAs("gridView")]
    [SerializeField] private TileView tilePrefab;

    [SerializeField] private RectTransform gridParent;
    [SerializeField] private GridDataSO gridData;

    public event Action<int, int> CellClicked;

    private int _width;
    private int _height;

    private float _side;
    private float _spacing;
    private float _stepX;
    private float _stepY;
    private float _startX;
    private float _startY;

    private TileView[] _grid;
    private CellType[] _cells;
    private ClickStage _stage = ClickStage.PickGreen;
    
    // Reusable BFS buffer (no GC each try)
    private int[] _visitStampByIndex;
    private int _visitStamp;

    public int Width => _width;
    public int Height => _height;
    public Vector2Int? StartCell { get; private set; }
    public Vector2Int? EndCell { get; private set; }

    
    private void Start()
    {
        InitializeFromData();
        BuildGrid();
    }


    private void InitializeFromData()
    {
        _width = gridData != null ? gridData.Width : 0;
        _height = gridData != null ? gridData.Height : 0;

        int size = (_width > 0 && _height > 0) ? _width * _height : 0;
        _grid = size > 0 ? new TileView[size] : Array.Empty<TileView>();
        _cells = size > 0 ? new CellType[size] : Array.Empty<CellType>();

        StartCell = null;
        EndCell = null;
        _stage = ClickStage.PickGreen;
    }

    private void BuildGrid()
    {
        if (tilePrefab == null || gridParent == null || gridData == null) return;
        if (_width <= 0 || _height <= 0) return;

        CalculateLayout(gridData.TileOffset);

        for (int i = 0; i < _width; i++)
        {
            for (int j = 0; j < _height; j++)
            {
                var tile = Instantiate(tilePrefab, gridParent);
                _grid[Idx(i, j)] = tile;

                var rt = tile.GetComponent<RectTransform>();
                rt.sizeDelta = new Vector2(_side, _side);

                float x = _startX + i * _stepX;
                float y = _startY - j * _stepY;

                rt.anchoredPosition = new Vector2(x, y);
            }
        }
    }

    private void CalculateLayout(float offset)
    {
        var prefabRt = tilePrefab.GetComponent<RectTransform>();
        Vector2 prefabSize = prefabRt.rect.size;

        float baseSide = Mathf.Min(prefabSize.x, prefabSize.y);

        float availW = gridParent.rect.width;
        float availH = gridParent.rect.height;

        float baseTotalW = _width * baseSide + (_width - 1) * offset;
        float baseTotalH = _height * baseSide + (_height - 1) * offset;

        float scaleW = baseTotalW > 0f ? (availW / baseTotalW) : 1f;
        float scaleH = baseTotalH > 0f ? (availH / baseTotalH) : 1f;
        float scale = Mathf.Clamp01(Mathf.Min(scaleW, scaleH));

        _side = baseSide * scale;
        _spacing = offset * scale;

        gridParent.anchorMin = gridParent.anchorMax = new Vector2(0.5f, 0.5f);
        gridParent.pivot = new Vector2(0.5f, 0.5f);

        _stepX = _side + _spacing;
        _stepY = _side + _spacing;

        float totalW = _width * _side + (_width - 1) * _spacing;
        float totalH = _height * _side + (_height - 1) * _spacing;

        _startX = -totalW * 0.5f + _side * 0.5f;
        _startY = totalH * 0.5f - _side * 0.5f;
    }

    private void ApplyStartEndSelection(int x, int y)
    {
        if (IsBlocked(x, y))
            return;

        if (!TryGetTile(x, y, out var clickedTile))
            return;

        if (_stage == ClickStage.PickGreen)
        {
            if (StartCell.HasValue && TryGetTile(StartCell.Value.x, StartCell.Value.y, out var oldStart))
                oldStart.ResetColor();

            clickedTile.SetColor(Color.green);
            StartCell = new Vector2Int(x, y);

            _stage = ClickStage.PickRed;
            return;
        }

        if (EndCell.HasValue && TryGetTile(EndCell.Value.x, EndCell.Value.y, out var oldEnd))
            oldEnd.ResetColor();

        clickedTile.SetColor(Color.red);
        EndCell = new Vector2Int(x, y);

        _stage = ClickStage.PickGreen;
    }

    private void ToggleBlock(int x, int y)
    {
        if (!IsInBounds(x, y)) return;
        if (!TryGetTile(x, y, out var tile)) return;

        int idx = Idx(x, y);
        bool willBlock = _cells[idx] != CellType.Block;

        if (willBlock)
        {
            if (StartCell.HasValue && StartCell.Value.x == x && StartCell.Value.y == y) StartCell = null;
            if (EndCell.HasValue && EndCell.Value.x == x && EndCell.Value.y == y) EndCell = null;

            _cells[idx] = CellType.Block;
            tile.SetColor(Color.black);
        }
        else
        {
            _cells[idx] = CellType.None;
            tile.ResetColor();
        }
    }
    
    public bool RandomizeBlocksEnsuringPath(float blockProbability, int maxAttempts = 100, int? seed = null)
    {
        if (_cells == null || _cells.Length == 0) return false;
        if (!StartCell.HasValue || !EndCell.HasValue) return false;
        if (_width <= 0 || _height <= 0) return false;

        blockProbability = Mathf.Clamp01(blockProbability);

        var start = StartCell.Value;
        var end = EndCell.Value;

        int startIdx = Idx(start.x, start.y);
        int endIdx = Idx(end.x, end.y);

        // Backup current state so we can revert on failure
        var backup = new CellType[_cells.Length];
        Array.Copy(_cells, backup, _cells.Length);

        var rng = seed.HasValue ? new System.Random(seed.Value) : new System.Random();

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            for (int i = 0; i < _cells.Length; i++)
            {
                if (i == startIdx || i == endIdx)
                {
                    _cells[i] = CellType.None;
                    continue;
                }

                _cells[i] = (rng.NextDouble() < blockProbability) ? CellType.Block : CellType.None;
            }

            if (HasPathBfs(start, end))
            {
                ApplyAllCellVisuals();
                return true;
            }
        }

        // Revert if we couldn't find a valid layout
        Array.Copy(backup, _cells, _cells.Length);
        ApplyAllCellVisuals();
        return false;
    }
    
    private bool HasPathBfs(Vector2Int start, Vector2Int end)
    {
        if (IsBlocked(start.x, start.y) || IsBlocked(end.x, end.y))
            return false;

        int size = _width * _height;
        EnsureVisitBuffer(size);

        _visitStamp++;
        if (_visitStamp == int.MaxValue)
        {
            Array.Clear(_visitStampByIndex, 0, _visitStampByIndex.Length);
            _visitStamp = 1;
        }

        int startIndex = Idx(start.x, start.y);
        int endIndex = Idx(end.x, end.y);

        // Simple queue with int[] to avoid allocations
        var queue = new int[size];
        int head = 0, tail = 0;

        queue[tail++] = startIndex;
        _visitStampByIndex[startIndex] = _visitStamp;

        while (head < tail)
        {
            int cur = queue[head++];
            if (cur == endIndex) return true;

            int cx = cur % _width;
            int cy = cur / _width;

            // 4-neighborhood
            TryEnqueue(cx + 1, cy, cur, endIndex, queue, ref tail);
            TryEnqueue(cx - 1, cy, cur, endIndex, queue, ref tail);
            TryEnqueue(cx, cy + 1, cur, endIndex, queue, ref tail);
            TryEnqueue(cx, cy - 1, cur, endIndex, queue, ref tail);
        }
        
        

        return false;
    }
    
    private void TryEnqueue(int x, int y, int curIndex, int endIndex, int[] queue, ref int tail)
    {
        if (!IsInBounds(x, y)) return;
        if (IsBlocked(x, y)) return;

        int idx = Idx(x, y);
        if (_visitStampByIndex[idx] == _visitStamp) return;

        _visitStampByIndex[idx] = _visitStamp;
        queue[tail++] = idx;
    }

    private void EnsureVisitBuffer(int size)
    {
        if (_visitStampByIndex == null || _visitStampByIndex.Length != size)
        {
            _visitStampByIndex = new int[size];
            _visitStamp = 0;
        }
    }

    private void ApplyAllCellVisuals()
    {
        if (_grid == null || _grid.Length == 0) return;

        for (int y = 0; y < _height; y++)
        {
            for (int x = 0; x < _width; x++)
            {
                int idx = Idx(x, y);
                var tile = _grid[idx];
                if (tile == null) continue;

                if (_cells[idx] == CellType.Block) tile.SetColor(Color.black);
                else tile.ResetColor();
            }
        }

        // keep endpoints visible
        if (StartCell.HasValue && TryGetTile(StartCell.Value.x, StartCell.Value.y, out var s)) s.SetColor(Color.green);
        if (EndCell.HasValue && TryGetTile(EndCell.Value.x, EndCell.Value.y, out var e)) e.SetColor(Color.red);
    }
    
    private bool TryGetCellFromScreenPoint(Vector2 screenPosition, Camera uiCamera, out int x, out int y)
    {
        x = -1;
        y = -1;

        if (gridParent == null || _width <= 0 || _height <= 0)
            return false;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(gridParent, screenPosition, uiCamera, out Vector2 local))
            return false;

        float left = _startX - _side * 0.5f;
        float top = _startY + _side * 0.5f;

        float px = local.x - left;
        float py = top - local.y;

        if (px < 0f || py < 0f)
            return false;

        int ix = Mathf.FloorToInt(px / _stepX);
        int iy = Mathf.FloorToInt(py / _stepY);

        if (!IsInBounds(ix, iy))
            return false;

        float inCellX = px - ix * _stepX;
        float inCellY = py - iy * _stepY;

        if (inCellX > _side || inCellY > _side)
            return false;

        x = ix;
        y = iy;
        return true;
    }
    
    private int Idx(int x, int y) => y * _width + x;
    private bool IsInBounds(int x, int y) => x >= 0 && x < _width && y >= 0 && y < _height;
    public bool IsBlocked(int x, int y)
    {
        if (!IsInBounds(x, y)) return true;
        return _cells[Idx(x, y)] == CellType.Block;
    }
    public bool TryGetTile(int x, int y, out TileView tile)
    {
        tile = null;

        if (_grid == null || _grid.Length == 0) return false;
        if (!IsInBounds(x, y)) return false;

        tile = _grid[Idx(x, y)];
        return tile != null;
    }
    
    public void OnGridClicked(Vector2 screenPosition, Camera uiCamera)
    {
        if (!TryGetCellFromScreenPoint(screenPosition, uiCamera, out int x, out int y))
            return;

        if (CellClicked == null)
            ApplyStartEndSelection(x, y);

        CellClicked?.Invoke(x, y);
    }
    
    public void OnBlockClicked(Vector2 screenPosition, Camera uiCamera)
    {
        if (!TryGetCellFromScreenPoint(screenPosition, uiCamera, out int x, out int y))
            return;

        ToggleBlock(x, y);
    }
}