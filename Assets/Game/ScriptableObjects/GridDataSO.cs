using UnityEngine;

[CreateAssetMenu(menuName = "Game/Grid Data", fileName = "GridDataSO")]
public sealed class GridDataSO : ScriptableObject
{
    [SerializeField] private int width = 8;
    [SerializeField] private int height = 8;
    [SerializeField] private float tileOffset = 8;

    public int Width => width;
    public int Height => height;
    public float TileOffset => tileOffset;
}