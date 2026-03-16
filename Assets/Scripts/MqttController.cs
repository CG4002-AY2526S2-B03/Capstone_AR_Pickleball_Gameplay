using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using System;
using Newtonsoft.Json;
//using System.Diagnostics;

public class MqttController : MonoBehaviour
{
    public string nameController = "MqttController";
    public string unityPublishTopic = "/playerBall";

    public MqttReceiver _eventSender;

    void Start()
    {
        _eventSender.OnMessageArrived += OnMessageArrivedHandler;

        // Test publish on start
        StartCoroutine(TestPublish());
    }

    IEnumerator TestPublish()
    {
        // Wait a moment for MQTT to connect
        yield return new WaitForSeconds(3f);

        Vector3 dummyPos = new Vector3(1.0f, 2.0f, 3.0f);
        Vector3 dummyVel = new Vector3(0.5f, 0.6f, 0.7f);
        PublishPlayerBall(dummyPos, dummyVel);
        Debug.Log("[TEST] Dummy playerBall payload published");
    }

    private void OnMessageArrivedHandler(string topic, string newMsg)
    {
        this.GetComponent<TextMeshPro>().text = newMsg;
        if (topic == "/opponentBall")
        {
            OpponentBallPayload data = JsonConvert.DeserializeObject<OpponentBallPayload>(newMsg);
            Debug.Log($"[opponentBall] pos: ({data.position.x}, {data.position.y}, {data.position.z})");
            Debug.Log($"[opponentBall] swing type: {data.returnSwingType}");
        }
        else if (topic == "/playerPosition")
        {
            Debug.Log($"[playerPosition] {newMsg}");
            // TODO: handle player position
        }
        else if (topic == "/paddle")
        {
            Debug.Log($"[paddle] {newMsg}");
            // TODO: handle paddle data
        }
    }

    public void PublishPlayerBall(Vector3 pos, Vector3 vel)
    {
        PlayerBallPayload payload = new PlayerBallPayload
        {
            position = new Vec3 { x = pos.x, y = pos.y, z = pos.z },
            velocity = new VelocityData { vx = vel.x, vy = vel.y, vz = vel.z }
        };

        string json = JsonConvert.SerializeObject(payload);
        Debug.Log($"[TEST] Publishing to {unityPublishTopic}: {json}");
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