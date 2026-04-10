using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Screen-space overlay displaying score, set count, and rally state.
/// Attach to GameFlowManager alongside GameStateManager.
///
/// Layout (portrait phone):
///   ┌──────────────────────────────────────┐
///   │ Set 1 | Serve                        │  ← top-left: state
///   │ Player 0 - 0 Bot  Sets: 0-0         │  ← top-left: score
///   │                                      │
///   │            Net fault — Bot point      │  ← center: message (fades)
///   │                                      │
///   └──────────────────────────────────────┘
/// </summary>
public class ScoreboardUI : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Auto-found if left null.")]
    public GameStateManager gameState;
    public MqttController mqttController;
    public PaddleHitController paddleController;
    public ImuPaddleController imuController;
    public ARPlaneGameSpacePlacer gameSpacePlacer;

    [Header("Appearance")]
    public int fontSize = 36;
    public Color textColor = Color.white;
    public Color bgColor = new Color(0f, 0f, 0f, 0.5f);
    public Color messageColor = Color.yellow;

    private Text scoreText;
    private Text stateText;
    private Text modeText;
    private Text messageText;
    private Text sensorText;
    private GameObject canvasGO;
    private float messageTimer;

    private enum SensorHealth
    {
        Green,
        Yellow,
        Red
    }

    private void Start()
    {
        if (gameState == null)
            gameState = FindFirstObjectByType<GameStateManager>();
        ResolveRuntimeReferences();

        CreateUI();

        if (gameState != null)
        {
            gameState.OnScoreChanged += UpdateScoreDisplay;
            gameState.OnStateChanged += UpdateStateDisplay;
            gameState.OnMessage += ShowMessage;
            gameState.OnModeChanged += UpdateModeDisplay;
            UpdateScoreDisplay();
            UpdateStateDisplay(gameState.State);
            UpdateModeDisplay(gameState.Mode);
        }
    }

    private void Update()
    {
        if (messageTimer > 0f)
        {
            messageTimer -= Time.deltaTime;
            if (messageTimer <= 0f && messageText != null)
                messageText.text = "";
        }

        UpdateSensorDisplay();
    }

    private void CreateUI()
    {
        // ── Canvas: WorldSpace so it renders in both stereo viewports ────────
        canvasGO = new GameObject("ScoreboardCanvas");
        canvasGO.AddComponent<Canvas>();
        StereoscopicAR.SetupWorldSpaceCanvas(canvasGO, sortingOrder: 100,
            width: 1080, height: 600);

        // ── Score panel (top-left region of canvas) ─────────────────────────
        var panelGO = new GameObject("ScorePanel");
        panelGO.transform.SetParent(canvasGO.transform, false);
        Image panelImg = panelGO.AddComponent<Image>();
        panelImg.color = bgColor;
        panelImg.raycastTarget = false;
        RectTransform panelRT = panelGO.GetComponent<RectTransform>();
        panelRT.anchorMin = new Vector2(0.02f, 0.75f);
        panelRT.anchorMax = new Vector2(0.60f, 0.98f);
        panelRT.sizeDelta = Vector2.zero;

        // State label (top row of panel)
        stateText = CreateLabel(panelGO.transform, "StateText",
            new Vector2(0.05f, 0.55f), new Vector2(0.95f, 1f),
            24, new Color(0.85f, 0.85f, 0.85f));

        // Score label (bottom row of panel)
        scoreText = CreateLabel(panelGO.transform, "ScoreText",
            new Vector2(0.05f, 0f), new Vector2(0.95f, 0.55f),
            fontSize, textColor);

        // ── Mode label (top-right) ─────────────────────────────────────────────
        var modePanelGO = new GameObject("ModePanel");
        modePanelGO.transform.SetParent(canvasGO.transform, false);
        Image modePanelImg = modePanelGO.AddComponent<Image>();
        modePanelImg.color = bgColor;
        modePanelImg.raycastTarget = false;
        RectTransform modePanelRT = modePanelGO.GetComponent<RectTransform>();
        modePanelRT.anchorMin = new Vector2(0.65f, 0.85f);
        modePanelRT.anchorMax = new Vector2(0.98f, 0.98f);
        modePanelRT.sizeDelta = Vector2.zero;

        modeText = CreateLabel(modePanelGO.transform, "ModeText",
            new Vector2(0.05f, 0f), new Vector2(0.95f, 1f),
            24, new Color(0.6f, 1f, 0.6f));
        modeText.alignment = TextAnchor.MiddleCenter;

        var sensorPanelGO = new GameObject("SensorPanel");
        sensorPanelGO.transform.SetParent(canvasGO.transform, false);
        Image sensorPanelImg = sensorPanelGO.AddComponent<Image>();
        sensorPanelImg.color = bgColor;
        sensorPanelImg.raycastTarget = false;
        RectTransform sensorPanelRT = sensorPanelGO.GetComponent<RectTransform>();
        sensorPanelRT.anchorMin = new Vector2(0.65f, 0.58f);
        sensorPanelRT.anchorMax = new Vector2(0.98f, 0.83f);
        sensorPanelRT.sizeDelta = Vector2.zero;

        sensorText = CreateLabel(sensorPanelGO.transform, "SensorText",
            new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.95f),
            20, textColor);
        sensorText.alignment = TextAnchor.UpperLeft;
        sensorText.supportRichText = true;
        sensorText.text = "";
        sensorPanelGO.SetActive(false);

        // ── Center message (point awarded, set won, etc.) ────────────────────
        messageText = CreateLabel(canvasGO.transform, "MessageText",
            new Vector2(0.05f, 0.40f), new Vector2(0.95f, 0.55f),
            48, messageColor);
        messageText.alignment = TextAnchor.MiddleCenter;
        messageText.fontStyle = FontStyle.Bold;
        messageText.text = "";
    }

    private Text CreateLabel(Transform parent, string name,
        Vector2 anchorMin, Vector2 anchorMax, int size, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Text txt = go.AddComponent<Text>();
        txt.fontSize = size;
        txt.color = color;
        txt.alignment = TextAnchor.MiddleLeft;
        txt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        txt.raycastTarget = false;
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.sizeDelta = Vector2.zero;
        return txt;
    }

    private void UpdateScoreDisplay()
    {
        if (scoreText == null || gameState == null) return;
        if (gameState.Mode == GameStateManager.GameMode.Tutorial)
        {
            scoreText.text = "Practice Mode — no scoring";
        }
        else if (gameState.Mode == GameStateManager.GameMode.GodMode)
        {
            scoreText.text = "God Mode — no scoring";
        }
        else
        {
            scoreText.text = $"Player {gameState.PlayerScore} - {gameState.BotScore} Bot  |  Sets: {gameState.PlayerSets}-{gameState.BotSets}";
        }
    }

    private void UpdateStateDisplay(GameStateManager.RallyState state)
    {
        if (stateText == null || gameState == null) return;
        stateText.text = state switch
        {
            GameStateManager.RallyState.WaitingToServe => $"Set {gameState.CurrentSet}  |  Serve",
            GameStateManager.RallyState.InPlay => "Rally",
            GameStateManager.RallyState.PointScored => "Point!",
            GameStateManager.RallyState.MatchOver => "Match Over",
            _ => ""
        };
    }

    private void UpdateModeDisplay(GameStateManager.GameMode mode)
    {
        if (modeText == null) return;
        modeText.text = mode switch
        {
            GameStateManager.GameMode.Normal   => "Normal",
            GameStateManager.GameMode.Tutorial => "Tutorial",
            GameStateManager.GameMode.GodMode  => "God Mode",
            _ => ""
        };
    }

    private void ShowMessage(string msg)
    {
        if (messageText == null) return;
        messageText.text = msg;
        messageTimer = 2f;
    }

    private void ResolveRuntimeReferences()
    {
        if (mqttController == null)
            mqttController = FindFirstObjectByType<MqttController>();
        if (paddleController == null)
            paddleController = FindFirstObjectByType<PaddleHitController>();
        if (imuController == null)
            imuController = FindFirstObjectByType<ImuPaddleController>();
        if (gameSpacePlacer == null)
            gameSpacePlacer = FindFirstObjectByType<ARPlaneGameSpacePlacer>();
    }

    private void UpdateSensorDisplay()
    {
        if (sensorText == null || gameState == null)
            return;

        ResolveRuntimeReferences();

        bool show = gameState.Mode == GameStateManager.GameMode.GodMode;
        if (sensorText.transform.parent != null)
            sensorText.transform.parent.gameObject.SetActive(show);

        if (!show)
            return;

        sensorText.text =
            "<b>Sensors</b>\n" +
            FormatSensorLine("IMU", GetImuStatus(), GetImuDetail()) + "\n" +
            FormatSensorLine("QR", GetQrStatus(), GetQrDetail()) + "\n" +
            FormatSensorLine("UWB", GetUwbStatus(), GetUwbDetail()) + "\n" +
            FormatSensorLine("ARKit", GetArkitStatus(), GetArkitDetail()) + "\n" +
            $"<color=#D0D0D0>Mode  {GetControlModeDetail()}</color>";
    }

    private SensorHealth GetImuStatus()
    {
        if (imuController == null || !imuController.IsActive || imuController.LastImuPacketTime < 0f)
            return SensorHealth.Red;

        float age = Time.time - imuController.LastImuPacketTime;
        if (age <= 0.1f)
            return SensorHealth.Green;
        if (age <= 1f)
            return SensorHealth.Yellow;
        return SensorHealth.Red;
    }

    private string GetImuDetail()
    {
        if (imuController == null || imuController.LastImuPacketTime < 0f)
            return "missing";

        float ageMs = Mathf.Max(0f, (Time.time - imuController.LastImuPacketTime) * 1000f);
        if (!imuController.IsActive)
            return "inactive";
        if (ageMs <= 100f)
            return $"{ageMs:0}ms";
        if (ageMs <= 1000f)
            return $"stale {ageMs:0}ms";
        return "lost";
    }

    private SensorHealth GetQrStatus()
    {
        if (paddleController == null)
            return SensorHealth.Red;

        bool trackedFresh = paddleController.qrActivelyTracking
            && paddleController.lastQrTrackingUpdateTime > 0f
            && Time.time - paddleController.lastQrTrackingUpdateTime <= Mathf.Max(0.1f, paddleController.qrTrackingTimeout);
        if (trackedFresh)
            return SensorHealth.Green;

        bool hasTrackedPaddle = paddleController.qrTrackedRacket != null || paddleController.lastQrTrackingUpdateTime > 0f;
        return hasTrackedPaddle ? SensorHealth.Yellow : SensorHealth.Red;
    }

    private string GetQrDetail()
    {
        if (paddleController == null)
            return "missing";

        bool trackedFresh = paddleController.qrActivelyTracking
            && paddleController.lastQrTrackingUpdateTime > 0f
            && Time.time - paddleController.lastQrTrackingUpdateTime <= Mathf.Max(0.1f, paddleController.qrTrackingTimeout);
        if (trackedFresh)
            return "tracking";

        bool hasTrackedPaddle = paddleController.qrTrackedRacket != null || paddleController.lastQrTrackingUpdateTime > 0f;
        return hasTrackedPaddle ? "fallback" : "missing";
    }

    private SensorHealth GetUwbStatus()
    {
        if (mqttController == null || mqttController.LastUwbReceiveTime < 0f || !mqttController.HasUwbFix)
            return SensorHealth.Red;

        float age = Time.time - mqttController.LastUwbReceiveTime;
        if (!mqttController.IsUwbTimedOut && age <= 0.1f)
            return SensorHealth.Green;
        return SensorHealth.Yellow;
    }

    private string GetUwbDetail()
    {
        if (mqttController == null || mqttController.LastUwbReceiveTime < 0f || !mqttController.HasUwbFix)
            return "missing";

        float ageMs = Mathf.Max(0f, (Time.time - mqttController.LastUwbReceiveTime) * 1000f);
        if (!mqttController.IsUwbTimedOut && ageMs <= 100f)
            return $"{ageMs:0}ms";
        return "fallback";
    }

    private SensorHealth GetArkitStatus()
    {
        if (gameSpacePlacer == null || !gameSpacePlacer.IsPlaced)
            return SensorHealth.Red;

        if (mqttController != null && mqttController.IsArkitFallbackActive)
            return SensorHealth.Yellow;

        return SensorHealth.Green;
    }

    private string GetArkitDetail()
    {
        if (gameSpacePlacer == null || !gameSpacePlacer.IsPlaced)
            return "no anchor";

        if (mqttController != null && mqttController.IsArkitFallbackActive)
            return "fallback";

        return "locked";
    }

    private string GetControlModeDetail()
    {
        if (paddleController == null)
            return "unknown";

        return paddleController.CurrentMode;
    }

    private static string FormatSensorLine(string label, SensorHealth health, string detail)
    {
        return $"<color={GetSensorColorHex(health)}>{label,-6}{detail}</color>";
    }

    private static string GetSensorColorHex(SensorHealth health)
    {
        return health switch
        {
            SensorHealth.Green => "#6EFF7A",
            SensorHealth.Yellow => "#FFD966",
            _ => "#FF6B6B"
        };
    }

    private void OnDestroy()
    {
        if (gameState != null)
        {
            gameState.OnScoreChanged -= UpdateScoreDisplay;
            gameState.OnStateChanged -= UpdateStateDisplay;
            gameState.OnMessage -= ShowMessage;
            gameState.OnModeChanged -= UpdateModeDisplay;
        }
    }
}
