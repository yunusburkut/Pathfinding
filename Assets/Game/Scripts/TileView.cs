using UnityEngine;
using UnityEngine.UI;

public class TileView : MonoBehaviour
{
    [SerializeField] private SpriteRenderer image;

    public void SetColor(Color color)
    {
        image.color = color;
    }
}
