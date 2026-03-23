using System;
using Newtonsoft.Json;

[Serializable]
public class EulerAngles
{
    public float roll;
    public float pitch;
    public float yaw;
}

[Serializable]
public class Vec3Payload
{
    public float x;
    public float y;
    public float z;
}

[Serializable]
public class ButtonState
{
    public bool up;
    public bool down;

    [JsonProperty("return")]
    public bool returnBtn;

    public bool select;
}

[Serializable]
public class PaddlePayload
{
    public EulerAngles orientation;
    public Vec3Payload angularVelocity;
    public Vec3Payload linearVelocity;
    public bool isServeAction;
    public ButtonState buttons;
}

/// <summary>
/// Raw packet from the ESP32 on /paddle topic.
/// ESP32 sends two schemas: type="imu" (position + velocity) and type="button" (button id).
/// Field names differ from PaddlePayload — this DTO matches the wire format exactly.
/// </summary>
[Serializable]
public class Esp32Packet
{
    [JsonProperty("type")]     public string     type;      // "imu" or "button"
    // IMU fields (ESP32 calls orientation "position" and linearVelocity "velocity")
    [JsonProperty("position")] public EulerAngles position;
    [JsonProperty("velocity")] public Vec3Payload velocity;
    // Button fields
    [JsonProperty("button")]   public int        button;    // 1=up 2=down 3=return 4=select
}
