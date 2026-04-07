using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using Newtonsoft.Json;

public class MqttController : MonoBehaviour
{
    public string nameController = "MqttController";
    public string unityPublishTopic = "/playerBall";

    public MqttReceiver _eventSender;

    [Header("Court Reference")]
    [Tooltip("GameSpaceRoot transform. Used to convert ball coords to/from court-local space for the AI model.")]
    public Transform gameSpaceRoot;

    [Header("Game Component References")]
    [Tooltip("IMU paddle controller for hardware-driven racket.")]
    public ImuPaddleController imuPaddleController;

    [Tooltip("Bot hit controller for ML-driven opponent.")]
    public BotHitController botHitController;

    [Tooltip("Ball controller for serve detection.")]
    public PracticeBallController ballController;

    [Tooltip("Game state for start/pause/resume/reset.")]
    public GameStateManager gameState;

    [Header("Player Tracking")]
    [Tooltip("A GameObject (e.g. capsule/cylinder) placed on the court to represent the physical player. " +
             "Drag a prefab or scene object here.")]
    public Transform playerMarker;

    [Tooltip("Smooth factor for position lerp (0=static, 1=snap). 0.15 is a good default.")]
    [Range(0f, 1f)]
    public float playerPositionSmoothing = 0.15f;

    [Tooltip("Seconds without a UWB packet before falling back to camera position.")]
    public float uwbTimeoutSeconds = 2f;

    [Header("UWB Coordinate Mapping")]
    [Tooltip("The net Z position in court-local space. UWB origin is at the net centre, " +
             "so courtLocal.z = uwbNetZ - uwb.y (when UWB y increases toward player from net). " +
             "Must match CourtBoundarySetup.netLocalPosition.z.")]
    public float uwbNetZ = 5.4f;

    [Tooltip("Set to -1 if UWB y increases toward the player from the net (most common). " +
             "Set to +1 if UWB y increases toward the bot side.")]
    public float uwbYSign = -1f;

    [Header("UWB Drift Correction")]
    [Tooltip("Smoothly corrects GameSpaceRoot position using UWB head tracking to counter AR camera drift.")]
    public bool enableDriftCorrection = false;

    [Tooltip("Correction speed (0.1=very slow, 1.0=fast). Lower = smoother but slower to converge.")]
    [Range(0.05f, 2f)]
    public float driftCorrectionSpeed = 0.3f;

    [Tooltip("Minimum drift (metres) before correction kicks in. Filters UWB noise.")]
    public float driftMinThreshold = 0.05f;

    [Tooltip("Maximum correction per frame (metres). Prevents jumps from UWB outliers.")]
    public float driftMaxStepPerFrame = 0.02f;

    // Internally tracked target so we can lerp in Update
    private Vector3 _targetPlayerWorldPos;
    private bool _hasPlayerPosition = false;
    private float _lastUwbReceiveTime = -1f;
    private bool _uwbTimedOut = false;

    // UWB court-local position for drift correction
    private Vector3 _uwbCourtLocal;
    private bool _hasUwbCourtLocal;
    private float _lastDriftLogTime;

    [Header("Debug Display")]
    [Tooltip("Existing TMP 3D text in scene for displaying live MQTT data.")]
    public TextMeshPro debugText;

    // ── Cached display strings — one per topic (composed into debugText) ─────
    private string _connLine    = "MQTT: connecting...";
    private string _imuLine     = "/paddle IMU: waiting...";
    private string _btnLine     = "";
    private string _pubLine     = "";
    private string _recvLine    = "";
    private string _posLine     = "";
    private string _signalLine  = "";

    // ── Network status banner ──────────────────────────────────────────────────
    private GameObject bannerCanvasGO;
    private Text bannerText;

    /// <summary>True when MQTT is connected and operational.</summary>
    public bool IsConnected => _eventSender != null && _eventSender.isConnected;

    void Start()
    {
        if (gameState == null)
            gameState = FindFirstObjectByType<GameStateManager>();
        if (imuPaddleController == null)
            imuPaddleController = FindFirstObjectByType<ImuPaddleController>();
        if (botHitController == null)
            botHitController = FindFirstObjectByType<BotHitController>();
        if (ballController == null)
            ballController = FindFirstObjectByType<PracticeBallController>();
        if (debugText == null)
            debugText = FindFirstObjectByType<TextMeshPro>();

        // Set initial text on the existing TMP
        RefreshDebugText();

        if (_eventSender == null)
        {
            _connLine = "MQTT: no MqttReceiver ref";
            RefreshDebugText();
            ShowBanner("MQTT not configured — running in offline mode");
            Debug.LogWarning("[MqttController] MqttReceiver reference is null. Game will run without network.");
            return;
        }

        _connLine = $"MQTT: connecting to {_eventSender.brokerAddress}:{_eventSender.brokerPort}...";
        RefreshDebugText();

        try
        {
            _eventSender.OnMessageArrived += OnMessageArrivedHandler;
            _eventSender.OnConnectionSucceeded += OnConnectionStatusChanged;
            _eventSender.ConnectionFailed += OnMqttConnectionFailed;
        }
        catch (Exception e)
        {
            _connLine = $"MQTT: event subscribe error: {e.Message}";
            RefreshDebugText();
            ShowBanner($"MQTT setup error — running in offline mode");
            Debug.LogError($"[MqttController] Failed to subscribe to MQTT events: {e.Message}");
            return;
        }
        // Show connecting status; will be cleared on success or updated on failure
        ShowBanner("Connecting to MQTT broker...", Color.yellow);

#if UNITY_EDITOR
        StartCoroutine(TestPublish());
#endif
    }

    private void OnConnectionStatusChanged(bool connected)
    {
        if (connected)
        {
            HideBanner();
            _connLine = "MQTT: connected";
            Debug.Log("[MqttController] MQTT connected successfully.");
        }
        else
        {
            _connLine = "MQTT: disconnected";
            ShowBanner("MQTT disconnected — running in offline mode");
        }
        RefreshDebugText();
    }

    private void OnMqttConnectionFailed()
    {
        string err = _eventSender != null ? _eventSender.LastConnectionError : "unknown";
        _connLine = $"MQTT FAIL: {err}";
        ShowBanner("MQTT connection failed — running in offline mode");
        Debug.LogWarning($"[MqttController] MQTT connection failed: {err}");
        RefreshDebugText();
    }

#if UNITY_EDITOR
    IEnumerator TestPublish()
    {
        yield return new WaitForSeconds(3f);

        if (!IsConnected)
        {
            Debug.Log("[TEST] Skipping test publish — not connected.");
            yield break;
        }

        Vector3 dummyPos = new Vector3(1.0f, 2.0f, 3.0f);
        Vector3 dummyVel = new Vector3(0.5f, 0.6f, 0.7f);
        PublishPlayerBall(dummyPos, dummyVel);
        Debug.Log("[TEST] Dummy playerBall payload published");
    }
#endif

    private void OnMessageArrivedHandler(string topic, string newMsg)
    {
        if (topic == "/opponentBall")
        {
            HandleOpponentBall(newMsg);
        }
        else if (topic == "/paddle")
        {
            HandlePaddle(newMsg);
        }
        else if (topic == "/playerPosition")
        {
            HandlePlayerPosition(newMsg);
        }
        else if (topic == "/system/signal")
        {
            Debug.Log($"[system/signal] {newMsg}");
            _signalLine = $"/system/signal: {newMsg}";
            RefreshDebugText();
        }
    }

    // ── /opponentBall handler ───────────────────────────────────────────────────

    private void HandleOpponentBall(string json)
    {
        OpponentBallPayload data;
        try { data = JsonConvert.DeserializeObject<OpponentBallPayload>(json); }
        catch (Exception e)
        {
            Debug.LogWarning($"[MqttController] Failed to parse /opponentBall: {e.Message}");
            return;
        }

        if (data == null || data.position == null || data.velocity == null)
        {
            Debug.LogWarning("[MqttController] /opponentBall missing required fields.");
            return;
        }

        // Clamp to 6 AI classes (0=Drive 1=Drop 2=Dink 3=Lob 4=SpeedUp 5=HandBattle)
        if (data.returnSwingType < 0 || data.returnSwingType > 5)
        {
            Debug.LogWarning($"[MqttController] Invalid returnSwingType={data.returnSwingType}, clamping to 0.");
            data.returnSwingType = 0;
        }

        // AI court-local → Unity world
        // AI convention:    x=right, y=depth(forward), z=height(up)
        // Unity convention: x=right, y=up,             z=forward
        Vector3 courtLocalPos = new Vector3(data.position.x, data.position.z, data.position.y);  // y↔z swap
        Vector3 courtLocalVel = new Vector3(data.velocity.vx, data.velocity.vz, data.velocity.vy); // y↔z swap

        Vector3 worldPos = gameSpaceRoot != null
            ? gameSpaceRoot.TransformPoint(courtLocalPos) : courtLocalPos;
        Vector3 worldVel = gameSpaceRoot != null
            ? gameSpaceRoot.TransformDirection(courtLocalVel) : courtLocalVel;

        Debug.Log($"[opponentBall] courtPos=({courtLocalPos.x:F2},{courtLocalPos.y:F2},{courtLocalPos.z:F2})" +
                  $" worldPos=({worldPos.x:F2},{worldPos.y:F2},{worldPos.z:F2})" +
                  $" swing={data.returnSwingType}");

        _recvLine = $"RECV pos:({data.position.x:F2},{data.position.y:F2},{data.position.z:F2})" +
                    $" vel:({data.velocity.vx:F2},{data.velocity.vy:F2},{data.velocity.vz:F2})" +
                    $" shot:{(ShotType)data.returnSwingType}";
        RefreshDebugText();

        if (botHitController != null)
            botHitController.SetMLPrediction(worldPos, worldVel, data.returnSwingType);
    }

    // ── /paddle handler ─────────────────────────────────────────────────────────
    // ESP32 sends two different JSON schemas on /paddle:
    //   type="imu"    → { type, position: {roll,pitch,yaw}, velocity: {x,y,z} }
    //   type="button" → { type, button: 1-4 }
    // We route by packet type and remap ESP32 field names to PaddlePayload.

    private void HandlePaddle(string json)
    {
        Esp32Packet raw;
        try
        {
            raw = JsonConvert.DeserializeObject<Esp32Packet>(json);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[MqttController] Failed to parse /paddle: {e.Message}");
            return;
        }

        if (raw == null) return;

        switch (raw.type)
        {
            case "imu":    HandleImuPacket(raw);    break;
            case "button": HandleButtonPacket(raw); break;
            default:
                Debug.LogWarning($"[MqttController] Unknown /paddle type '{raw.type}'");
                break;
        }
    }

    private void HandleImuPacket(Esp32Packet raw)
    {
        if (raw.position == null)
        {
            Debug.LogWarning("[MqttController] IMU packet missing position field.");
            return;
        }

        // Remap: ESP32 "position" → PaddlePayload "orientation"
        //        ESP32 "velocity" → PaddlePayload "linearVelocity"
        //        angularVelocity not sent by ESP32 → default to zero
        PaddlePayload payload = new PaddlePayload
        {
            orientation     = raw.position,
            linearVelocity  = raw.velocity ?? new Vec3Payload(),
            angularVelocity = new Vec3Payload(),
            isServeAction   = false,
            buttons         = null
        };

        Debug.Log($"[paddle/imu] pitch={payload.orientation.pitch:F1} " +
                  $"yaw={payload.orientation.yaw:F1} " +
                  $"linVel=({payload.linearVelocity.x:F2},{payload.linearVelocity.y:F2},{payload.linearVelocity.z:F2})");

        if (imuPaddleController != null)
            imuPaddleController.SetPayload(payload);

        _imuLine = $"IMU  P:{payload.orientation.pitch:F1} Y:{payload.orientation.yaw:F1} R:{payload.orientation.roll:F1}" +
                   $"  V:({payload.linearVelocity.x:F2},{payload.linearVelocity.y:F2},{payload.linearVelocity.z:F2})";
        RefreshDebugText();
    }

    private void HandleButtonPacket(Esp32Packet raw)
    {
        // ESP32 sends one packet per button press (edge-triggered).
        // Buttons map 1:1 to RecalibrateUI screen buttons.
        Debug.Log($"[paddle/button] btn={raw.button}");
        string btnLabel = raw.button switch
        {
            1 => "Start/Pause",
            2 => "Reset+Calibrate",
            3 => "Reset Ball",
            4 => "Mode/Reset",
            _ => $"Unknown({raw.button})"
        };
        _btnLine = $"/paddle BTN: {raw.button} ({btnLabel})";
        RefreshDebugText();

        switch (raw.button)
        {
            case 1: // Start / Pause / Resume
                if (gameState != null)
                    gameState.StartOrTogglePause();
                Debug.Log("[MqttController] Button 1: Start / Pause / Resume");
                break;

            case 2: // Full reset + calibrate position and paddle
                // Reset gameplay (scores, state, ball)
                if (gameState != null)
                    gameState.ResetGameplay();

                // Force ball back
                var calBall = FindBallController();
                if (calBall != null)
                {
                    if (!calBall.gameObject.activeInHierarchy)
                        calBall.gameObject.SetActive(true);
                    calBall.ResetBall();
                }

                // Clear stale ball cache
                var calPaddle = FindFirstObjectByType<PaddleHitController>();
                if (calPaddle != null)
                    calPaddle.ClearCachedBall();

                // Reset court and paddle QR tracking so they re-scan
                var calTracker = FindFirstObjectByType<PlaceTrackedImages>();
                if (calTracker != null)
                {
                    calTracker.ResetCourt();
                    calTracker.ResetRacket();
                }

                // Calibrate IMU paddle orientation
                if (imuPaddleController != null)
                    imuPaddleController.Calibrate();

                // Publish calibration ack to ESP32 (UWB position + IMU paddle)
                PublishCalibration();

                Debug.Log("[MqttController] Button 2: Full Reset + Calibrate (position + paddle)");
                break;

            case 3: // Reset Ball (drop 3m, 0.5m in front of camera)
                var ball = FindBallController();
                if (ball != null)
                {
                    if (!ball.gameObject.activeInHierarchy)
                        ball.gameObject.SetActive(true);
                    ball.DropBallInFrontOfCamera();
                }
                var paddle2 = FindFirstObjectByType<PaddleHitController>();
                if (paddle2 != null)
                    paddle2.ClearCachedBall();
                Debug.Log("[MqttController] Button 3: Reset Ball");
                break;

            case 4:
                // Before game starts: cycle mode (Normal → Tutorial → GodMode)
                // After game starts: full reset
                if (gameState != null && !gameState.IsStarted)
                {
                    gameState.CycleMode();
                    Debug.Log("[MqttController] Button 4: Cycle Mode");
                    break;
                }

                // Full Reset — game state + ball + court + paddle
                if (gameState != null)
                    gameState.ResetGameplay();

                // Force ball back even if GameStateManager lost its reference
                var resetBall = FindBallController();
                if (resetBall != null)
                {
                    if (!resetBall.gameObject.activeInHierarchy)
                        resetBall.gameObject.SetActive(true);
                    resetBall.ResetBall();
                }

                // Clear stale ball cache in paddle so it re-finds the ball
                var paddle = FindFirstObjectByType<PaddleHitController>();
                if (paddle != null)
                    paddle.ClearCachedBall();

                // Reset court and paddle tracking
                var resetTracker = FindFirstObjectByType<PlaceTrackedImages>();
                if (resetTracker != null)
                {
                    resetTracker.ResetCourt();
                    resetTracker.ResetRacket();
                }

                Debug.Log("[MqttController] Button 4: Full Reset (game + ball + court + paddle)");
                break;

            default:
                Debug.Log($"[MqttController] Unknown button: {raw.button}");
                break;
        }
    }

    private PracticeBallController FindBallController()
    {
        if (ballController != null) return ballController;

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
        return ballController;
    }

    // ── /playerPosition handler (UWB) ──────────────────────────────────────────

    private void HandlePlayerPosition(string json)
    {
        UwbPositionPayload data;
        try { data = JsonConvert.DeserializeObject<UwbPositionPayload>(json); }
        catch (Exception e)
        {
            Debug.LogWarning($"[MqttController] Failed to parse /playerPosition: {e.Message}");
            return;
        }

        if (data?.position == null)
        {
            Debug.LogWarning("[MqttController] /playerPosition missing position field.");
            return;
        }

        // UWB origin is at the net centre (where anchors are).
        // Court-local origin (GameSpaceRoot) is at the player baseline, net at z=uwbNetZ.
        // UWB x = lateral (maps directly to court-local x).
        // UWB y = depth from net. uwbYSign controls direction:
        //   -1 → UWB y increases toward player (most common): courtZ = uwbNetZ - uwb.y
        //   +1 → UWB y increases toward bot:                  courtZ = uwbNetZ + uwb.y
        float courtZ = uwbNetZ + uwbYSign * data.position.y;
        Vector3 courtLocal = new Vector3(data.position.x, 0f, courtZ);

        // Store court-local for drift correction (camera head pos vs UWB head pos)
        _uwbCourtLocal = courtLocal;
        _hasUwbCourtLocal = true;

        // Transform into world space using the same reference as the ball
        _targetPlayerWorldPos = gameSpaceRoot != null
            ? gameSpaceRoot.TransformPoint(courtLocal)
            : courtLocal;

        _hasPlayerPosition = true;
        _lastUwbReceiveTime = Time.time;

        if (_uwbTimedOut)
        {
            _uwbTimedOut = false;
            Debug.Log("[playerPosition] UWB restored — switching back from camera fallback.");
        }

        _posLine = $"/playerPos: uwb=({data.position.x:F2},{data.position.y:F2}) court=({courtLocal.x:F2},{courtLocal.z:F2})";
        RefreshDebugText();

        Debug.Log($"[playerPosition] uwb=({data.position.x:F2},{data.position.y:F2}) " +
                  $"courtLocal=({courtLocal.x:F2},{courtLocal.z:F2}) " +
                  $"world=({_targetPlayerWorldPos.x:F2},{_targetPlayerWorldPos.z:F2})");
    }

    // ── Update: lerp player marker + UWB drift correction ───────────────────────

    private void Update()
    {
        // ── UWB timeout detection ─────────────────────────────────────────────────
        if (_hasPlayerPosition
            && !_uwbTimedOut
            && Time.time - _lastUwbReceiveTime > uwbTimeoutSeconds)
        {
            _uwbTimedOut = true;
            _posLine = "/playerPos: FALLBACK (camera)";
            RefreshDebugText();
            Debug.LogWarning("[playerPosition] UWB timed out — falling back to camera position.");
        }

        // ── Player marker ─────────────────────────────────────────────────────────
        if (playerMarker != null)
        {
            if (_uwbTimedOut || !_hasPlayerPosition)
            {
                if (Camera.main != null)
                {
                    Vector3 camPos = Camera.main.transform.position;
                    _targetPlayerWorldPos = new Vector3(camPos.x, playerMarker.position.y, camPos.z);
                }
            }

            playerMarker.position = Vector3.Lerp(
                playerMarker.position,
                _targetPlayerWorldPos,
                playerPositionSmoothing
            );
        }

        // ── UWB drift correction on GameSpaceRoot ─────────────────────────────────
        // UWB gives the player's absolute position on the court (court-local).
        // The AR camera gives the player's position in world space.
        // If gameSpaceRoot is correct: InverseTransformPoint(camera) == uwbCourtLocal (on X/Z).
        // Any difference is AR drift. We nudge gameSpaceRoot to close the gap.
        if (enableDriftCorrection
            && _hasUwbCourtLocal
            && !_uwbTimedOut
            && gameSpaceRoot != null
            && Camera.main != null)
        {
            Vector3 camWorld = Camera.main.transform.position;
            Vector3 camCourtLocal = gameSpaceRoot.InverseTransformPoint(camWorld);

            // Drift = where AR thinks we are minus where UWB says we are (X/Z only)
            float driftX = camCourtLocal.x - _uwbCourtLocal.x;
            float driftZ = camCourtLocal.z - _uwbCourtLocal.z;
            float driftMag = Mathf.Sqrt(driftX * driftX + driftZ * driftZ);

            if (driftMag > driftMinThreshold)
            {
                // Convert drift from court-local direction to world direction
                Vector3 driftWorld = gameSpaceRoot.TransformDirection(new Vector3(driftX, 0f, driftZ));
                driftWorld.y = 0f; // never correct vertical

                // Proportional correction, clamped per frame
                Vector3 correction = driftWorld * driftCorrectionSpeed * Time.deltaTime;
                if (correction.magnitude > driftMaxStepPerFrame)
                    correction = correction.normalized * driftMaxStepPerFrame;

                gameSpaceRoot.position += correction;

                // Periodic log
                if (Time.time - _lastDriftLogTime > 3f)
                {
                    _lastDriftLogTime = Time.time;
                    Debug.Log($"[UWB Drift] correcting {driftMag:F3}m " +
                              $"(AR court=({camCourtLocal.x:F2},{camCourtLocal.z:F2}) " +
                              $"UWB=({_uwbCourtLocal.x:F2},{_uwbCourtLocal.z:F2}))");
                }
            }
        }
    }

    // ── Publishing ──────────────────────────────────────────────────────────────

    public void PublishPlayerBall(Vector3 worldPos, Vector3 worldVel)
    {
        // Transform world → court-local
        Vector3 lp = gameSpaceRoot != null
            ? gameSpaceRoot.InverseTransformPoint(worldPos)
            : worldPos;
        Vector3 lv = gameSpaceRoot != null
            ? gameSpaceRoot.InverseTransformDirection(worldVel)
            : worldVel;

        // Axis remap: Unity (x=right, y=up, z=fwd) → AI training (x, y=depth, z=height)
        PlayerBallPayload payload = new PlayerBallPayload
        {
            position = new Vec3 { x = lp.x, y = lp.z, z = lp.y },
            velocity = new VelocityData { vx = lv.x, vy = lv.z, vz = lv.y }
        };

        string json = JsonConvert.SerializeObject(payload);

        // Always show on TMP so values are visible even when offline
        _pubLine = $"PUB pos:({payload.position.x:F2},{payload.position.y:F2},{payload.position.z:F2})" +
                   $" vel:({payload.velocity.vx:F2},{payload.velocity.vy:F2},{payload.velocity.vz:F2})";

        if (_eventSender == null || !IsConnected)
        {
            _pubLine += " [OFFLINE]";
            RefreshDebugText();
            Debug.LogWarning($"[MqttController] Cannot publish — not connected. Data: {json}");
            return;
        }

        try
        {
            _eventSender.Publish(unityPublishTopic, json);
            _pubLine += " [SENT]";
            RefreshDebugText();
            Debug.Log($"[playerBall] Published: {json}");
        }
        catch (Exception e)
        {
            _pubLine += " [FAIL]";
            RefreshDebugText();
            Debug.LogError($"[MqttController] Publish failed: {e.Message}");
        }
    }

    /// <summary>
    /// Publishes {"isCalibrated":1} to both calibration topics on the ESP32.
    /// /positionCalibration — UWB position calibration
    /// /paddleCalibration  — IMU paddle calibration
    /// </summary>
    public void PublishCalibration()
    {
        string json = "{\"isCalibrated\":1}";

        if (_eventSender == null || !IsConnected)
        {
            Debug.LogWarning("[MqttController] Cannot publish calibration — not connected.");
            return;
        }

        try
        {
            _eventSender.Publish("/positionCalibration", json);
            _eventSender.Publish("/paddleCalibration", json);
            Debug.Log("[MqttController] Published calibration to /positionCalibration and /paddleCalibration");
        }
        catch (Exception e)
        {
            Debug.LogError($"[MqttController] Calibration publish failed: {e.Message}");
        }
    }

    /// <summary>
    /// Publishes a ball hit acknowledgment to the ESP32 to trigger haptic feedback.
    /// </summary>
    public void PublishHitAcknowledge()
    {
        if (_eventSender == null || !IsConnected)
        {
            Debug.LogWarning("[MqttController] Cannot publish hit ack — not connected.");
            return;
        }

        try
        {
            _eventSender.Publish("/hitAck", "{\"hit\":true}");
            Debug.Log("[MqttController] Published hit ack to /hitAck");
        }
        catch (Exception e)
        {
            Debug.LogError($"[MqttController] Hit ack publish failed: {e.Message}");
        }
    }

    // ── Network status banner ────────────────────────────────────────────────

    private void ShowBanner(string message)
    {
        ShowBanner(message, new Color(0.9f, 0.2f, 0.2f, 0.85f));
    }

    private void ShowBanner(string message, Color bgColor)
    {
        if (bannerCanvasGO == null)
            CreateBannerUI();

        bannerText.text = message;
        bannerText.transform.parent.GetComponent<Image>().color = bgColor;
        bannerCanvasGO.SetActive(true);
    }

    private void HideBanner()
    {
        if (bannerCanvasGO != null)
            bannerCanvasGO.SetActive(false);
    }

    private void CreateBannerUI()
    {
        bannerCanvasGO = new GameObject("MqttStatusCanvas");
        bannerCanvasGO.AddComponent<Canvas>();
        StereoscopicAR.SetupWorldSpaceCanvas(bannerCanvasGO, sortingOrder: 500,
            width: 1080, height: 80);

        // Banner panel (fills entire canvas)
        var panelGO = new GameObject("BannerPanel");
        panelGO.transform.SetParent(bannerCanvasGO.transform, false);
        Image panelImg = panelGO.AddComponent<Image>();
        panelImg.color = new Color(0.9f, 0.2f, 0.2f, 0.85f);
        panelImg.raycastTarget = false;
        RectTransform panelRT = panelGO.GetComponent<RectTransform>();
        panelRT.anchorMin = Vector2.zero;
        panelRT.anchorMax = Vector2.one;
        panelRT.sizeDelta = Vector2.zero;

        // Position banner above the main HUD area
        RectTransform canvasRT = bannerCanvasGO.GetComponent<RectTransform>();
        // Offset upward: half of main canvas height + gap
        bannerCanvasGO.transform.localPosition += new Vector3(0f, 0.35f, 0f);

        // Banner text
        var textGO = new GameObject("BannerText");
        textGO.transform.SetParent(panelGO.transform, false);
        bannerText = textGO.AddComponent<Text>();
        bannerText.fontSize = 28;
        bannerText.color = Color.white;
        bannerText.alignment = TextAnchor.MiddleCenter;
        bannerText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        bannerText.fontStyle = FontStyle.Bold;
        bannerText.raycastTarget = false;
        RectTransform textRT = textGO.GetComponent<RectTransform>();
        textRT.anchorMin = Vector2.zero;
        textRT.anchorMax = Vector2.one;
        textRT.sizeDelta = Vector2.zero;
    }

    // ── Refresh the single TMP debug text with all live data ───────────────

    private void RefreshDebugText()
    {
        if (debugText == null) return;
        // Compose all topic lines into the existing TMP 3D text
        debugText.text = _connLine + "\n" + _imuLine;
        if (!string.IsNullOrEmpty(_btnLine))
            debugText.text += "\n" + _btnLine;
        if (!string.IsNullOrEmpty(_pubLine))
            debugText.text += "\n" + _pubLine;
        if (!string.IsNullOrEmpty(_recvLine))
            debugText.text += "\n" + _recvLine;
        if (!string.IsNullOrEmpty(_posLine))
            debugText.text += "\n" + _posLine;
        if (!string.IsNullOrEmpty(_signalLine))
            debugText.text += "\n" + _signalLine;
    }

    private void OnDestroy()
    {
        if (_eventSender != null)
        {
            _eventSender.OnMessageArrived -= OnMessageArrivedHandler;
            _eventSender.OnConnectionSucceeded -= OnConnectionStatusChanged;
            _eventSender.ConnectionFailed -= OnMqttConnectionFailed;
        }
    }
}

[Serializable]
public class Vec3 { public float x, y, z; }

[Serializable]
public class VelocityData { public float vx, vy, vz; }

// Received from /opponentBall
[Serializable]
public class OpponentBallPayload
{
    public Vec3 position;
    public VelocityData velocity;
    public int returnSwingType;
}

// Published to /playerBall
[Serializable]
public class PlayerBallPayload
{
    public Vec3 position;
    public VelocityData velocity;
}

// Received from /playerPosition (UWB ESP32)
[Serializable]
public class UwbPositionPayload
{
    public string clientID;
    public UwbPos position;
}

[Serializable]
public class UwbPos
{
    public float x;  // lateral across court (metres)
    public float y;  // depth along court   (metres)
}
