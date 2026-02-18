using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class InputController : MonoBehaviour, IPointerClickHandler
{
    [SerializeField] private GridManager gridManager;
    [SerializeField] private Canvas canvas;

    public void OnPointerClick(PointerEventData eventData)
    {
        if (gridManager == null) return;

        Camera uiCamera = GetUiCamera();

        if (eventData.button == PointerEventData.InputButton.Left)
            gridManager.OnGridClicked(eventData.position, uiCamera);

        if (eventData.button == PointerEventData.InputButton.Right)
            gridManager.OnBlockClicked(eventData.position, uiCamera);
    }

    private Camera GetUiCamera()
    {
        if (canvas == null) return null;
        if (canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
        return canvas.worldCamera;
    }
}