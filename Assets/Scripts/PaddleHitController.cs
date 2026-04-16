using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

public class PaddleHitController : MonoBehaviour
{
    [Header("References")]
    public Transform cameraTransform;
    [Tooltip("When set and active, the paddle is driven by IMU sensor data via MQTT. " +
             "Takes priority over AprilTag and camera modes.")]
    public ImuPaddleController imuController;
    [Tooltip("When set, publishes ball state to /playerBall after each hit.")]
    public MqttController mqttController;
    [Tooltip("When set, reports hits and checks kitchen violations.")]
    public GameStateManager gameState;
    [Tooltip("When set, the physics paddle teleports to this transform every FixedUpdate " +
             "instead of using camera-relative positioning. Assign this at runtime " +
             "when the AprilTag-spawned racket is detected.")]
    public Transform qrTrackedRacket;
    [Tooltip("Optional direct ball reference used by assist and hit detection. Falls back to runtime lookup when null.")]
    public Rigidbody trackedBall;

    [Header("Assist Hit / Base")]
    [Tooltip("Master proximity assist. When enabled, near-misses around the paddle surface count as hits.")]
    public bool enableProximityFallback = true;
    [Tooltip("Base assist-hit radius from the paddle collider surface (meters).")]
    public float proximityHitDistance = 0.5f;

    [Header("Assist Hit / AprilTag")]
    [Tooltip("Local-space offset (meters) from the AprilTag marker origin to the desired physics paddle center. " +
             "Use this to align collider hits with the rendered racket when the marker is mounted off-center.")]
    public Vector3 qrMarkerLocalOffset = Vector3.zero;
    [Tooltip("Additional rotation offset (degrees) applied to AprilTag marker rotation before driving the physics paddle.")]
    public Vector3 qrRotationOffsetEuler = Vector3.zero;
    [Tooltip("When true, AprilTag-driven modes enlarge the assist-hit proximity radius to compensate for AR drift.")]
    public bool enableQrProximityAssist = true;
    [Tooltip("Proximity hit distance used only in AprilTag-driven modes (meters).")]
    public float qrProximityHitDistance = 0.6f;

    [Header("Assist Hit / Serve")]
    [Tooltip("When true, WaitingToServe enlarges the assist-hit proximity radius to make serves easier.")]
    public bool enableWaitingToServeAssist = true;
    [Tooltip("Proximity hit distance used only in WaitingToServe to make serves easier.")]
    public float waitingToServeHitDistance = 1.0f;

    [Header("Assist Hit / IMU")]
    [Tooltip("When IMU is active, flickRadius contributes to the unified assist-hit radius, " +
             "and nearby IMU-assisted hits can use flickVelocityMultiplier.")]
    public bool enableFlick = true;
    [Tooltip("Ball must be within this radial distance of the opponent-facing flick cylinder axis (metres).")]
    public float flickRadius = 0.3f;
    [Tooltip("Forward reach of the opponent-facing flick cylinder from the paddle face centre (metres).")]
    public float flickAssistRange = 0.4f;
    [Tooltip("Minimum swing speed (linear + wrist contribution) to trigger a flick. " +
             "Prevents accidental triggers while holding still.")]
    public float flickMinSwingSpeed = 0.5f;
    [Tooltip("Scales the IMU-derived paddle surface speed used by Flick Assist. " +
             "3.0 means flick hits are solved as if the paddle surface were moving three times as fast.")]
    public float flickVelocityMultiplier = 3f;
    [Tooltip("Upward tilt added to the flick direction so the ball arcs over the net. " +
             "0 = flat horizontal, 0.3 = natural arc.")]
    [Range(0f, 0.8f)]
    public float flickUplift = 0.25f;
    [Tooltip("Minimum seconds between flick triggers. Prevents rapid re-triggers in one swing.")]
    public float flickCooldown = 0.3f;
    [Tooltip("Minimum forward/back swing speed component (m/s) used to decide flick toward/away direction.")]
    public float flickDirectionalDeadzone = 0.08f;
    [Tooltip("Synthetic contact backstep from the ball center (meters) used by flick assist to preserve intended direction.")]
    public float flickContactBackstep = 0.08f;
    [Tooltip("Extra local-space offset applied to the flick assist cylinder center after resolving the paddle face center from the BoxCollider.")]
    [FormerlySerializedAs("flickHemisphereLocalOffset")]
    public Vector3 flickCylinderLocalOffset = Vector3.zero;

    [Header("Assist Hit / God Mode")]
    [Tooltip("Additional assist-hit radius granted only in God Mode to make rallies easier for demos and grading.")]
    [Min(0f)]
    public float godModeAssistRadiusBonus = 0.3f;

    [Header("Mouse 3D Control")]
    public float depthFromCamera = 0.55f;
    public float horizontalRange = 0.45f;
    public float verticalRange = 0.35f;
    public float followSharpness = 24f;
    public float defaultScreenX = 0.75f;
    public float defaultScreenY = 0.36f;
    public bool lockCursorOnPlay;

    [Header("AR / Device Control")]
    [Tooltip("When true, use mouse position to place the paddle in editor mode.")]
    public bool useMouseInEditor = true;
    [Tooltip("When true, device builds ignore mouse input and keep paddle anchored to default screen point relative to AR camera.")]
    public bool useCameraRelativePointOnDevice = true;

    [Header("Paddle Pose")]
    public Vector3 baseLocalEuler = new Vector3(15f, -90f, 0f);
    public Vector3 localFaceNormal = Vector3.right;

    [Header("Hit Physics")]
    // Coefficient of restitution: 1 = perfectly elastic, 0 = perfectly plastic.
    // A real pickleball COR is typically 0.82–0.90 at tournament speed.
    public float restitution = 0.86f;
    // Coulomb friction coefficient between paddle surface and ball (tangential impulse).
    // Controls how much lateral paddle motion transfers to the ball and drives spin.
    public float frictionCoefficient = 0.35f;
    [Tooltip("Scales paddle-surface velocity for standard player hits. Use this to tune player-only hit power.")]
    public float playerHitVelocityMultiplier = 3f;
    public float maxBallSpeed = 22f;
    // Multiplier for angular impulse from off-center contact (higher = more topspin/slice).
    public float spinFromOffCenter = 5f;
    // Multiplier for spin contribution from paddle rotation at impact.
    public float spinFromTangential = 0.15f;
    public float hitCooldown = 0.03f;
    public bool requireBallTag;
    public string ballTag = "Ball";

    [Header("Paddle Surface (2D Platform)")]
    [Tooltip("Width of the paddle face (meters). Standard pickleball paddle ≈ 0.20m.")]
    public float paddleWidth = 0.20f;
    [Tooltip("Height of the paddle face (meters). Standard pickleball paddle ≈ 0.24m.")]
    public float paddleHeight = 0.24f;
    [Tooltip("Thickness of the collision surface (meters). Thin = 2D platform feel.")]
    public float paddleThickness = 0.015f;
    [Tooltip("Local-space center of the paddle BoxCollider. Use this to align the physics collider to the rendered racket mesh.")]
    public Vector3 paddleColliderCenter = Vector3.zero;
    [Tooltip("Auto-configure BoxCollider to paddle face dimensions on startup.")]
    public bool autoSizeCollider = true;

    [Header("Debug Proximity")]
    [Tooltip("When true, emits [debugproximity] logs to Xcode with live paddle/ball positions and hit coordinates.")]
    public bool enableDebugProximityLogs = false;
    [Tooltip("Seconds between continuous [debugproximity] position logs. Set to 0 to log every FixedUpdate.")]
    public float debugProximityLogInterval = 0.1f;

    [Header("Debug Flick Assist")]
    [Tooltip("When true, shows the IMU flick assist cylinder as a transparent colored volume during play.")]
    public bool showFlickAssistDebugVolume = false;
    [Tooltip("Color of the flick assist debug cylinder. Alpha controls transparency.")]
    public Color flickAssistDebugColor = new Color(0.1f, 0.9f, 0.4f, 0.2f);

    private Rigidbody paddleRigidbody;
    private Collider[] paddleColliders;
    private Vector3 previousPosition;
    private Vector3 paddleVelocity;
    private Vector3 paddleAngularVelocity;
    private float lastHitTime;
    private float lastFlickTime;
    private bool isInKitchen;
    private Rigidbody cachedBallRb;
    private PracticeBallController cachedPracticeBallController;
    private float lastBallSearchTime;
    private Transform cachedGameSpaceRoot;
    private bool qrPoseDrivenMode;
    private bool loggedBallTagLookupFailure;

    /// <summary>Clears all runtime ball references so the next lookup fully re-resolves the live ball.</summary>
    public void ClearCachedBall()
    {
        trackedBall = null;
        cachedBallRb = null;
        cachedPracticeBallController = null;
        lastBallSearchTime = 0f;
        loggedBallTagLookupFailure = false;
    }

    // AprilTag position persistence: paddle stays at last known position when AprilTag is lost
    private bool qrEverTracked;
    private Vector3 lastQrPosition;
    private Quaternion lastQrRotation;

    /// <summary>Set by PlaceTrackedImages each frame based on ARTrackedImage.trackingState.</summary>
    [HideInInspector] public bool qrActivelyTracking;

    /// <summary>Time.time when PlaceTrackedImages last set qrActivelyTracking.</summary>
    [HideInInspector] public float lastQrTrackingUpdateTime;

    /// <summary>Rotation offset baked into the AprilTag racket prefab. Set by PlaceTrackedImages on spawn.</summary>
    [HideInInspector] public Quaternion qrPrefabRotOffset = Quaternion.identity;

    [Header("AprilTag Tracking Timeout")]
    [Tooltip("If PlaceTrackedImages hasn't confirmed active AprilTag tracking for this long (seconds), treat as stale.")]
    public float qrTrackingTimeout = 0.1f;

    [Header("IMU Placement")]
    [Tooltip("Distance from IMU (handle/wrist) to paddle face center (meters). 0.3 = 30cm.")]
    public float imuToFaceDistance = 0.3f;

    [Header("Stale AprilTag + IMU")]
    [Tooltip("When true, stale mode uses IMU linear XYZ to translate paddle position. " +
             "Disable to keep position locked to the last AprilTag pose and avoid drift.")]
    public bool useImuLinearVelocityForStalePosition = true;
    [Tooltip("Maximum stale-mode drift distance from the last AprilTag pose when IMU position integration is enabled (meters). " +
             "Set to 0 to disable clamping.")]
    public float staleImuMaxDrift = 2.0f;
    [Tooltip("Scale applied to stale-mode IMU origin velocity before integration.")]
    public float staleImuLinearVelocityScale = 3.0f;
    [Tooltip("How long stale mode should keep integrating IMU position estimate after AprilTag is lost (seconds). Set to 0 for unlimited.")]
    public float staleImuPredictionSeconds = 0f;
    [Tooltip("Ignore tiny stale IMU origin-velocity magnitudes below this threshold (m/s) to reduce jitter.")]
    public float staleImuVelocityDeadzone = 0.002f;
    [Tooltip("Smoothing rate for stale IMU origin-velocity estimate (1/seconds).")]
    public float staleImuVelocitySmoothing = 4f;
    [Tooltip("When stale IMU speed is low, gently pull estimated position back toward last AprilTag anchor (1/seconds).")]
    public float staleImuAnchorReturnRate = 0.7f;
    [Tooltip("Maximum stale IMU speed (m/s) where anchor return damping is active.")]
    public float staleImuAnchorReturnVelocityThreshold = 0.12f;

    [Header("AprilTag -> IMU Takeover Guard")]
    [Tooltip("When true, prevents a large one-frame jump when switching from AprilTag tracking to stale IMU mode.")]
    public bool clampLargeQrToImuTakeover = true;
    [Tooltip("Maximum allowed distance (meters) between current physics paddle position and last AprilTag pose at AprilTag-loss handoff. " +
             "If exceeded, stale mode starts from current physics position instead of snapping to AprilTag pose.")]
    public float staleTakeoverMaxDistance = 0.35f;

    // Stale AprilTag + IMU: integration state from last AprilTag pose
    private Vector3 stalePosition;
    private Quaternion staleRotation;
    private bool staleModeInitialized;
    private float staleModeStartTime;
    private Vector3 staleSmoothedOriginVelocity;
    private Vector3 staleAnchorPosition;

    // Mode transition logging
    private string _lastMode;
    private float _lastDiagLogTime;
    private float _lastDebugProximityLogTime;
    private GameObject flickAssistDebugVisual;
    private Material flickAssistDebugMaterial;
    private GameObject flickAssistDebugAxisVisual;
    private LineRenderer flickAssistDebugAxisRenderer;
    private GameObject flickAssistDebugStartCapVisual;
    private GameObject flickAssistDebugEndCapVisual;

    public string CurrentMode => string.IsNullOrEmpty(_lastMode) ? "Unknown" : _lastMode;

    private void Awake()
    {
        if (TryGetComponent<Camera>(out _))
        {
            Debug.LogError("PaddleHitController is attached to a Camera. Attach it to the paddle object instead.");
            enabled = false;
            return;
        }

        paddleRigidbody = GetComponent<Rigidbody>();

        if (cameraTransform == null && Camera.main != null)
        {
            cameraTransform = Camera.main.transform;
        }

        // Auto-resolve references that are commonly left null in Inspector
        if (imuController == null)
            imuController = FindFirstObjectByType<ImuPaddleController>();
        if (mqttController == null)
            mqttController = FindFirstObjectByType<MqttController>();
        if (gameState == null)
            gameState = FindFirstObjectByType<GameStateManager>();

        if (cameraTransform != null && transform == cameraTransform)
        {
            Debug.LogError("PaddleHitController target transform is the camera. Move this component to the paddle object.");
            enabled = false;
            return;
        }

        if (paddleRigidbody != null)
        {
            paddleRigidbody.isKinematic = true;
            paddleRigidbody.useGravity = false;
            paddleRigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            paddleRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        }

        cachedGameSpaceRoot = ResolveGameSpaceRoot(forceRefresh: true);

        if (lockCursorOnPlay)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        paddleColliders = GetComponentsInChildren<Collider>();

        // Auto-size the BoxCollider to a thin paddle face (2D platform).
        // localFaceNormal = Vector3.right → face lies in the YZ plane,
        // so X = thickness (thin), Y = height, Z = width.
        if (autoSizeCollider)
        {
            BoxCollider box = GetComponent<BoxCollider>();
            if (box != null)
            {
                box.size = new Vector3(paddleThickness, paddleHeight, paddleWidth);
                box.center = paddleColliderCenter;
            }
        }

        previousPosition = transform.position;
    }

    private void LateUpdate()
    {
        UpdateFlickAssistDebugVisual();
    }

    private void OnDisable()
    {
        DestroyFlickAssistDebugVisual();
    }

    private void OnDestroy()
    {
        DestroyFlickAssistDebugVisual();
    }

    private void FixedUpdate()
    {
        qrPoseDrivenMode = false;

        // ── Cache AprilTag position ─────────────────────────────────────────────────────
        // Only update cached position when the AprilTag is genuinely being tracked.
        // When tracking is lost, lastQrPosition/lastQrRotation retain the last
        // good values so the paddle can use them as an anchor.
        bool qrAvailable = false;
        if (qrTrackedRacket != null)
        {
            if (qrTrackedRacket.gameObject.activeInHierarchy)
            {
                if (qrActivelyTracking)
                {
                    ApplyQrCalibrationPose(
                        qrTrackedRacket.position,
                        qrTrackedRacket.rotation,
                        out lastQrPosition,
                        out lastQrRotation);
                }
                qrEverTracked = true;
            }
            qrAvailable = qrEverTracked;
        }

        // ── AprilTag tracking timeout fallback ────────────────────────────────────────
        // If PlaceTrackedImages hasn't confirmed active tracking for a while,
        // force stale mode (handles edge case where ARFoundation stops firing events)
        if (qrActivelyTracking && lastQrTrackingUpdateTime > 0f
            && Time.time - lastQrTrackingUpdateTime > qrTrackingTimeout)
        {
            // Snapshot the latest available marker pose before switching to stale
            // mode so stale integration always starts from a reliable AprilTag anchor.
            if (qrTrackedRacket != null && qrTrackedRacket.gameObject.activeInHierarchy)
            {
                ApplyQrCalibrationPose(
                    qrTrackedRacket.position,
                    qrTrackedRacket.rotation,
                    out lastQrPosition,
                    out lastQrRotation);
            }

            qrActivelyTracking = false;
        }

        // ── Periodic diagnostic log ─────────────────────────────────────────────
        if (Time.time - _lastDiagLogTime > 2f)
        {
            _lastDiagLogTime = Time.time;
            bool imuActive = imuController != null && imuController.IsActive;
            Vector3 pPos = transform.position;
            Rigidbody diagBall = GetBallRigidbody();
            // Also keep trackedBall in sync so proximity hit always has a reference
            if (diagBall != null && trackedBall == null)
                trackedBall = diagBall;
            Vector3 bPos = diagBall != null ? diagBall.position : Vector3.zero;
            float dist = diagBall != null ? Vector3.Distance(pPos, bPos) : -1f;
            float qrToPhysics = qrTrackedRacket != null
                ? Vector3.Distance(qrTrackedRacket.position, pPos)
                : -1f;

            Vector3 imuRawEuler = imuController != null ? imuController.RawImuEuler : Vector3.zero;
            Vector3 imuRawLinVel = imuController != null ? imuController.RawImuLinearVelocity : Vector3.zero;
            Vector3 imuRawAngVel = imuController != null ? imuController.RawImuAngularVelocity : Vector3.zero;
            Debug.Log($"[PaddleHit] DIAG: qrAvail={qrAvailable} qrActive={qrActivelyTracking} " +
                      $"imuCtrl={imuController != null} imuActive={imuActive} " +
                      $"mode={_lastMode ?? "none"} " +
                      $"paddlePos=({pPos.x:F3},{pPos.y:F3},{pPos.z:F3}) " +
                      $"ballPos=({bPos.x:F3},{bPos.y:F3},{bPos.z:F3}) " +
                      $"paddleBallDist={dist:F3} qrToPhysics={qrToPhysics:F3}" +
                      (imuActive ? $" imuInEulerPYR=({imuRawEuler.x:F2},{imuRawEuler.y:F2},{imuRawEuler.z:F2})" +
                                   $" imuInLinVel=({imuRawLinVel.x:F5},{imuRawLinVel.y:F5},{imuRawLinVel.z:F5})" +
                                   $" imuInAngVel=({imuRawAngVel.x:F3},{imuRawAngVel.y:F3},{imuRawAngVel.z:F3})" +
                                   $" imuOutVel={imuController.PaddleVelocity.magnitude:F4}" +
                                   $" imuOutAngVel={imuController.PaddleAngularVelocity.magnitude:F2}" +
                                   $" worldOff={imuController.HasWorldOffset}" +
                                   $" cal={imuController.IsCalibrated}" : ""));
        }

        // ── Fresh AprilTag + IMU: AprilTag actively tracked ─────────────────────────────────
        // Follow AprilTag strictly for position and rotation (drift-free AR anchor).
        // IMU provides velocity/angular velocity for the hit impulse solver.
        // Continuously learn the IMU-to-world mapping so stale mode works correctly.
        if (qrAvailable && qrActivelyTracking && imuController != null && imuController.IsActive)
        {
            qrPoseDrivenMode = true;
            LogModeTransition("Fresh AprilTag + IMU (strict AprilTag)");

            imuController.ControlsTransform = false;

            // Auto-calibrate IMU-to-world alignment every frame AprilTag is visible.
            // This maps IMU yaw to court/world orientation so stale mode is correct.
            imuController.UpdateWorldOffset(lastQrRotation);

            paddleVelocity = imuController.PaddleVelocity;
            paddleAngularVelocity = imuController.PaddleAngularVelocity;

            // Snapshot for seamless stale-mode transition using the transform origin.
            // Handle pivot is accounted for in hit kinematics, not by translating the whole object.
            stalePosition = lastQrPosition;
            staleRotation = lastQrRotation;
            staleModeInitialized = false;
            staleSmoothedOriginVelocity = Vector3.zero;
            staleAnchorPosition = lastQrPosition;

            if (paddleRigidbody != null)
            {
                paddleRigidbody.MovePosition(lastQrPosition);
                paddleRigidbody.MoveRotation(lastQrRotation);
            }
            else
            {
                transform.SetPositionAndRotation(lastQrPosition, lastQrRotation);
            }

            previousPosition = lastQrPosition;
            LogDebugProximityTracking(lastQrPosition);

            bool assistedHit = TryProximityHit();
            if (!assistedHit)
                TryFlickAssist();
            return;
        }

        // ── Stale AprilTag + IMU: AprilTag lost, integrate from last AprilTag pose ────────────────
        // Position: continue integrating from the last AprilTag-aligned pose without
        // translating the whole paddle to the handle pivot.
        // Rotation: use IMU world orientation (AprilTag-learned offset maps IMU yaw to court space).
        // When AprilTag resumes (block above), snaps back to true AprilTag pose.
        if (qrAvailable && !qrActivelyTracking && imuController != null && imuController.IsActive)
        {
            qrPoseDrivenMode = true;
            LogModeTransition("Stale AprilTag + IMU (integration)");

            imuController.ControlsTransform = false;

            paddleVelocity = imuController.PaddleVelocity;
            paddleAngularVelocity = imuController.PaddleAngularVelocity;

            float dt = Mathf.Max(Time.fixedDeltaTime, 0.0001f);

            if (!staleModeInitialized)
            {
                staleModeInitialized = true;
                staleModeStartTime = Time.time;
                staleSmoothedOriginVelocity = Vector3.zero;

                // Re-anchor stale integration start to the latest AprilTag pose.
                // If the handoff gap is suspiciously large, keep continuity by
                // starting from the current physics paddle position instead.
                Vector3 currentPhysicsPosition = paddleRigidbody != null
                    ? paddleRigidbody.position
                    : transform.position;

                staleAnchorPosition = lastQrPosition;
                stalePosition = lastQrPosition;
                staleRotation = lastQrRotation;

                if (clampLargeQrToImuTakeover)
                {
                    float maxTakeoverDistance = Mathf.Max(0f, staleTakeoverMaxDistance);
                    if (maxTakeoverDistance > 0f)
                    {
                        float takeoverGap = Vector3.Distance(currentPhysicsPosition, lastQrPosition);
                        if (takeoverGap > maxTakeoverDistance)
                        {
                            staleAnchorPosition = currentPhysicsPosition;
                            stalePosition = currentPhysicsPosition;
                            Debug.LogWarning($"[PaddleHit] AprilTag->IMU takeover guard: gap={takeoverGap:F3}m " +
                                             $"(max {maxTakeoverDistance:F3}m). Starting stale mode from current paddle pose.");
                        }
                    }
                }
            }

            // Rotation FIRST: use world-space IMU orientation (auto-calibrated from AprilTag).
            // Fall back to camera-relative smoothed rotation if world offset isn't available
            // (e.g. after calibration while AprilTag is out of view) so rotation never freezes.
            staleRotation = imuController.HasWorldOffset
                ? imuController.WorldRotation
                : imuController.SmoothedRotation;

            Vector3 staleOriginVelocity = Vector3.zero;
            float staleAge = Time.time - staleModeStartTime;
            bool allowStalePrediction = useImuLinearVelocityForStalePosition
                && (staleImuPredictionSeconds <= 0f || staleAge <= staleImuPredictionSeconds);

            if (allowStalePrediction)
            {
                // Integrate transform-origin velocity derived from handle-mounted IMU data.
                // v_origin = v_handle - (omega x r_handle)
                Vector3 handleOffsetWorld = staleRotation * imuController.imuPivotLocalOffset;
                Vector3 rawOriginVelocity = paddleVelocity - Vector3.Cross(paddleAngularVelocity, handleOffsetWorld);
                if (rawOriginVelocity.magnitude < Mathf.Max(0f, staleImuVelocityDeadzone))
                    rawOriginVelocity = Vector3.zero;

                rawOriginVelocity *= Mathf.Max(0f, staleImuLinearVelocityScale);

                float smoothing = Mathf.Max(0f, staleImuVelocitySmoothing);
                float velocityLerp = 1f - Mathf.Exp(-smoothing * dt);
                staleSmoothedOriginVelocity = Vector3.Lerp(staleSmoothedOriginVelocity, rawOriginVelocity, velocityLerp);

                staleOriginVelocity = staleSmoothedOriginVelocity;
                stalePosition += staleOriginVelocity * dt;

                float maxDrift = Mathf.Max(0f, staleImuMaxDrift);
                if (maxDrift > 0f)
                {
                    Vector3 fromAnchor = stalePosition - staleAnchorPosition;
                    if (fromAnchor.sqrMagnitude > maxDrift * maxDrift)
                        stalePosition = staleAnchorPosition + fromAnchor.normalized * maxDrift;
                }

                // Suppress long-term drift from IMU bias when motion is nearly still.
                float anchorReturnRate = Mathf.Max(0f, staleImuAnchorReturnRate);
                float returnVelocityThreshold = Mathf.Max(0f, staleImuAnchorReturnVelocityThreshold);
                if (anchorReturnRate > 0f && staleOriginVelocity.magnitude <= returnVelocityThreshold)
                {
                    float anchorLerp = 1f - Mathf.Exp(-anchorReturnRate * dt);
                    stalePosition = Vector3.Lerp(stalePosition, staleAnchorPosition, anchorLerp);
                }
            }
            else
            {
                if (!useImuLinearVelocityForStalePosition)
                {
                    // Fully locked mode: keep position stuck to last reliable AprilTag pose.
                    stalePosition = staleAnchorPosition;
                }
                else
                {
                    // Prediction window expired: hold last predicted pose (no further IMU translation).
                    staleSmoothedOriginVelocity = Vector3.zero;
                }
            }
            Vector3 appliedPosition = stalePosition;

            // Apply to physics paddle
            if (paddleRigidbody != null)
            {
                paddleRigidbody.MovePosition(appliedPosition);
                paddleRigidbody.MoveRotation(staleRotation);
            }
            else
            {
                transform.SetPositionAndRotation(appliedPosition, staleRotation);
            }

            // Sync visible racket to follow physics paddle
            if (qrTrackedRacket != null)
            {
                ResolveVisualRacketPoseFromPhysicsPose(
                    appliedPosition,
                    staleRotation,
                    out Vector3 visualMarkerPosition,
                    out Quaternion visualMarkerRotation);
                qrTrackedRacket.SetPositionAndRotation(visualMarkerPosition, visualMarkerRotation);
            }

            previousPosition = appliedPosition;
            LogDebugProximityTracking(appliedPosition);

            // Periodic stale-mode diagnostic
            if (Time.time - _lastDiagLogTime > 2f)
            {
                _lastDiagLogTime = Time.time;
                Debug.Log($"[PaddleHit] STALE: linVel={paddleVelocity.magnitude:F5} " +
                          $"angVel={paddleAngularVelocity.magnitude:F3} " +
                          $"worldOffset={imuController.HasWorldOffset} " +
                          $"useImuPos={useImuLinearVelocityForStalePosition} " +
                          $"staleAge={staleAge:F2}s " +
                          $"predict={allowStalePrediction} " +
                          $"originVel={staleOriginVelocity.magnitude:F5} " +
                          $"appliedPos={appliedPosition} " +
                          $"staleRot={staleRotation.eulerAngles}");
            }

            bool assistedHit = TryProximityHit();
            if (!assistedHit)
                TryFlickAssist();
            return;
        }

        // ── IMU-only mode: driven by hardware IMU via MQTT ────────────────────────
        if (imuController != null && imuController.IsActive)
        {
            LogModeTransition("IMU-only");
            imuController.ControlsTransform = true;
            staleModeInitialized = false;
            staleSmoothedOriginVelocity = Vector3.zero;
            paddleVelocity = imuController.PaddleVelocity;
            paddleAngularVelocity = imuController.PaddleAngularVelocity;
            previousPosition = transform.position;
            LogDebugProximityTracking(transform.position);

            // Sync visible racket to follow physics paddle
            if (qrTrackedRacket != null)
            {
                ResolveVisualRacketPoseFromPhysicsPose(
                    transform.position,
                    transform.rotation,
                    out Vector3 visualMarkerPosition,
                    out Quaternion visualMarkerRotation);
                qrTrackedRacket.SetPositionAndRotation(visualMarkerPosition, visualMarkerRotation);
            }

            bool assistedHit = TryProximityHit();
            if (!assistedHit)
                TryFlickAssist();
            return;
        }

        // Re-enable IMU transform control when not in any IMU mode
        if (imuController != null)
            imuController.ControlsTransform = true;

        // ── AprilTag-only mode: follow the physical racket card ─────────────────────────
        // Uses cached position so paddle persists when AprilTag tracking is lost.
        if (qrAvailable)
        {
            qrPoseDrivenMode = true;
            LogModeTransition("AprilTag-only");
            staleModeInitialized = false;
            staleSmoothedOriginVelocity = Vector3.zero;
            paddleVelocity = (lastQrPosition - previousPosition) / Mathf.Max(Time.fixedDeltaTime, 0.0001f);

            Quaternion prevRot = paddleRigidbody != null
                ? paddleRigidbody.rotation
                : transform.rotation;
            Quaternion dRot = lastQrRotation * Quaternion.Inverse(prevRot);
            dRot.ToAngleAxis(out float dAngle, out Vector3 dAxis);
            if (dAngle > 180f) { dAngle -= 360f; }
            paddleAngularVelocity = dAxis * (dAngle * Mathf.Deg2Rad / Mathf.Max(Time.fixedDeltaTime, 0.0001f));

            if (paddleRigidbody != null)
            {
                paddleRigidbody.MovePosition(lastQrPosition);
                paddleRigidbody.MoveRotation(lastQrRotation);
            }
            else
            {
                transform.SetPositionAndRotation(lastQrPosition, lastQrRotation);
            }

            previousPosition = lastQrPosition;
            LogDebugProximityTracking(lastQrPosition);

            TryProximityHit();
            return;
        }

        // ── Fallback: camera-relative mode (editor / device without AprilTag) ──────────
        LogModeTransition("Camera fallback");
        staleModeInitialized = false;
        staleSmoothedOriginVelocity = Vector3.zero;
        if (cameraTransform == null)
        {
            return;
        }

        Vector3 worldPosition = GetTargetWorldPosition();
        Quaternion worldRotation = GetTargetWorldRotation(worldPosition);

        // ── Paddle velocity via finite-difference on the TARGET position ──────────
        // IMPORTANT: Kinematic Rigidbodies always report .velocity = Vector3.zero in
        // Unity regardless of how fast MovePosition moves them.  We must compute the
        // velocity ourselves from the delta of the desired (target) position between
        // consecutive FixedUpdate ticks.  This is the only reliable source of paddle
        // swing speed for the impulse solver.
        paddleVelocity = (worldPosition - previousPosition) / Mathf.Max(Time.fixedDeltaTime, 0.0001f);

        // Angular velocity: finite-difference on target rotation.
        Quaternion previousRotation = paddleRigidbody != null
            ? paddleRigidbody.rotation
            : transform.rotation;
        Quaternion deltaRotation = worldRotation * Quaternion.Inverse(previousRotation);
        deltaRotation.ToAngleAxis(out float deltaAngle, out Vector3 deltaAxis);
        if (deltaAngle > 180f) { deltaAngle -= 360f; }
        paddleAngularVelocity = deltaAxis * (deltaAngle * Mathf.Deg2Rad / Mathf.Max(Time.fixedDeltaTime, 0.0001f));

        // ── Move the paddle ───────────────────────────────────────────────────────
        if (paddleRigidbody != null)
        {
            float lerpFactor = 1f - Mathf.Exp(-followSharpness * Time.fixedDeltaTime);
            Vector3 blendedPosition = Vector3.Lerp(paddleRigidbody.position, worldPosition, lerpFactor);
            Quaternion blendedRotation = Quaternion.Slerp(paddleRigidbody.rotation, worldRotation, lerpFactor);

            paddleRigidbody.MovePosition(blendedPosition);
            paddleRigidbody.MoveRotation(blendedRotation);
        }
        else
        {
            transform.SetPositionAndRotation(worldPosition, worldRotation);
        }

        previousPosition = worldPosition;
        LogDebugProximityTracking(worldPosition);

        TryProximityHit();
    }

    private void LogModeTransition(string mode)
    {
        if (mode != _lastMode)
        {
            Debug.Log($"[PaddleHit] Mode: {mode}");
            _lastMode = mode;
        }
    }

    private void LogDebugProximityTracking(Vector3? paddleWorldOverride = null)
    {
        if (!enableDebugProximityLogs)
            return;

        if (debugProximityLogInterval > 0f
            && Time.time - _lastDebugProximityLogTime < debugProximityLogInterval)
        {
            return;
        }

        _lastDebugProximityLogTime = Time.time;

        Rigidbody ballRb = GetBallRigidbody();
        Transform gameSpaceRoot = ResolveGameSpaceRoot();
        Vector3 paddleWorld = paddleWorldOverride ?? transform.position;
        Vector3? ballWorld = ballRb != null ? ballRb.position : null;
        float? distance = ballWorld.HasValue ? Vector3.Distance(paddleWorld, ballWorld.Value) : null;

        Debug.Log(
            $"[debugproximity] event=tracking mode={_lastMode ?? "none"} " +
            $"paddleWorld={FormatVector3(paddleWorld)} " +
            $"paddleCourt={FormatCourtPosition(gameSpaceRoot, paddleWorld)} " +
            $"ballWorld={FormatNullableVector3(ballWorld)} " +
            $"ballCourt={FormatCourtPosition(gameSpaceRoot, ballWorld)} " +
            $"dist={FormatNullableFloat(distance)}");
    }

    private void LogDebugProximityHit(Rigidbody ballBody, Vector3 contactPoint, Vector3 outgoingVelocity)
    {
        if (!enableDebugProximityLogs)
            return;

        Transform gameSpaceRoot = ResolveGameSpaceRoot();
        Vector3 paddleWorld = transform.position;
        Vector3 ballWorld = ballBody.position;

        Debug.Log(
            $"[debugproximity] event=hit mode={_lastMode ?? "none"} " +
            $"paddleWorld={FormatVector3(paddleWorld)} " +
            $"paddleCourt={FormatCourtPosition(gameSpaceRoot, paddleWorld)} " +
            $"ballWorld={FormatVector3(ballWorld)} " +
            $"ballCourt={FormatCourtPosition(gameSpaceRoot, ballWorld)} " +
            $"contactWorld={FormatVector3(contactPoint)} " +
            $"contactCourt={FormatCourtPosition(gameSpaceRoot, contactPoint)} " +
            $"ballOutWorldVel={FormatVector3(outgoingVelocity)} " +
            $"ballOutCourtVel={FormatCourtDirection(gameSpaceRoot, outgoingVelocity)}");
    }

    private static string FormatVector3(Vector3 value)
    {
        return $"({value.x:F3},{value.y:F3},{value.z:F3})";
    }

    private static string FormatNullableVector3(Vector3? value)
    {
        return value.HasValue ? FormatVector3(value.Value) : "n/a";
    }

    private static string FormatNullableFloat(float? value)
    {
        return value.HasValue ? value.Value.ToString("F3") : "n/a";
    }

    private static string FormatCourtPosition(Transform gameSpaceRoot, Vector3 worldPosition)
    {
        if (gameSpaceRoot == null)
            return "n/a";

        return FormatVector3(gameSpaceRoot.InverseTransformPoint(worldPosition));
    }

    private static string FormatCourtPosition(Transform gameSpaceRoot, Vector3? worldPosition)
    {
        if (gameSpaceRoot == null || !worldPosition.HasValue)
            return "n/a";

        return FormatVector3(gameSpaceRoot.InverseTransformPoint(worldPosition.Value));
    }

    private static string FormatCourtDirection(Transform gameSpaceRoot, Vector3 worldDirection)
    {
        if (gameSpaceRoot == null)
            return "n/a";

        return FormatVector3(gameSpaceRoot.InverseTransformDirection(worldDirection));
    }

    /// <summary>
    /// Returns the best known ball Rigidbody. Uses <see cref="trackedBall"/> first,
    /// then <see cref="cachedBallRb"/>, and falls back to a tag/name search
    /// (throttled to once per second to avoid scanning every FixedUpdate).
    /// </summary>
    private Rigidbody GetBallRigidbody()
    {
        if (IsUsableBallRigidbody(trackedBall))
            return trackedBall;

        trackedBall = null;

        if (IsUsableBallRigidbody(cachedBallRb))
            return cachedBallRb;

        cachedBallRb = null;

        PracticeBallController liveBallController = PracticeBallController.GetLiveInstance();
        if (liveBallController != null)
        {
            Rigidbody liveBallRb = liveBallController.GetComponent<Rigidbody>();
            if (IsUsableBallRigidbody(liveBallRb))
            {
                cachedBallRb = liveBallRb;
                trackedBall = liveBallRb;
                loggedBallTagLookupFailure = false;
                return cachedBallRb;
            }
        }

        if (Time.time - lastBallSearchTime > 1f)
        {
            lastBallSearchTime = Time.time;

            Rigidbody found = null;
            if (!string.IsNullOrWhiteSpace(ballTag))
            {
                try
                {
                    GameObject ballObject = GameObject.FindWithTag(ballTag);
                    if (ballObject != null)
                        found = ballObject.GetComponent<Rigidbody>();
                }
                catch (UnityException exception)
                {
                    if (!loggedBallTagLookupFailure)
                    {
                        loggedBallTagLookupFailure = true;
                        Debug.LogWarning($"[PaddleHit] Ball tag lookup failed: {exception.Message}");
                    }
                }
            }

            if (found == null)
            {
                Rigidbody[] rigidbodies = FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
                for (int index = 0; index < rigidbodies.Length; index++)
                {
                    Rigidbody body = rigidbodies[index];
                    if (body == null || body == paddleRigidbody) continue;
                    if (!body.gameObject.activeInHierarchy) continue;
                    if (body.gameObject.name == "_BallBackup") continue;
                    if (body.gameObject.name.IndexOf("ball", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = body;
                        break;
                    }
                }
            }

            cachedBallRb = found;
            if (found != null)
            {
                trackedBall = found;
                loggedBallTagLookupFailure = false;
            }
        }

        return cachedBallRb;
    }

    private PracticeBallController GetBallController()
    {
        if (cachedPracticeBallController != null
            && cachedPracticeBallController.gameObject != null
            && cachedPracticeBallController.gameObject.scene.isLoaded)
        {
            return cachedPracticeBallController;
        }

        cachedPracticeBallController = PracticeBallController.GetLiveInstance();
        return cachedPracticeBallController;
    }

    private static bool IsUsableBallRigidbody(Rigidbody body)
    {
        if (body == null)
            return false;

        GameObject candidate = body.gameObject;
        if (candidate == null)
            return false;
        if (!candidate.scene.isLoaded)
            return false;
        if (!candidate.activeInHierarchy)
            return false;
        if (candidate.name == "_BallBackup")
            return false;

        return true;
    }

    private static bool IsFiniteVector(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsNaN(value.y) && !float.IsNaN(value.z)
            && !float.IsInfinity(value.x) && !float.IsInfinity(value.y) && !float.IsInfinity(value.z);
    }

    private bool CanProcessGameplayHit(bool allowServe = true)
    {
        if (gameState == null)
            return true;

        if (!gameState.IsStarted)
            return false;

        if (gameState.State == GameStateManager.RallyState.InPlay)
            return true;

        return allowServe && gameState.State == GameStateManager.RallyState.WaitingToServe;
    }

    private bool IsFlickAssistModeActive()
    {
        return enableFlick
            && imuController != null
            && imuController.IsActive
            && _lastMode != "Camera fallback";
    }

    private bool HasActiveHitControlSource()
    {
        // In editor, keep mouse/camera interaction available for quick testing.
        if (Application.isEditor && useMouseInEditor)
            return true;

        bool imuActive = imuController != null && imuController.IsActive;
        bool qrReady = qrTrackedRacket != null
            && (qrTrackedRacket.gameObject.activeInHierarchy || qrEverTracked);
        return imuActive || qrReady;
    }

    private bool TryProximityHit()
    {
        if (!CanProcessGameplayHit())
            return false;

        if (!HasActiveHitControlSource())
            return false;

        Rigidbody candidateBall = GetBallRigidbody();

        if (candidateBall == null)
        {
            return false;
        }

        Vector3 ballPosition = candidateBall.worldCenterOfMass;
        Vector3 closestPointOnPaddle = GetClosestPointOnPaddle(ballPosition);
        float distance = Vector3.Distance(closestPointOnPaddle, ballPosition);
        bool imuAssistActive = IsFlickAssistModeActive();
        bool waitingToServe = enableWaitingToServeAssist
            && gameState != null
            && gameState.State == GameStateManager.RallyState.WaitingToServe;

        float effectiveHitDistance = enableProximityFallback ? proximityHitDistance : 0f;
        if (enableQrProximityAssist && qrPoseDrivenMode)
            effectiveHitDistance = Mathf.Max(effectiveHitDistance, qrProximityHitDistance);
        if (waitingToServe)
        {
            effectiveHitDistance = Mathf.Max(effectiveHitDistance, waitingToServeHitDistance);
        }
        if (gameState != null
            && gameState.Mode == GameStateManager.GameMode.GodMode
            && effectiveHitDistance > 0f)
        {
            effectiveHitDistance += Mathf.Max(0f, godModeAssistRadiusBonus);
        }
        bool ballWithinFlickAssistVolume = false;
        if (imuAssistActive)
        {
            ballWithinFlickAssistVolume = IsWithinFlickAssistVolume(ballPosition, out _, out _, out _);
            if (ballWithinFlickAssistVolume)
                effectiveHitDistance = Mathf.Max(effectiveHitDistance, flickRadius);
        }

        if (effectiveHitDistance <= 0f)
            return false;

        if (distance <= effectiveHitDistance)
        {
            // Normal points from the paddle surface toward the ball COM.
            Vector3 toball = ballPosition - closestPointOnPaddle;
            Vector3 surfaceNormal = toball.sqrMagnitude > 0.0001f
                ? toball.normalized
                : transform.TransformDirection(localFaceNormal).normalized;

            // Draw a debug sphere at the contact point so you can see the hit in Scene view.
            Debug.DrawLine(closestPointOnPaddle, ballPosition, Color.yellow, 0.1f);

            float hitVelocityMultiplier = playerHitVelocityMultiplier;
            if (imuAssistActive && ballWithinFlickAssistVolume)
                hitVelocityMultiplier = Mathf.Max(hitVelocityMultiplier, flickVelocityMultiplier);

            ApplyHitImpulse(candidateBall, closestPointOnPaddle, surfaceNormal, hitVelocityMultiplier);
            return true;
        }

        return false;
    }

    private void ApplyQrCalibrationPose(
        Vector3 markerPosition,
        Quaternion markerRotation,
        out Vector3 calibratedPosition,
        out Quaternion calibratedRotation)
    {
        calibratedRotation = markerRotation * Quaternion.Euler(qrRotationOffsetEuler);
        calibratedPosition = markerPosition + calibratedRotation * qrMarkerLocalOffset;
    }

    private void ResolveVisualRacketPoseFromPhysicsPose(
        Vector3 calibratedPosition,
        Quaternion calibratedRotation,
        out Vector3 markerPosition,
        out Quaternion markerRotation)
    {
        Quaternion calibrationRotation = Quaternion.Euler(qrRotationOffsetEuler);
        markerRotation = calibratedRotation * Quaternion.Inverse(calibrationRotation);
        markerPosition = calibratedPosition - calibratedRotation * qrMarkerLocalOffset;
    }

    /// <summary>
    /// IMU-assist flick: when the ball is within an opponent-facing cylinder of
    /// radius <see cref="flickRadius"/> and forward range <see cref="flickAssistRange"/>
    /// from the paddle face centre, and the player is actively swinging
    /// (IMU speed ≥ flickMinSwingSpeed), applies a directed impulse that steers
    /// the ball toward the bot side.
    ///
    /// This compensates for AR positional error that can cause collision-based detection
    /// to miss the ball or accidentally push it back toward the player.
    ///
    /// Only active when IMU is running. Works in all game modes.
    /// Uses the same <see cref="ApplyHitImpulse"/> solver as normal hits, so restitution,
    /// max speed, spin, cooldown, and game-state registration all apply identically.
    /// </summary>
    private void TryFlickAssist()
    {
        if (!CanProcessGameplayHit())
            return;

        if (!IsFlickAssistModeActive()) return;
        if (Time.time - lastFlickTime < flickCooldown) return;
        if (Time.time - lastHitTime < hitCooldown) return;

        // Require a minimum swing speed to prevent accidental triggers when holding still.
        // Wrist angular velocity is weighted by the IMU-to-face lever arm length.
        float swingSpeed = imuController.PaddleVelocity.magnitude
                         + imuController.PaddleAngularVelocity.magnitude * imuToFaceDistance;
        if (swingSpeed < flickMinSwingSpeed) return;

        // Locate the ball (uses own search fallback so flick works even when
        // enableProximityFallback is false and trackedBall is unassigned).
        Rigidbody ballRb = GetBallRigidbody();
        if (ballRb == null) return;

        // Spatial check: ball must be inside an opponent-facing cylinder
        // extending forward from the paddle face centre.
        if (!IsWithinFlickAssistVolume(ballRb.worldCenterOfMass, out Vector3 faceCenter, out float radialDistance, out float forwardDistance))
        {
            return;
        }

        Vector3 toBall = ballRb.worldCenterOfMass - faceCenter;

        // Side check: ball must be roughly in front of the paddle face, not behind it.
        // Tolerance of -0.3 allows the ball to be slightly off-axis to the side.
        Vector3 worldFaceNormal = transform.TransformDirection(localFaceNormal).normalized;
        if (toBall.sqrMagnitude > 0.0001f && Vector3.Dot(worldFaceNormal, toBall.normalized) < -0.3f)
            return;

        // Resolve base direction from swing intent (toward/away) projected on the
        // court forward axis, then add uplift for arc.
        Vector3 baseDir = ResolveFlickBaseDirection(faceCenter, ballRb.worldCenterOfMass);
        Vector3 flickDir = (baseDir + Vector3.up * flickUplift).normalized;

        // Use a synthetic contact point behind the ball along flickDir so the
        // impulse solver's normal-sanitization step preserves our intended direction.
        float contactBackstep = Mathf.Max(0.02f, flickContactBackstep);
        Vector3 assistContactPoint = ballRb.worldCenterOfMass - flickDir * contactBackstep;

        Debug.Log($"[Flick] Assist triggered: swingSpd={swingSpeed:F2} radial={radialDistance:F3} forward={forwardDistance:F3} dir={flickDir}");

        // paddleVelocity is already populated from IMU data in the current mode block,
        // so the impulse solver naturally scales with the player's actual swing speed.
        ApplyHitImpulse(ballRb, assistContactPoint, flickDir, flickVelocityMultiplier);
        lastFlickTime = Time.time;
    }

    private Vector3 ResolveFlickBaseDirection(Vector3 faceCenter, Vector3 ballPosition)
    {
        Vector3 opponentForward = ResolveOpponentForwardDirection();
        if (opponentForward.sqrMagnitude < 0.0001f)
            opponentForward = Vector3.forward;
        opponentForward.y = 0f;
        opponentForward.Normalize();

        // Primary: infer toward/away intent from swing along court axis.
        Vector3 swingPlanar = paddleVelocity;
        swingPlanar.y = 0f;
        float swingAlongCourt = Vector3.Dot(swingPlanar, opponentForward);
        if (Mathf.Abs(swingAlongCourt) >= Mathf.Max(0.001f, flickDirectionalDeadzone))
            return swingAlongCourt >= 0f ? opponentForward : -opponentForward;

        // Secondary: use which side of the paddle the ball is on relative to court axis.
        Vector3 toBall = ballPosition - faceCenter;
        toBall.y = 0f;
        if (toBall.sqrMagnitude > 0.0001f)
            return Vector3.Dot(toBall, opponentForward) >= 0f ? opponentForward : -opponentForward;

        return opponentForward;
    }

    private bool IsWithinFlickAssistVolume(
        Vector3 ballPosition,
        out Vector3 faceCenter,
        out float radialDistance,
        out float forwardDistance)
    {
        faceCenter = ResolveFlickCylinderCenterWorld();
        radialDistance = float.PositiveInfinity;
        forwardDistance = 0f;

        Vector3 opponentForward = ResolveOpponentForwardDirection();
        if (opponentForward.sqrMagnitude < 0.0001f)
            opponentForward = Vector3.forward;
        opponentForward.y = 0f;
        if (opponentForward.sqrMagnitude < 0.0001f)
            opponentForward = Vector3.forward;
        opponentForward.Normalize();

        Vector3 toBall = ballPosition - faceCenter;
        forwardDistance = Vector3.Dot(toBall, opponentForward);
        if (forwardDistance < 0f || forwardDistance > Mathf.Max(0f, flickAssistRange))
            return false;

        Vector3 radialVector = toBall - opponentForward * forwardDistance;
        radialDistance = radialVector.magnitude;
        return radialDistance <= Mathf.Max(0f, flickRadius);
    }

    private Vector3 ResolveFlickCylinderCenterWorld()
    {
        Vector3 localCenter = paddleColliderCenter;

        BoxCollider box = GetComponent<BoxCollider>();
        if (box != null)
            localCenter = box.center;

        localCenter += flickCylinderLocalOffset;
        return transform.TransformPoint(localCenter);
    }

    private void UpdateFlickAssistDebugVisual()
    {
        if (!showFlickAssistDebugVolume || !enableFlick || !isActiveAndEnabled || !HasLiveFlickAssistDebugAnchor())
        {
            DestroyFlickAssistDebugVisual();
            return;
        }

        EnsureFlickAssistDebugVisual();
        if (flickAssistDebugVisual == null)
            return;

        Vector3 faceCenter = ResolveFlickCylinderCenterWorld();
        Vector3 opponentForward = ResolveOpponentForwardDirection();
        if (opponentForward.sqrMagnitude < 0.0001f)
            opponentForward = transform.forward;
        if (opponentForward.sqrMagnitude < 0.0001f)
            opponentForward = Vector3.forward;
        opponentForward.Normalize();

        float radius = Mathf.Max(0.001f, flickRadius);
        float range = Mathf.Max(0.001f, flickAssistRange);

        Transform visualTransform = flickAssistDebugVisual.transform;
        visualTransform.position = faceCenter + opponentForward * (range * 0.5f);
        visualTransform.rotation = Quaternion.FromToRotation(Vector3.up, opponentForward);
        visualTransform.localScale = new Vector3(radius * 2f, range * 0.5f, radius * 2f);

        ApplyDebugMaterialColor();
        UpdateFlickAssistDebugCaps(faceCenter, opponentForward, radius, range);
        UpdateFlickAssistDebugAxis(faceCenter, opponentForward, range);
    }

    private void EnsureFlickAssistDebugVisual()
    {
        if (flickAssistDebugVisual != null)
            return;

        flickAssistDebugVisual = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        flickAssistDebugVisual.name = "_FlickAssistDebugCylinder";
        flickAssistDebugVisual.hideFlags = HideFlags.DontSave;

        Collider debugCollider = flickAssistDebugVisual.GetComponent<Collider>();
        if (debugCollider != null)
            Destroy(debugCollider);

        Renderer renderer = flickAssistDebugVisual.GetComponent<Renderer>();
        if (renderer == null)
            return;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null)
            return;

        flickAssistDebugMaterial = new Material(shader)
        {
            hideFlags = HideFlags.DontSave
        };

        ConfigureDebugMaterialForTransparency(flickAssistDebugMaterial);
        renderer.sharedMaterial = flickAssistDebugMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

        ApplyDebugMaterialColor();
    }

    private void UpdateFlickAssistDebugCaps(Vector3 faceCenter, Vector3 opponentForward, float radius, float range)
    {
        EnsureFlickAssistDebugCaps();
        if (flickAssistDebugStartCapVisual == null || flickAssistDebugEndCapVisual == null)
            return;

        Quaternion capRotation = Quaternion.FromToRotation(Vector3.up, opponentForward);
        Vector3 endCenter = faceCenter + opponentForward * range;
        float capThickness = 0.01f;
        Vector3 capScale = new Vector3(radius * 2f, capThickness * 0.5f, radius * 2f);

        flickAssistDebugStartCapVisual.transform.SetPositionAndRotation(faceCenter, capRotation);
        flickAssistDebugStartCapVisual.transform.localScale = capScale;

        flickAssistDebugEndCapVisual.transform.SetPositionAndRotation(endCenter, capRotation);
        flickAssistDebugEndCapVisual.transform.localScale = capScale;
    }

    private void EnsureFlickAssistDebugCaps()
    {
        if (flickAssistDebugStartCapVisual == null)
            flickAssistDebugStartCapVisual = CreateFlickAssistDebugCap("_FlickAssistDebugStartCap");

        if (flickAssistDebugEndCapVisual == null)
            flickAssistDebugEndCapVisual = CreateFlickAssistDebugCap("_FlickAssistDebugEndCap");
    }

    private GameObject CreateFlickAssistDebugCap(string objectName)
    {
        GameObject cap = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        cap.name = objectName;
        cap.hideFlags = HideFlags.DontSave;

        Collider debugCollider = cap.GetComponent<Collider>();
        if (debugCollider != null)
            Destroy(debugCollider);

        Renderer renderer = cap.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.sharedMaterial = flickAssistDebugMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
        }

        return cap;
    }

    private bool HasLiveFlickAssistDebugAnchor()
    {
        bool imuActive = IsFlickAssistModeActive();
        bool qrActive = qrActivelyTracking
            && qrTrackedRacket != null
            && qrTrackedRacket.gameObject.activeInHierarchy;
        return imuActive || qrActive;
    }

    private void UpdateFlickAssistDebugAxis(Vector3 faceCenter, Vector3 opponentForward, float range)
    {
        EnsureFlickAssistDebugAxis();
        if (flickAssistDebugAxisRenderer == null)
            return;

        Color axisColor = new Color(
            flickAssistDebugColor.r,
            flickAssistDebugColor.g,
            flickAssistDebugColor.b,
            Mathf.Clamp01(Mathf.Max(flickAssistDebugColor.a, 0.65f)));

        flickAssistDebugAxisRenderer.startColor = axisColor;
        flickAssistDebugAxisRenderer.endColor = axisColor;
        flickAssistDebugAxisRenderer.SetPosition(0, faceCenter);
        flickAssistDebugAxisRenderer.SetPosition(1, faceCenter + opponentForward * range);
    }

    private void EnsureFlickAssistDebugAxis()
    {
        if (flickAssistDebugAxisRenderer != null)
            return;

        flickAssistDebugAxisVisual = new GameObject("_FlickAssistDebugAxis");
        flickAssistDebugAxisVisual.hideFlags = HideFlags.DontSave;
        flickAssistDebugAxisRenderer = flickAssistDebugAxisVisual.AddComponent<LineRenderer>();
        flickAssistDebugAxisRenderer.useWorldSpace = true;
        flickAssistDebugAxisRenderer.positionCount = 2;
        flickAssistDebugAxisRenderer.widthMultiplier = 0.01f;
        flickAssistDebugAxisRenderer.numCapVertices = 4;
        flickAssistDebugAxisRenderer.shadowCastingMode = ShadowCastingMode.Off;
        flickAssistDebugAxisRenderer.receiveShadows = false;
        flickAssistDebugAxisRenderer.lightProbeUsage = LightProbeUsage.Off;
        flickAssistDebugAxisRenderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

        Shader lineShader = Shader.Find("Sprites/Default");
        if (lineShader == null)
            lineShader = Shader.Find("Unlit/Color");
        if (lineShader != null)
        {
            Material lineMaterial = new Material(lineShader)
            {
                hideFlags = HideFlags.DontSave
            };
            flickAssistDebugAxisRenderer.sharedMaterial = lineMaterial;
        }
    }

    private void ApplyDebugMaterialColor()
    {
        if (flickAssistDebugMaterial == null)
            return;

        if (flickAssistDebugMaterial.HasProperty("_BaseColor"))
            flickAssistDebugMaterial.SetColor("_BaseColor", flickAssistDebugColor);
        if (flickAssistDebugMaterial.HasProperty("_Color"))
            flickAssistDebugMaterial.SetColor("_Color", flickAssistDebugColor);
    }

    private static void ConfigureDebugMaterialForTransparency(Material material)
    {
        if (material == null)
            return;

        if (material.HasProperty("_Surface"))
            material.SetFloat("_Surface", 1f);
        if (material.HasProperty("_Blend"))
            material.SetFloat("_Blend", 0f);
        if (material.HasProperty("_Mode"))
            material.SetFloat("_Mode", 3f);
        if (material.HasProperty("_SrcBlend"))
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        if (material.HasProperty("_DstBlend"))
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (material.HasProperty("_ZWrite"))
            material.SetFloat("_ZWrite", 0f);
        if (material.HasProperty("_Cull"))
            material.SetFloat("_Cull", (float)CullMode.Off);

        material.renderQueue = (int)RenderQueue.Transparent;
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.DisableKeyword("_ALPHATEST_ON");
        material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        material.DisableKeyword("_ALPHAMODULATE_ON");
    }

    private void DestroyFlickAssistDebugVisual()
    {
        if (flickAssistDebugVisual != null)
            Destroy(flickAssistDebugVisual);
        flickAssistDebugVisual = null;

        if (flickAssistDebugMaterial != null)
            Destroy(flickAssistDebugMaterial);
        flickAssistDebugMaterial = null;

        if (flickAssistDebugStartCapVisual != null)
            Destroy(flickAssistDebugStartCapVisual);
        flickAssistDebugStartCapVisual = null;

        if (flickAssistDebugEndCapVisual != null)
            Destroy(flickAssistDebugEndCapVisual);
        flickAssistDebugEndCapVisual = null;

        if (flickAssistDebugAxisRenderer != null && flickAssistDebugAxisRenderer.sharedMaterial != null)
            Destroy(flickAssistDebugAxisRenderer.sharedMaterial);
        if (flickAssistDebugAxisVisual != null)
            Destroy(flickAssistDebugAxisVisual);
        flickAssistDebugAxisVisual = null;
        flickAssistDebugAxisRenderer = null;
    }

    private Vector3 ResolveOpponentForwardDirection()
    {
        Transform gameSpaceRoot = ResolveGameSpaceRoot();
        ARPlaneGameSpacePlacer placer = FindFirstObjectByType<ARPlaneGameSpacePlacer>();
        bool canUseCourtDirection = gameSpaceRoot != null
            && (placer == null || placer.GameSpaceRoot != gameSpaceRoot || placer.IsPlaced);

        if (canUseCourtDirection)
        {
            Vector3 courtForward = gameSpaceRoot.TransformDirection(Vector3.forward);
            courtForward.y = 0f;
            if (courtForward.sqrMagnitude > 0.0001f)
                return courtForward.normalized;
        }

        Vector3 fallbackForward = cameraTransform != null ? cameraTransform.forward : transform.forward;
        fallbackForward.y = 0f;
        if (fallbackForward.sqrMagnitude < 0.0001f)
        {
            fallbackForward = transform.forward;
            fallbackForward.y = 0f;
        }
        if (fallbackForward.sqrMagnitude < 0.0001f)
            fallbackForward = Vector3.forward;

        return fallbackForward.normalized;
    }

    private Transform ResolveGameSpaceRoot(bool forceRefresh = false)
    {
        if (!forceRefresh && cachedGameSpaceRoot != null)
            return cachedGameSpaceRoot;

        GameObject rootByName = GameObject.Find("GameSpaceRoot");
        if (rootByName != null)
        {
            cachedGameSpaceRoot = rootByName.transform;
            return cachedGameSpaceRoot;
        }

        BotHitController bot = FindFirstObjectByType<BotHitController>();
        if (bot != null && bot.transform.parent != null)
        {
            cachedGameSpaceRoot = bot.transform.parent;
            return cachedGameSpaceRoot;
        }

        return null;
    }

    private Vector3 GetClosestPointOnPaddle(Vector3 worldPoint)
    {
        if (paddleColliders == null || paddleColliders.Length == 0)
        {
            return transform.position;
        }

        Vector3 bestPoint = transform.position;
        float bestDistance = float.MaxValue;

        for (int index = 0; index < paddleColliders.Length; index++)
        {
            Collider paddleCollider = paddleColliders[index];
            if (paddleCollider == null || !paddleCollider.enabled)
            {
                continue;
            }

            Vector3 candidatePoint;

            // ClosestPoint only works on Box/Sphere/Capsule and CONVEX MeshColliders.
            // For non-convex MeshColliders, fall back to the collider's AABB closest point,
            // which is a good enough approximation for the proximity-hit normal direction.
            MeshCollider mc = paddleCollider as MeshCollider;
            if (mc != null && !mc.convex)
            {
                candidatePoint = paddleCollider.bounds.ClosestPoint(worldPoint);
            }
            else
            {
                candidatePoint = paddleCollider.ClosestPoint(worldPoint);
            }

            float candidateDistance = (candidatePoint - worldPoint).sqrMagnitude;
            if (candidateDistance < bestDistance)
            {
                bestDistance = candidateDistance;
                bestPoint = candidatePoint;
            }
        }

        return bestPoint;
    }

    private Vector3 GetTargetWorldPosition()
    {
        Camera sourceCamera = cameraTransform.GetComponent<Camera>();

        Vector3 localOffset;
        if (sourceCamera != null)
        {
            bool isDeviceBuild = !Application.isEditor;
            bool useMouseInput = useMouseInEditor && !isDeviceBuild;

            if (lockCursorOnPlay)
            {
                useMouseInput = false;
            }

            if (isDeviceBuild && useCameraRelativePointOnDevice)
            {
                useMouseInput = false;
            }

            Vector3 screenPoint = useMouseInput
                ? Input.mousePosition
                : new Vector3(Screen.width * defaultScreenX, Screen.height * defaultScreenY, 0f);

            Ray mouseRay = sourceCamera.ScreenPointToRay(screenPoint);
            Plane paddlePlane = new Plane(cameraTransform.forward, cameraTransform.position + cameraTransform.forward * depthFromCamera);

            if (paddlePlane.Raycast(mouseRay, out float enterDistance))
            {
                Vector3 hitPoint = mouseRay.GetPoint(enterDistance);
                localOffset = cameraTransform.InverseTransformPoint(hitPoint);
            }
            else
            {
                localOffset = new Vector3(depthFromCamera * 0.5f, -0.1f, depthFromCamera);
            }
        }
        else
        {
            localOffset = new Vector3(depthFromCamera * 0.5f, -0.1f, depthFromCamera);
        }

        localOffset.z = depthFromCamera;
        localOffset.x = Mathf.Clamp(localOffset.x, -horizontalRange, horizontalRange);
        localOffset.y = Mathf.Clamp(localOffset.y, -verticalRange, verticalRange);

        return cameraTransform.TransformPoint(localOffset);
    }

    private Quaternion GetTargetWorldRotation(Vector3 worldPosition)
    {
        Vector3 toPaddle = worldPosition - cameraTransform.position;
        if (toPaddle.sqrMagnitude < 0.0001f)
        {
            return cameraTransform.rotation * Quaternion.Euler(baseLocalEuler);
        }

        Quaternion lookRotation = Quaternion.LookRotation(toPaddle.normalized, Vector3.up);
        return lookRotation * Quaternion.Euler(baseLocalEuler);
    }

    // ── Paddle-side collision callbacks (secondary path) ─────────────────────────
    // NOTE: These fire on the PADDLE (kinematic), which Unity does not always
    // guarantee for kinematic-vs-dynamic contacts.  BallContactDetector on the
    // ball is the primary, reliable path.  These act as an extra safety net.

    private void OnCollisionEnter(Collision collision)
    {
        HandlePaddleCollision(collision);
    }

    private void OnCollisionStay(Collision collision)
    {
        HandlePaddleCollision(collision);
    }

    private void HandlePaddleCollision(Collision collision)
    {
        if (!CanProcessGameplayHit())
        {
            return;
        }

        if (collision.contactCount == 0 || collision.rigidbody == null)
        {
            return;
        }

        ContactPoint contact = collision.GetContact(0);

        // From the PADDLE's OnCollision, contact.normal points FROM ball INTO paddle.
        // Negate it to get the outward paddle-surface normal (paddle → ball).
        Vector3 surfaceNormal = -contact.normal;
        ApplyHitImpulse(collision.rigidbody, contact.point, surfaceNormal, playerHitVelocityMultiplier);
    }

    private void OnTriggerEnter(Collider other)
    {
        // Track kitchen zone entry
        var boundary = other.GetComponent<CourtBoundary>();
        if (boundary != null)
        {
            if (boundary.boundaryType == CourtBoundary.BoundaryType.Kitchen)
                isInKitchen = true;
            return;
        }
        HandlePaddleTrigger(other);
    }

    private void OnTriggerStay(Collider other)
    {
        if (other.GetComponent<CourtBoundary>() != null) return;
        HandlePaddleTrigger(other);
    }

    private void OnTriggerExit(Collider other)
    {
        var boundary = other.GetComponent<CourtBoundary>();
        if (boundary != null && boundary.boundaryType == CourtBoundary.BoundaryType.Kitchen)
            isInKitchen = false;
    }

    private void HandlePaddleTrigger(Collider other)
    {
        if (!CanProcessGameplayHit())
        {
            return;
        }

        Rigidbody otherRigidbody = other.attachedRigidbody;
        if (otherRigidbody == null)
        {
            return;
        }

        Vector3 contactPoint = other.ClosestPoint(transform.position);
        // Derive normal from paddle surface → ball centre of mass.
        Vector3 toball = otherRigidbody.worldCenterOfMass - contactPoint;
        Vector3 surfaceNormal = toball.sqrMagnitude > 0.0001f
            ? toball.normalized
            : transform.TransformDirection(localFaceNormal).normalized;
        ApplyHitImpulse(otherRigidbody, contactPoint, surfaceNormal, playerHitVelocityMultiplier);
    }

    /// <summary>
    /// Public entry point for all hit detection paths (BallContactDetector, proximity
    /// fallback, and the paddle-side collision callbacks).
    ///
    /// <paramref name="surfaceNormal"/> must point FROM the paddle surface TOWARD the
    /// ball centre of mass.  This is the outward paddle normal at the contact point.
    /// </summary>
    public void ApplyHitImpulse(
        Rigidbody ballBody,
        Vector3 contactPoint,
        Vector3 surfaceNormal,
        float surfaceVelocityMultiplier = 2f)
    {
        if (ballBody == null)
        {
            return;
        }

        if (!CanProcessGameplayHit())
        {
            return;
        }

        if (!HasActiveHitControlSource())
        {
            return;
        }

        if (requireBallTag && !ballBody.gameObject.CompareTag(ballTag))
        {
            return;
        }

        if (!ballBody.gameObject.activeInHierarchy || ballBody.isKinematic)
        {
            return;
        }

        if (!IsFiniteVector(ballBody.position)
            || !IsFiniteVector(ballBody.linearVelocity)
            || !IsFiniteVector(contactPoint)
            || !IsFiniteVector(surfaceNormal))
        {
            return;
        }

        if (Time.time - lastHitTime < hitCooldown)
        {
            return;
        }

        // ── Kitchen violation check ───────────────────────────────────────────────
        // Non-volley zone: a volley from the player's kitchen side is a fault.
        // Keep the shared kitchen geometry, but gate faults by side + volley state.
        if (isInKitchen && gameState != null)
        {
            Transform gameSpaceRoot = ResolveGameSpaceRoot();
            float netLocalZ = gameState.GetNetLocalZ();
            float paddleLocalZ = gameSpaceRoot != null
                ? gameSpaceRoot.InverseTransformPoint(transform.position).z
                : transform.position.z;
            bool paddleOnPlayerSide = paddleLocalZ < netLocalZ;

            if (paddleOnPlayerSide)
            {
                PracticeBallController ballController = GetBallController();
                bool isVolley = ballController != null && ballController.GetBounceCount() == 0;
                if (isVolley)
                {
                    gameState.OnKitchenViolation();
                    lastHitTime = Time.time;
                    return;
                }
            }
        }

        // ── Sanitise the surface normal ───────────────────────────────────────────
        // Ensure it actually points from the paddle toward the ball COM.
        // If the provided normal points the wrong way (can happen with trigger overlaps),
        // flip it so the impulse always pushes the ball away from the paddle.
        Vector3 faceNormal = surfaceNormal.normalized;
        Vector3 toBallCOM = ballBody.worldCenterOfMass - contactPoint;
        if (Vector3.Dot(faceNormal, toBallCOM) < 0f)
        {
            faceNormal = -faceNormal;
        }

        // ── Paddle surface velocity at the contact point ──────────────────────────
        // v_surface = v_paddle + ω_paddle × (contactPoint − paddleCOM)
        // Capturing the rotational contribution means a wrist-snap adds exactly the
        // right tangential speed at the edge of the paddle face.
        Vector3 paddleCOM = paddleRigidbody != null
            ? paddleRigidbody.worldCenterOfMass
            : transform.position;

        // In IMU modes, linear velocity is measured at the handle-mounted sensor.
        // Use the handle pivot as the angular-velocity reference point so wrist
        // rotation contributes correctly without relocating the entire paddle.
        Vector3 velocityReferencePoint = paddleCOM;
        if (imuController != null && imuController.IsActive)
        {
            Quaternion currentRotation = paddleRigidbody != null ? paddleRigidbody.rotation : transform.rotation;
            Vector3 currentPosition = paddleRigidbody != null ? paddleRigidbody.position : transform.position;
            velocityReferencePoint = currentPosition + currentRotation * imuController.imuPivotLocalOffset;
        }

        float clampedSurfaceVelocityMultiplier = Mathf.Max(0f, surfaceVelocityMultiplier);
        Vector3 paddleContactVelocity = (
            paddleVelocity + Vector3.Cross(paddleAngularVelocity, contactPoint - velocityReferencePoint))
            * clampedSurfaceVelocityMultiplier;

        // ── Relative velocity of the ball w.r.t. the paddle surface ──────────────
        Vector3 relativeVelocity = ballBody.linearVelocity - paddleContactVelocity;
        float vN = Vector3.Dot(relativeVelocity, faceNormal);

        // Guard: only apply an impulse when the paddle is moving INTO the ball
        // (vN < 0) OR when the paddle is actively approaching (paddleVelocity toward
        // ball has sufficient magnitude).  This prevents phantom impulses when the
        // paddle is withdrawing after a hit but the cooldown hasn't expired.
        //
        // We use a small positive tolerance (0.05 m/s) to accept near-zero relative
        // velocity contacts, e.g. a stationary paddle resting against the ball.
        // During serve setup, use a wider tolerance so gentle contacts still register.
        bool waitingToServe = gameState != null
            && gameState.State == GameStateManager.RallyState.WaitingToServe;
        float separatingTolerance = waitingToServe ? 0.1f : 0.05f;
        if (vN > separatingTolerance)
        {
            return;
        }

        // ── Normal impulse (COR model, infinite-mass paddle approximation) ────────
        // Δv_n = −(1 + e) · vN · n
        float vNClamped = Mathf.Min(vN, 0f); // cap at 0 to handle the tolerance case
        Vector3 normalDeltaV = -(1f + restitution) * vNClamped * faceNormal;

        // ── Tangential impulse (Coulomb friction) ─────────────────────────────────
        // The ball slides across the paddle face; friction opposes the sliding
        // direction and is clamped to the Coulomb cone: |Δv_t| ≤ μ·|Δv_n|.
        Vector3 tangentialRelVel = relativeVelocity - vNClamped * faceNormal;
        float tangentialSpeed = tangentialRelVel.magnitude;

        Vector3 tangentialDeltaV = Vector3.zero;
        if (tangentialSpeed > 0.001f)
        {
            float frictionLimit = frictionCoefficient * normalDeltaV.magnitude;
            float frictionMag = Mathf.Min(frictionLimit, tangentialSpeed);
            tangentialDeltaV = -(tangentialRelVel / tangentialSpeed) * frictionMag;
        }

        // ── Compose & apply velocity impulse ──────────────────────────────────────
        Vector3 newVelocity = ballBody.linearVelocity + normalDeltaV + tangentialDeltaV;

        if (newVelocity.magnitude > maxBallSpeed)
        {
            newVelocity = newVelocity.normalized * maxBallSpeed;
        }

        // ForceMode.VelocityChange applies Δv directly, independent of ball mass.
        ballBody.AddForce(newVelocity - ballBody.linearVelocity, ForceMode.VelocityChange);
        // ── Angular impulse (spin) ────────────────────────────────────────────────
        // 1. Off-centre contact: tangential impulse × lever arm from ball COM.
        //    Δω ≈ spinFromOffCenter · (r_contact × Δv_t)   [hollow sphere factor]
        Vector3 contactOffset = contactPoint - ballBody.worldCenterOfMass;
        Vector3 spinOffCenter = spinFromOffCenter * Vector3.Cross(contactOffset, tangentialDeltaV);

        // 2. Paddle rotation (wrist snap): transmits spin via surface friction.
        //    Δω ≈ spinFromTangential · (n × ω_paddle)
        Vector3 spinWrist = spinFromTangential * Vector3.Cross(faceNormal, paddleAngularVelocity);

        ballBody.AddTorque(spinOffCenter + spinWrist, ForceMode.VelocityChange);

        lastHitTime = Time.time;

        // ── Classify the shot type ──────────────────────────────────────────────
        ShotType shotType = ShotClassifier.Classify(
            paddleContactVelocity.magnitude, newVelocity);

        Debug.Log($"[Hit] vN={vN:F2}  paddleSpeed={paddleVelocity.magnitude:F2} m/s" +
                  $"  paddleContactVel={paddleContactVelocity.magnitude:F2} m/s" +
                  $"  ball-out={newVelocity.magnitude:F1} m/s" +
                  $"  normalDeltaV={normalDeltaV.magnitude:F2}" +
                  $"  shotType={shotType}");
        LogDebugProximityHit(ballBody, contactPoint, newVelocity);

        // ── Publish ball state to ML via MQTT ───────────────────────────────────
        if (mqttController != null)
        {
            mqttController.PublishPlayerBall(ballBody.position, newVelocity);
            mqttController.PublishHitAcknowledge();
        }
        else
        {
            Debug.LogWarning("[PaddleHit] mqttController is null — ball state NOT sent to ML.");
        }

        // ── Register hit with game state ─────────────────────────────────────
        if (gameState != null)
        {
            gameState.RegisterPlayerHit(shotType);
        }
    }
}
