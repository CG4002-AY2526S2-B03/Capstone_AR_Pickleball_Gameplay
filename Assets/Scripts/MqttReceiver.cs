using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using M2MqttUnity;
using uPLibrary.Networking.M2Mqtt;
using uPLibrary.Networking.M2Mqtt.Messages;
using System;
using UnityEngine.UI;

public class MqttReceiver : M2MqttUnityClient
{
    [Header("MQTT Settings")]
    public string[] unitySubscribeTopic = {"/paddle", "/opponentBall", "/playerPosition", "/system/signal" };
    public string unityPublishTopic = "/playerBall";

    public bool autoTest = false;

    //using C# Property GET/SET and event listener to reduce Update overhead in the controlled objects
    private string m_msg;
    public string msg
    {
        get
        {
            return m_msg;
        }
        set
        {
            if (m_msg == value) return;
            m_msg = value;
            //if (OnMessageArrived != null)
            //{
            //    OnMessageArrived(m_msg);
            //}
        }
    }

    public event OnMessageArrivedDelegate OnMessageArrived;
    public delegate void OnMessageArrivedDelegate(string topic, string newMsg);

    //using C# Property GET/SET and event listener to expose the connection status
    private bool m_isConnected;

    public bool isConnected
    {
        get
        {
            return m_isConnected;
        }
        set
        {
            if (m_isConnected == value) return;
            m_isConnected = value;
            if (OnConnectionSucceeded != null)
            {
                OnConnectionSucceeded(isConnected);
            }
        }
    }
    public event OnConnectionSucceededDelegate OnConnectionSucceeded;
    public delegate void OnConnectionSucceededDelegate(bool isConnected);

    // a list to store the messages
    private List<string> eventMessages = new List<string>();

    // TODO: update QoS
    public void Publish(string topic, string messageToPublish)
    {
        client.Publish(topic, System.Text.Encoding.UTF8.GetBytes(messageToPublish), MqttMsgBase.QOS_LEVEL_EXACTLY_ONCE, false);
        Debug.Log(topic + " ] Published: " + messageToPublish);
    }

    public void SetEncrypted(bool isEncrypted)
    {
        this.isEncrypted = isEncrypted;
    }

    protected override void OnConnecting()
    {
        base.OnConnecting();
    }

    protected override void OnConnected()
    {
        base.OnConnected();
        isConnected = true;

        //if (autoTest)
        //{
        //    Publish();
        //}
    }

    protected override void OnConnectionFailed(string errorMessage)
    {
        Debug.LogWarning("CONNECTION FAILED! " + errorMessage);
        isConnected = false;
    }

    protected override void OnDisconnected()
    {
        Debug.Log("Disconnected.");
        isConnected = false;
    }

    protected override void OnConnectionLost()
    {
        Debug.Log("CONNECTION LOST!");
    }

    protected override void SubscribeTopics()
    {
        byte[] qosLevels = new byte[unitySubscribeTopic.Length];
        for (int i = 0; i < qosLevels.Length; i++) {
            qosLevels[i] = MqttMsgBase.QOS_LEVEL_AT_MOST_ONCE; // Set QoS = 0 for all topics
        }
        Debug.Log($"qosLevels: '{qosLevels.Length}'");

        // Debug - check what topics are being passed
        for (int i = 0; i < unitySubscribeTopic.Length; i++)
        {
            Debug.Log($"Subscribing to topic [{i}]: '{unitySubscribeTopic[i]}'");
        }

        client.Subscribe(unitySubscribeTopic, qosLevels);
    }

    protected override void UnsubscribeTopics()
    {
        client.Unsubscribe(unitySubscribeTopic);
    }

    protected override void Start()
    {
        //mqttClientId = System.Guid.NewGuid().ToString();
        MqttProtocolVersion protocolVersion = MqttProtocolVersion.Version_3_1_1;
        base.Start();
    }

    protected override void DecodeMessage(string topic, byte[] message)
    {
        //The message is decoded
        msg = System.Text.Encoding.UTF8.GetString(message);

        Debug.Log("[" + topic + "] Received: " + msg);

        StoreMessage(msg);
        if (OnMessageArrived != null)
            OnMessageArrived(topic, msg);

        // TODO: Add handler for different topics
        // if (topic == unitySubscribeTopic)
        // {
        //if (autoTest)
        //    {
        //        autoTest = false;
        //        Disconnect();
        //    }
        // }
    }

    private void StoreMessage(string eventMsg)
    {
        if (eventMessages.Count > 50)
        {
            eventMessages.Clear();
        }
        eventMessages.Add(eventMsg);
    }

    protected override void Update()
    {
        base.Update(); // call ProcessMqttEvents()

    }

    private void OnDestroy()
    {
        Disconnect();
    }

    private void OnValidate()
    {
        if (autoTest)
        {
            autoConnect = true;
        }
    }
}