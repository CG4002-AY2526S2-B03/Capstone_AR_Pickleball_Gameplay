using UnityEngine;

/// <summary>
/// Drives the player paddle from IMU sensor data received via MQTT on the /paddle topic.
///
/// Attach this to the same GameObject as PaddleHitController (PlayerPaddle).
/// MqttController calls SetPayload() each time a /paddle message arrives.
///
/// Orientation model:
///   - While QR is active, PaddleHitController calls UpdateWorldOffset() each frame
///     to learn the mapping from IMU space to AR world space.
///   - When QR is lost, the frozen offset correctly maps IMU orientation to world space.
///   - Calibrate() (button 3) sets the IMU zero reference (pitch/roll/yaw).
///
/// Velocity model:
///   - Linear velocity from ESP32 accelerometer integration.
///   - Angular velocity derived from frame-to-frame IMU orientation change.
///   - Both are transformed to world space using the QR-learned offset (or camera fallback).
/// </summary>
public class ImuPaddleController : MonoBehaviour
{
    public enum ImuAxis
    {
        X = 0,
        Y = 1,
        Z = 2,
    }

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

    [Header("IMU Pivot")]
    [Tooltip("Local-space offset from the PlayerPaddle transform origin to the IMU attachment point " +
             "(the physical pivot at the end of the handle). IMU-driven rotation is applied around this " +
             "pivot instead of around the GameObject origin.")]
    public Vector3 imuPivotLocalOffset = new Vector3(0f, -0.3f, 0f);

    [Tooltip("When enabled, converts between IMU handle pivot position and transform position using imuPivotLocalOffset. " +
             "Disable to keep the paddle visually anchored and avoid whole-object shifting.")]
    public bool applyPivotOffsetToTransformPosition = false;

    [Header("Smoothing")]
    [Tooltip("Exponential smoothing factor for rotation (higher = snappier).")]
    public float rotationSmoothing = 12f;

    [Header("IMU-only Translation")]
    [Tooltip("When true, IMU linear velocity contributes to IMU-only position translation. " +
             "Disable to prevent drift from integrated IMU XYZ.")]
    public bool useImuLinearVelocityForImuOnlyPosition = true;
    [Tooltip("Scale applied to IMU linear velocity integration when IMU-only translation is enabled.")]
    public float imuOnlyLinearVelocityScale = 2.5f;
    [Tooltip("Ignore tiny IMU-only linear velocity magnitudes below this threshold (m/s) to reduce drift from bias/noise.")]
    public float imuOnlyLinearVelocityDeadzone = 0.04f;
    [Tooltip("Smoothing rate for IMU-only linear velocity before integrating position (1/seconds).")]
    public float imuOnlyLinearVelocitySmoothing = 10f;
    [Tooltip("Maximum IMU-only displacement magnitude from anchor (meters). Set to 0 to disable clamping.")]
    public float imuOnlyMaxDisplacement = 1.0f;
    [Tooltip("Anchor return / damping applied to IMU-only displacement (1/seconds). Higher = less drift.")]
    public float imuOnlyDisplacementDamping = 0.6f;

    [Header("IMU Axis Mapping")]
    [Tooltip("Sign multipliers to remap IMU Euler (pitch, yaw, roll) to Unity (X, Y, Z). " +
             "Adjust per hardware mounting orientation.")]
    public Vector3 eulerSign = new Vector3(-1f, -1f, 1f);

    [Tooltip("Constant offset (degrees) added AFTER sign mapping. " +
             "Use (0, 180, 0) to fix a 180° yaw flip from IMU mounting orientation.")]
    public Vector3 eulerOffset = new Vector3(0f, 180f, 0f);

    [Tooltip("Sign multipliers applied after linear-velocity axis remap to Unity local X,Y,Z.")]
    public Vector3 linearVelocitySign = new Vector3(1f, 1f, -1f);

    [Tooltip("Which raw IMU axis maps to Unity local X velocity before sign is applied.")]
    public ImuAxis linearVelocityAxisForUnityX = ImuAxis.X;

    [Tooltip("Which raw IMU axis maps to Unity local Y velocity before sign is applied.")]
    public ImuAxis linearVelocityAxisForUnityY = ImuAxis.Y;

    [Tooltip("Which raw IMU axis maps to Unity local Z velocity before sign is applied.")]
    public ImuAxis linearVelocityAxisForUnityZ = ImuAxis.Z;

    [Tooltip("Sign multipliers for angular velocity (IMU X,Y,Z -> Unity X,Y,Z).")]
    public Vector3 angularVelocitySign = new Vector3(1f, 1f, -1f);

    // ── Public state for PaddleHitController ────────────────────────────────────

    /// <summary>True when at least one valid payload has been received.</summary>
    public bool IsActive { get; private set; }

    /// <summary>True after Calibrate() has been called.</summary>
    public bool IsCalibrated => isCalibrated;

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

    /// <summary>True after QR-to-IMU alignment has been learned at least once.</summary>
    public bool HasWorldOffset => hasWorldOffset;

    /// <summary>Current IMU orientation mapped to world space (using QR offset or camera fallback).</summary>
    public Quaternion WorldRotation { get; private set; }

    /// <summary>Smoothed IMU orientation in camera-relative space (for IMU-only mode).</summary>
    public Quaternion SmoothedRotation => smoothedRotation;

    // ── Private state ───────────────────────────────────────────────────────────

    private Rigidbody paddleRigidbody;
    private PaddlePayload latestPayload;
    private bool hasNewPayload;

    // Calibration: records the IMU orientation when Calibrate() is called.
    // All subsequent orientations are relative to this zero-point.
    private Quaternion calibrationOffset = Quaternion.identity;
    private bool isCalibrated;

    // QR-to-IMU alignment: learned while QR is actively tracked.
    // Maps calibrated IMU orientation → world-space orientation.
    // Frozen when QR is lost; continuously updated when QR is active.
    private Quaternion imuToWorldOffset = Quaternion.identity;
    private bool hasWorldOffset;

    // Smoothed state
    private Quaternion smoothedRotation;
    private Quaternion smoothedWorldRotation;

    // Previous calibrated IMU rotation for angular velocity derivation (pure IMU, no camera)
    private Quaternion prevCalibrated = Quaternion.identity;
    private bool hasPrevCalibrated;

    // IMU-only mode: camera-relative displacement
    private Vector3 accumulatedDisplacement;
    private Vector3 smoothedImuOnlyVelocity;

    private bool _loggedFirstPayload;

    // ─────────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        paddleRigidbody = GetComponent<Rigidbody>();

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        smoothedRotation = transform.rotation;
        smoothedWorldRotation = transform.rotation;
        WorldRotation = transform.rotation;
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
    /// Call this when the player holds the racket in a known neutral pose
    /// (horizontal, paddle face up).
    /// Pitch and roll are absolute (gravity-referenced).
    /// Yaw resets to 0 at the current heading.
    /// </summary>
    public void Calibrate()
    {
        if (latestPayload == null || latestPayload.orientation == null) return;

        Quaternion rawImu = ImuEulerToQuaternion(latestPayload.orientation);
        calibrationOffset = Quaternion.Inverse(rawImu);
        isCalibrated = true;

        // Reset previous calibrated state so angular velocity doesn't spike
        prevCalibrated = Quaternion.identity;
        hasPrevCalibrated = false;

        // Invalidate QR-learned world offset — it was learned with the old calibration.
        // When QR is active, UpdateWorldOffset() re-learns it next frame (imperceptible).
        // When QR is lost (stale mode), paddle falls back to camera-relative orientation
        // until QR resumes and re-learns the mapping.
        hasWorldOffset = false;

        Debug.Log($"[ImuPaddleController] Calibrated. IMU euler=({latestPayload.orientation.pitch:F1}," +
                  $"{latestPayload.orientation.yaw:F1},{latestPayload.orientation.roll:F1}) set as zero reference. " +
                  $"World offset invalidated — will re-learn from QR.");
    }

    /// <summary>
    /// Called by PaddleHitController every frame while QR is actively tracked.
    /// Learns the mapping from IMU orientation to world-space orientation.
    /// This auto-calibrates the yaw alignment so IMU maps correctly to court space.
    /// </summary>
    /// <param name="qrWorldRotation">The QR-tracked paddle rotation in world space
    /// (includes prefab rotation offset).</param>
    public void UpdateWorldOffset(Quaternion qrWorldRotation)
    {
        if (latestPayload == null || latestPayload.orientation == null) return;

        Quaternion calibratedImu = calibrationOffset * ImuEulerToQuaternion(latestPayload.orientation);
        // worldRot = offset * calibratedImu  =>  offset = worldRot * Inverse(calibratedImu)
        imuToWorldOffset = qrWorldRotation * Quaternion.Inverse(calibratedImu);
        hasWorldOffset = true;
    }

    private void FixedUpdate()
    {
        if (!IsActive || latestPayload == null || cameraTransform == null)
            return;

        if (latestPayload.orientation == null || latestPayload.linearVelocity == null)
        {
            Debug.LogWarning("[ImuPaddleController] Incomplete payload — skipping frame.");
            return;
        }

        Vec3Payload angVelData = latestPayload.angularVelocity ?? new Vec3Payload();

        float dt = Time.fixedDeltaTime;

        // ── Orientation ─────────────────────────────────────────────────────────
        Quaternion rawImu = ImuEulerToQuaternion(latestPayload.orientation);

        if (IsNaN(rawImu))
        {
            Debug.LogWarning("[ImuPaddleController] NaN orientation from IMU — skipping frame.");
            return;
        }

        Quaternion calibrated = calibrationOffset * rawImu;

        // Camera-relative rotation (for IMU-only mode)
        Quaternion cameraTarget = cameraTransform.rotation * calibrated;
        float lerpFactor = 1f - Mathf.Exp(-rotationSmoothing * dt);
        smoothedRotation = Quaternion.Slerp(smoothedRotation, cameraTarget, lerpFactor);

        // World-space rotation (using QR-learned offset, or camera fallback)
        if (hasWorldOffset)
        {
            Quaternion worldTarget = imuToWorldOffset * calibrated;
            smoothedWorldRotation = Quaternion.Slerp(smoothedWorldRotation, worldTarget, lerpFactor);
            WorldRotation = smoothedWorldRotation;
        }
        else
        {
            WorldRotation = smoothedRotation; // fallback to camera-relative
        }

        // ── Velocity conversion ──────────────────────────────────────────────────
        Vector3 rawImuLinVel = new Vector3(
            latestPayload.linearVelocity.x,
            latestPayload.linearVelocity.y,
            latestPayload.linearVelocity.z);

        Vector3 imuLinVel = new Vector3(
            GetAxis(rawImuLinVel, linearVelocityAxisForUnityX) * linearVelocitySign.x,
            GetAxis(rawImuLinVel, linearVelocityAxisForUnityY) * linearVelocitySign.y,
            GetAxis(rawImuLinVel, linearVelocityAxisForUnityZ) * linearVelocitySign.z);

        Vector3 imuAngVel = new Vector3(
            angVelData.x * angularVelocitySign.x,
            angVelData.y * angularVelocitySign.y,
            angVelData.z * angularVelocitySign.z);

        if (IsNaNOrInf(imuLinVel) || IsNaNOrInf(imuAngVel))
        {
            Debug.LogWarning("[ImuPaddleController] NaN/Inf velocity from IMU — skipping frame.");
            return;
        }

        // IMU velocity vectors are in paddle/handle-local coordinates.
        // Rotate them by the current world paddle orientation each frame.
        Quaternion vectorToWorld = hasWorldOffset ? WorldRotation : smoothedRotation;
        PaddleVelocity = vectorToWorld * imuLinVel;

        // Angular velocity: use ESP32 data if provided, otherwise derive from
        // frame-to-frame calibrated IMU orientation change (pure IMU, no camera contamination)
        if (imuAngVel.sqrMagnitude > 0.001f)
        {
            PaddleAngularVelocity = vectorToWorld * imuAngVel;
        }
        else
        {
            // Derive from pure IMU orientation delta (not camera-relative)
            if (hasPrevCalibrated)
            {
                Quaternion imuDelta = calibrated * Quaternion.Inverse(prevCalibrated);
                imuDelta.ToAngleAxis(out float dAngle, out Vector3 dAxis);
                if (dAngle > 180f) dAngle -= 360f;
                Vector3 localAngVel = (dAxis.sqrMagnitude > 0.001f)
                    ? dAxis.normalized * (dAngle * Mathf.Deg2Rad / dt)
                    : Vector3.zero;
                // Rotate to world space
                PaddleAngularVelocity = vectorToWorld * localAngVel;
            }
            else
            {
                PaddleAngularVelocity = Vector3.zero;
            }
        }
        prevCalibrated = calibrated;
        hasPrevCalibrated = true;

        if (!_loggedFirstPayload)
        {
            Debug.Log($"[ImuPaddleController] First IMU payload — " +
                      $"euler=({latestPayload.orientation.pitch:F1},{latestPayload.orientation.yaw:F1},{latestPayload.orientation.roll:F1}) " +
                      $"rawLinVel=({latestPayload.linearVelocity.x:F2},{latestPayload.linearVelocity.y:F2},{latestPayload.linearVelocity.z:F2}) " +
                      $"mappedLocalVel=({imuLinVel.x:F2},{imuLinVel.y:F2},{imuLinVel.z:F2}) " +
                      $"worldVel=({PaddleVelocity.x:F2},{PaddleVelocity.y:F2},{PaddleVelocity.z:F2}) " +
                      $"hasWorldOffset={hasWorldOffset}");
            _loggedFirstPayload = true;
        }

        // ── Position (IMU-only mode): anchor + velocity displacement ──────────
        if (ControlsTransform)
        {
            Vector3 pivotWorld = cameraTransform.TransformPoint(
                new Vector3(anchorLateral, anchorHeight, anchorDepth));

            if (useImuLinearVelocityForImuOnlyPosition)
            {
                Vector3 filteredVelocity = PaddleVelocity;
                float deadzone = Mathf.Max(0f, imuOnlyLinearVelocityDeadzone);
                if (filteredVelocity.magnitude < deadzone)
                    filteredVelocity = Vector3.zero;

                float smoothing = Mathf.Max(0f, imuOnlyLinearVelocitySmoothing);
                float velocityLerp = 1f - Mathf.Exp(-smoothing * dt);
                smoothedImuOnlyVelocity = Vector3.Lerp(smoothedImuOnlyVelocity, filteredVelocity, velocityLerp);

                accumulatedDisplacement += smoothedImuOnlyVelocity * dt * imuOnlyLinearVelocityScale;
                accumulatedDisplacement *= Mathf.Exp(-Mathf.Max(0f, imuOnlyDisplacementDamping) * dt);

                float maxDisplacement = Mathf.Max(0f, imuOnlyMaxDisplacement);
                if (maxDisplacement > 0f && accumulatedDisplacement.magnitude > maxDisplacement)
                    accumulatedDisplacement = accumulatedDisplacement.normalized * maxDisplacement;
            }
            else
            {
                accumulatedDisplacement = Vector3.zero;
                smoothedImuOnlyVelocity = Vector3.zero;
            }

            pivotWorld += accumulatedDisplacement;
            Vector3 targetPosition = ResolveTransformPositionFromPivot(pivotWorld, smoothedRotation);

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

    /// <summary>
    /// Converts a desired IMU-pivot world position into the PlayerPaddle transform position,
    /// so rotations occur around the handle-mounted IMU instead of the GameObject origin.
    /// </summary>
    public Vector3 ResolveTransformPositionFromPivot(Vector3 pivotWorldPosition, Quaternion worldRotation)
    {
        if (!applyPivotOffsetToTransformPosition)
            return pivotWorldPosition;

        return pivotWorldPosition - worldRotation * imuPivotLocalOffset;
    }

    /// <summary>
    /// Converts the current PlayerPaddle transform position into the IMU-pivot world position.
    /// Useful when handing off from QR-driven pose to IMU-driven pose without a visible snap.
    /// </summary>
    public Vector3 ResolvePivotWorldPosition(Vector3 transformWorldPosition, Quaternion worldRotation)
    {
        if (!applyPivotOffsetToTransformPosition)
            return transformWorldPosition;

        return transformWorldPosition + worldRotation * imuPivotLocalOffset;
    }

    private static bool IsNaN(Quaternion q)
    {
        return float.IsNaN(q.x) || float.IsNaN(q.y) || float.IsNaN(q.z) || float.IsNaN(q.w);
    }

    private static float GetAxis(Vector3 value, ImuAxis axis)
    {
        switch (axis)
        {
            case ImuAxis.X:
                return value.x;
            case ImuAxis.Y:
                return value.y;
            case ImuAxis.Z:
                return value.z;
            default:
                return value.x;
        }
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
        float x = euler.pitch * eulerSign.x + eulerOffset.x;
        float y = euler.yaw * eulerSign.y + eulerOffset.y;
        float z = euler.roll * eulerSign.z + eulerOffset.z;
        return Quaternion.Euler(x, y, z);
    }
}
