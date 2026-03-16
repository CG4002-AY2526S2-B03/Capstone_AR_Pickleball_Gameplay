using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class MqttController : MonoBehaviour
{
    public string nameController = "MqttController";

    public MqttReceiver _eventSender;

    void Start()
    {
        _eventSender.OnMessageArrived += OnMessageArrivedHandler;
    }

    private void OnMessageArrivedHandler(string newMsg)
    {
        this.GetComponent<TextMeshPro>().text = newMsg;
        // Debug.Log("Message, from Object " +nameController+" is = " + newMsg);
    }

}