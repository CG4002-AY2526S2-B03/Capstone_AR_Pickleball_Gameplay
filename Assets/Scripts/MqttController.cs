using System.Collections;
using System.Collections.Generic;
using UnityEngine;
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

    void Start()
    {
        _eventSender.OnMessageArrived += OnMessageArrivedHandler;

#if UNITY_EDITOR
        // Test publish on start (editor only)
        StartCoroutine(TestPublish());
#endif
    }

#if UNITY_EDITOR
    IEnumerator TestPublish()
    {
        // Wait a moment for MQTT to connect
        yield return new WaitForSeconds(3f);

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
        PlayerBallPayload payload = new PlayerBallPayload
        {
            position = new Vec3 { x = pos.x, y = pos.y, z = pos.z },
            velocity = new VelocityData { vx = vel.x, vy = vel.y, vz = vel.z }
        };

        string json = JsonConvert.SerializeObject(payload);
        Debug.Log($"[playerBall] Publishing to {unityPublishTopic}: {json}");
        _eventSender.Publish(unityPublishTopic, json);
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
