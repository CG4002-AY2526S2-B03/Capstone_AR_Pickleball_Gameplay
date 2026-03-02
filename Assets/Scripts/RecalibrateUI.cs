using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Creates two small recalibration buttons in the top-right corner:
///   • "↻ Court"  — resets the court anchor so the next QR scan re-places it
///   • "↻ Racket" — destroys the current racket prefab so it re-spawns on next detection
///
/// Setup: Attach to any GameObject. Drag the PlaceTrackedImages reference
/// into the Inspector (or leave null — it will be auto-resolved).
/// </summary>
public class RecalibrateUI : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The PlaceTrackedImages component (usually on XR Origin).")]
    [SerializeField] private PlaceTrackedImages imageTracker;

    [Header("Appearance")]
    [SerializeField] private int fontSize = 22;
    [SerializeField] private Color buttonColor = new Color(0.15f, 0.15f, 0.15f, 0.75f);
    [SerializeField] private Color textColor = Color.white;

    private GameObject _canvasGO;

    private void Start()
    {
        if (imageTracker == null)
            imageTracker = FindFirstObjectByType<PlaceTrackedImages>();

        CreateUI();
    }

    private void CreateUI()
    {
        // ── Canvas ──
        _canvasGO = new GameObject("RecalibrateCanvas");
        Canvas canvas = _canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 90; // below PlayButtonUI (100)

        var scaler = _canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080); // landscape
        _canvasGO.AddComponent<GraphicRaycaster>();

        // ── Court Recalibrate Button (top-right) ──
        CreateButton(
            parent: _canvasGO.transform,
            label: "\u21BB Court",
            anchorMin: new Vector2(0.82f, 0.88f),
            anchorMax: new Vector2(0.99f, 0.98f),
            onClick: OnCourtRecalibrate);

        // ── Racket Recalibrate Button (below court button) ──
        CreateButton(
            parent: _canvasGO.transform,
            label: "\u21BB Racket",
            anchorMin: new Vector2(0.82f, 0.76f),
            anchorMax: new Vector2(0.99f, 0.86f),
            onClick: OnRacketRecalibrate);
    }

    private void CreateButton(Transform parent, string label, Vector2 anchorMin, Vector2 anchorMax, UnityEngine.Events.UnityAction onClick)
    {
        var btnGO = new GameObject(label + "Btn");
        btnGO.transform.SetParent(parent, false);

        Image img = btnGO.AddComponent<Image>();
        img.color = buttonColor;

        Button btn = btnGO.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(onClick);

        RectTransform rt = btnGO.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.sizeDelta = Vector2.zero;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        // Label
        var labelGO = new GameObject("Label");
        labelGO.transform.SetParent(btnGO.transform, false);
        Text txt = labelGO.AddComponent<Text>();
        txt.text = label;
        txt.fontSize = fontSize;
        txt.color = textColor;
        txt.alignment = TextAnchor.MiddleCenter;
        txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        txt.fontStyle = FontStyle.Bold;

        RectTransform lr = labelGO.GetComponent<RectTransform>();
        lr.anchorMin = Vector2.zero;
        lr.anchorMax = Vector2.one;
        lr.sizeDelta = Vector2.zero;
    }

    // ── Button callbacks ──

    private void OnCourtRecalibrate()
    {
        Debug.Log("[RecalibrateUI] Court recalibrate pressed.");
        if (imageTracker != null)
            imageTracker.ResetCourtAnchor();
    }

    private void OnRacketRecalibrate()
    {
        Debug.Log("[RecalibrateUI] Racket recalibrate pressed.");
        if (imageTracker != null)
            imageTracker.ResetRacket();
    }
}
