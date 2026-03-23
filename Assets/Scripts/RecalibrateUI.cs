using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// In-game HUD with 4 buttons that map 1:1 to the IMU hardware buttons.
/// Each action can be triggered by tapping on screen OR pressing the
/// corresponding physical button on the ESP32 controller.
///
/// Layout (portrait phone):
///   ┌──────────────────────────────────────┐
///   │                    [↻ Court + Paddle] │  ← top-right row 1 (btn 3)
///   │                    [  Reset Game    ] │  ← top-right row 2 (btn 4)
///   │                                      │
///   │                                      │
///   │  [Start / Pause]        [Reset Ball] │  ← bottom (btn 1, btn 2)
///   └──────────────────────────────────────┘
///
/// Button mapping (matches ESP32 buttons 1-4):
///   1 (up)     → Start / Pause / Resume
///   2 (down)   → Reset Ball (drop 3m, 0.5m in front of camera)
///   3 (return) → Reset Court + Paddle (QR recalibration)
///   4 (select) → Reset Game (full gameplay reset)
/// </summary>
public class RecalibrateUI : MonoBehaviour
{
    [Header("References (auto-resolved if null)")]
    [SerializeField] private GameStateManager gameState;
    [SerializeField] private PracticeBallController ballController;
    [SerializeField] private PlaceTrackedImages imageTracker;

    [Header("Appearance")]
    [SerializeField] private int fontSize = 28;
    [SerializeField] private Color buttonColor = new Color(0.15f, 0.15f, 0.15f, 0.85f);
    [SerializeField] private Color accentColor = new Color(0.15f, 0.55f, 0.95f, 0.90f);
    [SerializeField] private Color textColor = Color.white;

    private GameObject _canvasGO;
    private Text _btn1Label;

    private void Start()
    {
        if (gameState == null)
            gameState = FindFirstObjectByType<GameStateManager>();
        if (ballController == null)
            ballController = FindFirstObjectByType<PracticeBallController>();
        if (imageTracker == null)
            imageTracker = FindFirstObjectByType<PlaceTrackedImages>();

        CreateUI();
    }

    private void Update()
    {
        UpdateButton1Label();
    }

    private void CreateUI()
    {
        _canvasGO = new GameObject("GameHUDCanvas");
        Canvas canvas = _canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;

        var scaler = _canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight = 0.5f;
        _canvasGO.AddComponent<GraphicRaycaster>();

        // Button 1: Start / Pause / Resume — bottom-left
        _btn1Label = CreateButton(
            parent: _canvasGO.transform,
            label: "Start",
            anchorMin: new Vector2(0.02f, 0.02f),
            anchorMax: new Vector2(0.38f, 0.09f),
            color: accentColor,
            textSize: 32,
            onClick: OnButton1_StartPauseResume);

        // Button 2: Reset Ball — bottom-right
        CreateButton(
            parent: _canvasGO.transform,
            label: "Reset Ball",
            anchorMin: new Vector2(0.62f, 0.02f),
            anchorMax: new Vector2(0.98f, 0.09f),
            color: accentColor,
            textSize: 32,
            onClick: OnButton2_ResetBall);

        // Button 3: Reset Court + Paddle — top-right row 1
        CreateButton(
            parent: _canvasGO.transform,
            label: "\u21BB Court + Paddle",
            anchorMin: new Vector2(0.58f, 0.92f),
            anchorMax: new Vector2(0.98f, 0.98f),
            color: buttonColor,
            textSize: fontSize,
            onClick: OnButton3_ResetCourtPaddle);

        // Button 4: Reset Game — top-right row 2
        CreateButton(
            parent: _canvasGO.transform,
            label: "Reset Game",
            anchorMin: new Vector2(0.58f, 0.85f),
            anchorMax: new Vector2(0.98f, 0.91f),
            color: buttonColor,
            textSize: fontSize,
            onClick: OnButton4_ResetGame);
    }

    // ── Button factory ──────────────────────────────────────────────────────

    private Text CreateButton(
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

        return txt;
    }

    // ── Dynamic label ───────────────────────────────────────────────────────

    private void UpdateButton1Label()
    {
        if (_btn1Label == null || gameState == null) return;

        if (!gameState.IsStarted)
            _btn1Label.text = "Start";
        else if (gameState.IsPaused)
            _btn1Label.text = "Resume";
        else
            _btn1Label.text = "Pause";
    }

    // ── Button actions (public so MqttController can also call them) ────────

    /// <summary>Button 1: Start game (first press), then Pause / Resume.</summary>
    public void OnButton1_StartPauseResume()
    {
        if (gameState == null) return;
        gameState.StartOrTogglePause();
        Debug.Log("[GameHUD] Button 1: Start / Pause / Resume");
    }

    /// <summary>Button 2: Drop ball 3m high, 0.5m in front of camera.</summary>
    public void OnButton2_ResetBall()
    {
        Debug.Log("[GameHUD] Button 2: Reset Ball");
        EnsureBallController();
        if (ballController != null)
            ballController.DropBallInFrontOfCamera();
    }

    /// <summary>Button 3: Reset court and paddle QR tracking.</summary>
    public void OnButton3_ResetCourtPaddle()
    {
        Debug.Log("[GameHUD] Button 3: Reset Court + Paddle");
        if (imageTracker == null)
            imageTracker = FindFirstObjectByType<PlaceTrackedImages>();
        if (imageTracker != null)
        {
            imageTracker.ResetCourt();
            imageTracker.ResetRacket();
        }
    }

    /// <summary>Button 4: Full gameplay reset (scores, ball, state).</summary>
    public void OnButton4_ResetGame()
    {
        Debug.Log("[GameHUD] Button 4: Reset Game");
        if (gameState == null)
            gameState = FindFirstObjectByType<GameStateManager>();
        if (gameState != null)
            gameState.ResetGameplay();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void EnsureBallController()
    {
        ballController = FindFirstObjectByType<PracticeBallController>();
        if (ballController == null)
        {
            foreach (var bc in Resources.FindObjectsOfTypeAll<PracticeBallController>())
            {
                if (bc.gameObject.scene.isLoaded)
                {
                    ballController = bc;
                    break;
                }
            }
        }
        if (ballController != null && !ballController.gameObject.activeInHierarchy)
            ballController.gameObject.SetActive(true);
    }
}
