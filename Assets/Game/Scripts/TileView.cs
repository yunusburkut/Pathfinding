using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Image))]
public class TileView : MonoBehaviour
{
    [SerializeField] private Image image;

    private Color _defaultColor;

    private void Reset()
    {
        image = GetComponent<Image>();
    }

    private void Awake()
    {
        if (image == null)
            image = GetComponent<Image>();

        if (image != null)
            _defaultColor = image.color;
    }

    public void SetColor(Color color)
    {
        if (image == null) return;
        image.color = color;
    }

    public void ResetColor()
    {
        if (image == null) return;
        image.color = _defaultColor;
    }
}