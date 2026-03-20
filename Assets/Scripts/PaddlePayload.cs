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
