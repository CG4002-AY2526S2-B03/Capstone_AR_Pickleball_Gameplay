using UnityEngine;

/// <summary>
/// Drives the player paddle from IMU sensor data received via MQTT on the /paddle topic.
///
/// Attach this to the same GameObject as PaddleHitController (PlayerPaddle).
/// MqttController calls SetPayload() each time a /paddle message arrives.
///
/// Position model: the paddle is anchored at a configurable offset from the AR camera.
/// Short-term velocity integration provides swing displacement that decays back to the
/// anchor, giving visual feedback without IMU drift.
///
/// Orientation comes directly from the hardware's complementary-filtered Euler angles.
/// Linear and angular velocities are exposed for PaddleHitController's impulse solver.
/// </summary>
public class ImuPaddleController : MonoBehaviour
{
    [Header("Camera Reference")]
    [Tooltip("AR Camera transform. Auto-assigned from Camera.main if left null.")]
    public Transform cameraTransform;

    [Header("Anchor Position (relative to camera)")]
    [Tooltip("Forward distance from camera.")]
    public float anchorDepth = 0.55f;
    [Tooltip("Lateral offset (positive = right).")]
    public float anchorLateral = 0.18f;
    [Tooltip("Vertical offset (positive = up).")]
    public float anchorHeight = -0.12f;

    [Header("Smoothing")]
    [Tooltip("Exponential smoothing factor for rotation (higher = snappier).")]
    public float rotationSmoothing = 12f;
    [Tooltip("Exponential smoothing factor for position displacement.")]
    public float positionSmoothing = 10f;

    [Header("Velocity Displacement")]
    [Tooltip("Scale factor for velocity-to-displacement integration.")]
    public float velocityDisplacementScale = 0.3f;
    [Tooltip("How fast displacement decays back to anchor (higher = faster).")]
    public float displacementDecay = 5f;
    [Tooltip("Maximum displacement magnitude from anchor (meters).")]
    public float maxDisplacement = 0.3f;

    [Header("IMU Axis Mapping")]
    [Tooltip("Sign multipliers to remap IMU Euler (pitch, yaw, roll) to Unity (X, Y, Z). " +
             "Adjust per hardware mounting orientation.")]
    public Vector3 eulerSign = new Vector3(1f, 1f, -1f);

    [Tooltip("Sign multipliers for linear velocity (IMU X,Y,Z -> Unity X,Y,Z).")]
    public Vector3 linearVelocitySign = new Vector3(1f, 1f, -1f);

    [Tooltip("Sign multipliers for angular velocity (IMU X,Y,Z -> Unity X,Y,Z).")]
    public Vector3 angularVelocitySign = new Vector3(1f, 1f, -1f);

    // ── Public state for PaddleHitController ────────────────────────────────────

    /// <summary>True when at least one valid payload has been received.</summary>
    public bool IsActive { get; private set; }

    /// <summary>IMU linear velocity converted to Unity world frame.</summary>
    public Vector3 PaddleVelocity { get; private set; }

    /// <summary>IMU angular velocity converted to Unity world frame.</summary>
    public Vector3 PaddleAngularVelocity { get; private set; }

    /// <summary>
    /// When false, IMU data is still processed and velocities are exposed,
    /// but this controller will NOT move/rotate the paddle transform.
    /// PaddleHitController sets this to false when hybrid QR+IMU mode is active
    /// (QR handles position, IMU provides velocity for the impulse solver).
    /// </summary>
    [HideInInspector]
    public bool ControlsTransform = true;

    /// <summary>Smoothed IMU orientation in world space (for hybrid mode).</summary>
    public Quaternion SmoothedRotation => smoothedRotation;

    // ── Private state ───────────────────────────────────────────────────────────

    private Rigidbody paddleRigidbody;
    private PaddlePayload latestPayload;
    private bool hasNewPayload;

    // Calibration: records the IMU orientation when Calibrate() is called.
    // All subsequent orientations are relative to this zero-point.
    private Quaternion calibrationOffset = Quaternion.identity;
    private bool isCalibrated;

    // Smoothed state
    private Quaternion smoothedRotation;
    private Quaternion previousTargetRotation;
    private Vector3 accumulatedDisplacement;
    private bool _loggedFirstPayload;

    // ─────────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        paddleRigidbody = GetComponent<Rigidbody>();

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        smoothedRotation = transform.rotation;
        previousTargetRotation = transform.rotation;
    }

    /// <summary>
    /// Called by MqttController on the main thread when a /paddle message arrives.
    /// Stores the payload for consumption in the next FixedUpdate.
    /// </summary>
    public void SetPayload(PaddlePayload payload)
    {
        latestPayload = payload;
        hasNewPayload = true;

        if (!IsActive)
            IsActive = true;
    }

    /// <summary>
    /// Records the current IMU orientation as the "zero" reference.
    /// Call this when the player holds the racket in a known neutral pose.
    /// </summary>
    public void Calibrate()
    {
        if (latestPayload == null) return;

        Quaternion rawImu = ImuEulerToQuaternion(latestPayload.orientation);
        calibrationOffset = Quaternion.Inverse(rawImu);
        isCalibrated = true;

        Debug.Log("[ImuPaddleController] Calibrated. Current IMU orientation set as zero reference.");
    }

    private void FixedUpdate()
    {
        if (!IsActive || latestPayload == null || cameraTransform == null)
            return;

        // Guard against incomplete payloads (orientation + linearVelocity required;
        // angularVelocity is optional — ESP32 doesn't send it separately)
        if (latestPayload.orientation == null || latestPayload.linearVelocity == null)
        {
            Debug.LogWarning("[ImuPaddleController] Incomplete payload — skipping frame.");
            return;
        }

        Vec3Payload angVelData = latestPayload.angularVelocity ?? new Vec3Payload();

        float dt = Time.fixedDeltaTime;

        // ── Orientation ─────────────────────────────────────────────────────────
        Quaternion rawImu = ImuEulerToQuaternion(latestPayload.orientation);

        // Guard against NaN quaternion from bad sensor data
        if (IsNaN(rawImu))
        {
            Debug.LogWarning("[ImuPaddleController] NaN orientation from IMU — skipping frame.");
            return;
        }

        Quaternion calibrated = calibrationOffset * rawImu;

        // Target rotation in world space: camera forward as base, IMU as local rotation
        Quaternion targetRotation = cameraTransform.rotation * calibrated;

        float lerpFactor = 1f - Mathf.Exp(-rotationSmoothing * dt);
        smoothedRotation = Quaternion.Slerp(smoothedRotation, targetRotation, lerpFactor);

        // ── Velocity conversion (IMU local frame -> Unity world frame) ──────────
        Vector3 imuLinVel = new Vector3(
            latestPayload.linearVelocity.x * linearVelocitySign.x,
            latestPayload.linearVelocity.y * linearVelocitySign.y,
            latestPayload.linearVelocity.z * linearVelocitySign.z);

        Vector3 imuAngVel = new Vector3(
            angVelData.x * angularVelocitySign.x,
            angVelData.y * angularVelocitySign.y,
            angVelData.z * angularVelocitySign.z);

        // Guard against NaN/Infinity from bad sensor readings
        if (IsNaNOrInf(imuLinVel) || IsNaNOrInf(imuAngVel))
        {
            Debug.LogWarning("[ImuPaddleController] NaN/Inf velocity from IMU — skipping frame.");
            return;
        }

        // Rotate into world frame using the camera orientation
        PaddleVelocity = cameraTransform.TransformDirection(imuLinVel);

        // Angular velocity: use ESP32 data if provided, otherwise derive from
        // frame-to-frame orientation change (ESP32 doesn't send angular velocity)
        if (imuAngVel.sqrMagnitude > 0.001f)
        {
            PaddleAngularVelocity = cameraTransform.TransformDirection(imuAngVel);
        }
        else
        {
            Quaternion deltaRot = targetRotation * Quaternion.Inverse(previousTargetRotation);
            deltaRot.ToAngleAxis(out float dAngle, out Vector3 dAxis);
            if (dAngle > 180f) dAngle -= 360f;
            PaddleAngularVelocity = (dAxis.sqrMagnitude > 0.001f)
                ? dAxis.normalized * (dAngle * Mathf.Deg2Rad / dt)
                : Vector3.zero;
        }
        previousTargetRotation = targetRotation;

        if (!_loggedFirstPayload)
        {
            Debug.Log($"[ImuPaddleController] First IMU payload — " +
                      $"euler=({latestPayload.orientation.pitch:F1},{latestPayload.orientation.yaw:F1},{latestPayload.orientation.roll:F1}) " +
                      $"rawLinVel=({latestPayload.linearVelocity.x:F2},{latestPayload.linearVelocity.y:F2},{latestPayload.linearVelocity.z:F2}) " +
                      $"worldVel=({PaddleVelocity.x:F2},{PaddleVelocity.y:F2},{PaddleVelocity.z:F2})");
            _loggedFirstPayload = true;
        }

        // ── Position: anchor + velocity displacement ────────────────────────────
        Vector3 anchorWorld = cameraTransform.TransformPoint(
            new Vector3(anchorLateral, anchorHeight, anchorDepth));

        // Integrate velocity into displacement
        accumulatedDisplacement += PaddleVelocity * dt * velocityDisplacementScale;

        // Exponential decay back toward anchor
        accumulatedDisplacement *= Mathf.Exp(-displacementDecay * dt);

        // Clamp displacement
        if (accumulatedDisplacement.magnitude > maxDisplacement)
            accumulatedDisplacement = accumulatedDisplacement.normalized * maxDisplacement;

        Vector3 targetPosition = anchorWorld + accumulatedDisplacement;

        // ── Apply to paddle ─────────────────────────────────────────────────────
        // In hybrid mode (QR + IMU), PaddleHitController handles position from QR.
        // We still compute velocities above but skip transform updates.
        if (ControlsTransform)
        {
            if (paddleRigidbody != null)
            {
                paddleRigidbody.MovePosition(targetPosition);
                paddleRigidbody.MoveRotation(smoothedRotation);
            }
            else
            {
                transform.SetPositionAndRotation(targetPosition, smoothedRotation);
            }
        }

        hasNewPayload = false;
    }

    private static bool IsNaN(Quaternion q)
    {
        return float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w);
    }

    private static bool IsNaNOrInf(Vector3 v)
    {
        return float.IsNaN(v.x) || float.IsNaN(v.y) || float.IsNaN(v.z) ||
               float.IsInfinity(v.x) || float.IsInfinity(v.y) || float.IsInfinity(v.z);
    }

    /// <summary>
    /// Converts IMU Euler angles (roll, pitch, yaw) to a Unity Quaternion,
    /// applying the configured axis sign mapping.
    /// </summary>
    private Quaternion ImuEulerToQuaternion(EulerAngles euler)
    {
        // IMU convention: roll/pitch/yaw -> mapped to Unity X/Y/Z via eulerSign
        float x = euler.pitch * eulerSign.x;
        float y = euler.yaw * eulerSign.y;
        float z = euler.roll * eulerSign.z;
        return Quaternion.Euler(x, y, z);
    }
}
