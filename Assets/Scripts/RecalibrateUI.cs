using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Creates the in-game touch HUD that replaces every keyboard shortcut with
/// on-screen buttons.  Layout (portrait phone):
///
///   ┌────────────────────────────────────┐
///   │                         [↻ Racket]       │  ← top-right row 1
///   │                         [↻ Court ]       │  ← top-right row 2
///   │                                          │
///   │                                          │
///   │                          [Reset Ball]    │  ← bottom-right (large)
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

        // ── RESET BALL — bottom-right, large and prominent ────────────────
        CreateButton(
            parent: _canvasGO.transform,
            label: "Reset Ball",
            anchorMin: new Vector2(0.62f, 0.02f),
            anchorMax: new Vector2(0.98f, 0.09f),
            color: accentColor,
            textSize: 32,
            onClick: OnResetBall);

        // ── Recalibrate Racket — top-right, row 1 ──────────────────────────
        CreateButton(
            parent: _canvasGO.transform,
            label: "\u21BB Racket",
            anchorMin: new Vector2(0.72f, 0.92f),
            anchorMax: new Vector2(0.98f, 0.98f),
            color: buttonColor,
            textSize: fontSize,
            onClick: OnRacketRecalibrate);

        // ── Recalibrate Court — top-right, row 2 (below racket) ────────────
        CreateButton(
            parent: _canvasGO.transform,
            label: "\u21BB Court",
            anchorMin: new Vector2(0.72f, 0.85f),
            anchorMax: new Vector2(0.98f, 0.91f),
            color: buttonColor,
            textSize: fontSize,
            onClick: OnCourtRecalibrate);
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

        // Always re-find — the cached reference can become stale
        // (Unity-null) if the ball object was recycled or its parent
        // (GameSpaceRoot) was deactivated by the AR anchor system.
        _ballController = FindFirstObjectByType<PracticeBallController>();

        // FindFirstObjectByType skips inactive objects. Ball2 can become
        // inactive when GameSpaceRoot is deactivated during court re-placement,
        // so fall back to a search that includes inactive objects.
        if (_ballController == null)
        {
            foreach (var bc in Resources.FindObjectsOfTypeAll<PracticeBallController>())
            {
                if (bc.gameObject.scene.isLoaded)
                {
                    _ballController = bc;
                    break;
                }
            }
        }

        if (_ballController != null)
        {
            // Re-enable the GameObject in case it was deactivated.
            if (!_ballController.gameObject.activeInHierarchy)
                _ballController.gameObject.SetActive(true);

            _ballController.ResetBall();
        }
        else
        {
            Debug.LogWarning("[GameHUD] No PracticeBallController found in scene.");
        }
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
