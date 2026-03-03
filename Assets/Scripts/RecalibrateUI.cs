using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Creates the in-game touch HUD that replaces every keyboard shortcut with
/// on-screen buttons.  Layout (portrait phone):
///
///   ┌────────────────────────────────────┐
///   │                    [↻ Court] [↻ Racket] │  ← top-right
///   │                                          │
///   │                                          │
///   │                                          │
///   │  [Reset Ball]                            │  ← bottom-left (large)
///   └────────────────────────────────────┘
///
/// Setup: Attach to any GameObject (e.g. GameFlowManager).
///        References are auto-resolved if left null.
/// </summary>
public class RecalibrateUI : MonoBehaviour
{
    [Header("References (auto-resolved if null)")]
    [SerializeField] private PlaceTrackedImages imageTracker;

    [Header("Appearance")]
    [SerializeField] private int fontSize = 28;
    [SerializeField] private Color buttonColor = new Color(0.15f, 0.15f, 0.15f, 0.85f);
    [SerializeField] private Color accentColor = new Color(0.15f, 0.55f, 0.95f, 0.90f);
    [SerializeField] private Color textColor = Color.white;

    private GameObject _canvasGO;
    private PracticeBallController _ballController;

    private void Start()
    {
        // Auto-resolve references
        if (imageTracker == null)
            imageTracker = FindFirstObjectByType<PlaceTrackedImages>();

        _ballController = FindFirstObjectByType<PracticeBallController>();

        CreateUI();
    }

    private void CreateUI()
    {
        // ── Canvas ──────────────────────────────────────────────────────────
        _canvasGO = new GameObject("GameHUDCanvas");
        Canvas canvas = _canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;

        var scaler = _canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight = 0.5f;
        _canvasGO.AddComponent<GraphicRaycaster>();

        // ── RESET BALL — bottom-left, large and prominent ───────────────────
        CreateButton(
            parent: _canvasGO.transform,
            label: "Reset Ball",
            anchorMin: new Vector2(0.02f, 0.02f),
            anchorMax: new Vector2(0.38f, 0.09f),
            color: accentColor,
            textSize: 32,
            onClick: OnResetBall);

        // ── Recalibrate Court — top-right, upper ────────────────────────────
        CreateButton(
            parent: _canvasGO.transform,
            label: "\u21BB Court",
            anchorMin: new Vector2(0.52f, 0.92f),
            anchorMax: new Vector2(0.74f, 0.98f),
            color: buttonColor,
            textSize: fontSize,
            onClick: OnCourtRecalibrate);

        // ── Recalibrate Racket — top-right, next to court ───────────────────
        CreateButton(
            parent: _canvasGO.transform,
            label: "\u21BB Racket",
            anchorMin: new Vector2(0.76f, 0.92f),
            anchorMax: new Vector2(0.98f, 0.98f),
            color: buttonColor,
            textSize: fontSize,
            onClick: OnRacketRecalibrate);
    }

    // ── Button factory ──────────────────────────────────────────────────────

    private void CreateButton(
        Transform parent, string label,
        Vector2 anchorMin, Vector2 anchorMax,
        Color color, int textSize,
        UnityEngine.Events.UnityAction onClick)
    {
        var btnGO = new GameObject(label + "_Btn");
        btnGO.transform.SetParent(parent, false);

        Image img = btnGO.AddComponent<Image>();
        img.color = color;

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
        txt.fontSize = textSize;
        txt.color = textColor;
        txt.alignment = TextAnchor.MiddleCenter;
        txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        txt.fontStyle = FontStyle.Bold;
        txt.raycastTarget = false;

        RectTransform lr = labelGO.GetComponent<RectTransform>();
        lr.anchorMin = Vector2.zero;
        lr.anchorMax = Vector2.one;
        lr.sizeDelta = Vector2.zero;
    }

    // ── Callbacks ────────────────────────────────────────────────────────────

    private void OnResetBall()
    {
        Debug.Log("[GameHUD] Reset Ball pressed.");

        // Try the cached reference first, then search again (ball may have
        // been spawned after this script's Start).
        if (_ballController == null)
            _ballController = FindFirstObjectByType<PracticeBallController>();

        if (_ballController != null)
            _ballController.ResetBall();
        else
            Debug.LogWarning("[GameHUD] No PracticeBallController found in scene.");
    }

    private void OnCourtRecalibrate()
    {
        Debug.Log("[GameHUD] Court recalibrate pressed.");
        if (imageTracker != null)
            imageTracker.ResetCourt();
    }

    private void OnRacketRecalibrate()
    {
        Debug.Log("[GameHUD] Racket recalibrate pressed.");
        if (imageTracker != null)
            imageTracker.ResetRacket();
    }
}
