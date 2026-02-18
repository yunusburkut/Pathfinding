using UnityEngine;

public struct GridData
{
    [SerializeField] private int width;
    [SerializeField] private int height;
    [SerializeField] private int tileOffset;

    public int Width => width;
    public int Height => height;
    public int TileOffset => tileOffset;
}