using UnityEngine;

public class GridManager : MonoBehaviour
{
    [SerializeField] public TileView gridView;
    [SerializeField] public RectTransform gridParent;
    [SerializeField] private GridDataSO gridData;

    private int _width;
    private int _height;
    private float _offset;
    private TileView[] _grid;
    void Start()
    {
        _width = gridData.Width;
        _height = gridData.Height;
        _offset = gridData.TileOffset;

        _grid = new TileView[_width * _height];

        var prefabRt = gridView.GetComponent<RectTransform>();
        Vector2 cellSize = prefabRt.rect.size;

        gridParent.anchorMin = gridParent.anchorMax = new Vector2(0.5f, 0.5f);
        gridParent.pivot = new Vector2(0.5f, 0.5f);

        float stepX = cellSize.x + _offset;
        float stepY = cellSize.y + _offset;

        float totalW = _width * cellSize.x + (_width - 1) * _offset;
        float totalH = _height * cellSize.y + (_height - 1) * _offset;

        float startX = -totalW * 0.5f + cellSize.x * 0.5f;
        float startY =  totalH * 0.5f - cellSize.y * 0.5f;

        for (int i = 0; i < _width; i++)
        {
            for (int j = 0; j < _height; j++)
            {
                var tile = Instantiate(gridView, gridParent);
                _grid[Idx(i, j)] = tile;

                var rt = tile.GetComponent<RectTransform>();

                float x = startX + i * stepX;
                float y = startY - j * stepY; 

                rt.anchoredPosition = new Vector2(x, y);
            }
        }
    }

    int Idx(int x, int y)
    {
        return y * _width + x;
    }
    
    public TileView GetTile(int x, int y)
    {
        return _grid[Idx(x, y)];
    }
}