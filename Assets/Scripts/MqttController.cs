using System.Collections;
using System.Collections.Generic;
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

    [Header("Game Component References")]
    [Tooltip("IMU paddle controller for hardware-driven racket.")]
    public ImuPaddleController imuPaddleController;

    [Tooltip("Bot hit controller for ML-driven opponent.")]
    public BotHitController botHitController;

    [Tooltip("Ball controller for serve detection.")]
    public PracticeBallController ballController;

    [Header("Debug Display")]
    [Tooltip("Optional TMP text for displaying incoming messages.")]
    public TextMeshPro debugText;

    // ── Button / serve rising-edge state ────────────────────────────────────────
    private bool lastServeAction;
    private bool lastBtnUp;
    private bool lastBtnDown;
    private bool lastBtnReturn;
    private bool lastBtnSelect;

    // ── Network status banner ──────────────────────────────────────────────────
    private GameObject bannerCanvasGO;
    private Text bannerText;

    /// <summary>True when MQTT is connected and operational.</summary>
    public bool IsConnected => _eventSender != null && _eventSender.isConnected;

    void Start()
    {
        if (_eventSender == null)
        {
            ShowBanner("MQTT not configured — running in offline mode");
            Debug.LogWarning("[MqttController] MqttReceiver reference is null. Game will run without network.");
            return;
        }

        try
        {
            _eventSender.OnMessageArrived += OnMessageArrivedHandler;
            _eventSender.OnConnectionSucceeded += OnConnectionStatusChanged;
            _eventSender.ConnectionFailed += OnMqttConnectionFailed;
        }
        catch (Exception e)
        {
            ShowBanner($"MQTT setup error — running in offline mode");
            Debug.LogError($"[MqttController] Failed to subscribe to MQTT events: {e.Message}");
            return;
        }

        // Show connecting status; will be cleared on success or updated on failure
        ShowBanner("Connecting to MQTT broker...", Color.yellow);
        StartCoroutine(CheckConnectionTimeout());

#if UNITY_EDITOR
        StartCoroutine(TestPublish());
#endif
    }

    private IEnumerator CheckConnectionTimeout()
    {
        // Wait for connection attempt to resolve
        float timeout = _eventSender != null ? _eventSender.timeoutOnConnection / 1000f + 2f : 5f;
        yield return new WaitForSeconds(timeout);

        if (!IsConnected)
        {
            ShowBanner("MQTT connection failed — running in offline mode");
        }
    }

    private void OnConnectionStatusChanged(bool connected)
    {
        if (connected)
        {
            HideBanner();
            Debug.Log("[MqttController] MQTT connected successfully.");
        }
        else
        {
            ShowBanner("MQTT disconnected — running in offline mode");
        }
    }

    private void OnMqttConnectionFailed()
    {
        ShowBanner("MQTT connection failed — running in offline mode");
        Debug.LogWarning("[MqttController] MQTT connection failed. Game continues without network.");
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
        if (debugText != null)
            debugText.text = newMsg;

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
            Debug.Log($"[playerPosition] {newMsg}");
        }
        else if (topic == "/system/signal")
        {
            Debug.Log($"[system/signal] {newMsg}");
        }
    }

    // ── /opponentBall handler ───────────────────────────────────────────────────

    private void HandleOpponentBall(string json)
    {
        OpponentBallPayload data = JsonConvert.DeserializeObject<OpponentBallPayload>(json);
        Debug.Log($"[opponentBall] pos: ({data.position.x}, {data.position.y}, {data.position.z})" +
                  $" vel: ({data.velocity.vx}, {data.velocity.vy}, {data.velocity.vz})" +
                  $" swing: {data.returnSwingType}");

        if (botHitController != null)
        {
            Vector3 pos = new Vector3(data.position.x, data.position.y, data.position.z);
            Vector3 vel = new Vector3(data.velocity.vx, data.velocity.vy, data.velocity.vz);
            botHitController.SetMLPrediction(pos, vel, data.returnSwingType);
        }
    }

    // ── /paddle handler ─────────────────────────────────────────────────────────

    private void HandlePaddle(string json)
    {
        PaddlePayload data = JsonConvert.DeserializeObject<PaddlePayload>(json);

        // Feed IMU data to paddle controller
        if (imuPaddleController != null)
        {
            imuPaddleController.SetPayload(data);
        }

        // Serve detection (rising edge: was false, now true)
        if (data.isServeAction && !lastServeAction)
        {
            if (ballController != null && ballController.IsFrozen)
            {
                ballController.ResetBall();
                Debug.Log("[MqttController] Serve action detected — ball reset to serve position.");
            }
        }
        lastServeAction = data.isServeAction;

        // Button handling (rising edge for each)
        if (data.buttons != null)
        {
            HandleButtons(data.buttons);
        }
    }

    private void HandleButtons(ButtonState buttons)
    {
        // Return button -> reset ball / restart rally
        if (buttons.returnBtn && !lastBtnReturn)
        {
            if (ballController != null)
            {
                ballController.ResetBall();
                Debug.Log("[MqttController] Return button pressed — ball reset.");
            }
        }
        lastBtnReturn = buttons.returnBtn;

        // Select button -> calibrate IMU paddle
        if (buttons.select && !lastBtnSelect)
        {
            if (imuPaddleController != null)
            {
                imuPaddleController.Calibrate();
                Debug.Log("[MqttController] Select button pressed — paddle calibrated.");
            }
        }
        lastBtnSelect = buttons.select;

        // Up/Down buttons -> reserved for future use
        if (buttons.up && !lastBtnUp)
        {
            Debug.Log("[MqttController] Up button pressed.");
        }
        lastBtnUp = buttons.up;

        if (buttons.down && !lastBtnDown)
        {
            Debug.Log("[MqttController] Down button pressed.");
        }
        lastBtnDown = buttons.down;
    }

    // ── Publishing ──────────────────────────────────────────────────────────────

    public void PublishPlayerBall(Vector3 pos, Vector3 vel)
    {
        if (_eventSender == null || !IsConnected)
        {
            Debug.LogWarning("[MqttController] Cannot publish — not connected.");
            return;
        }

        try
        {
            PlayerBallPayload payload = new PlayerBallPayload
            {
                position = new Vec3 { x = pos.x, y = pos.y, z = pos.z },
                velocity = new VelocityData { vx = vel.x, vy = vel.y, vz = vel.z }
            };

            string json = JsonConvert.SerializeObject(payload);
            Debug.Log($"[playerBall] Publishing to {unityPublishTopic}: {json}");
            _eventSender.Publish(unityPublishTopic, json);
        }
        catch (Exception e)
        {
            Debug.LogError($"[MqttController] Publish failed: {e.Message}");
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
        Canvas canvas = bannerCanvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;

        var scaler = bannerCanvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight = 0.5f;

        // Banner panel (top-center strip)
        var panelGO = new GameObject("BannerPanel");
        panelGO.transform.SetParent(bannerCanvasGO.transform, false);
        Image panelImg = panelGO.AddComponent<Image>();
        panelImg.color = new Color(0.9f, 0.2f, 0.2f, 0.85f);
        panelImg.raycastTarget = false;
        RectTransform panelRT = panelGO.GetComponent<RectTransform>();
        panelRT.anchorMin = new Vector2(0.05f, 0.80f);
        panelRT.anchorMax = new Vector2(0.95f, 0.85f);
        panelRT.sizeDelta = Vector2.zero;

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
