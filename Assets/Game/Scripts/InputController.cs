using UnityEngine;
using UnityEngine.UI;

public class InputController : MonoBehaviour
{
    
    [SerializeField] private GridManager gridManager;
    [SerializeField] private Button changeColorButton;
    [SerializeField] private Vector2 gridLoc;
    
    void Start()
    {
        changeColorButton.onClick.AddListener(ChangeColor);
    }

    private void ChangeColor()
    {
        TileView tv = gridManager.GetTile((int)gridLoc.x, (int)gridLoc.y);
        tv.SetColor(Color.red);
    }
}
