using UnityEngine;

public class PaddleHitController : MonoBehaviour
{
    [Header("References")]
    public Transform cameraTransform;

    [Header("IMU-Tracked Racket (Hardware Mode)")]
    [Tooltip("When set and active, the paddle is driven by IMU sensor data via MQTT. " +
             "Takes priority over QR and camera modes.")]
    public ImuPaddleController imuController;

    [Header("MQTT Integration")]
    [Tooltip("When set, publishes ball state to /playerBall after each hit.")]
    public MqttController mqttController;

    [Header("Game State")]
    [Tooltip("When set, reports hits and checks kitchen violations.")]
    public GameStateManager gameState;

    [Header("QR-Tracked Racket (AR Mode)")]
    [Tooltip("When set, the physics paddle teleports to this transform every FixedUpdate " +
             "instead of using camera-relative positioning. Assign this at runtime " +
             "when the QR-spawned racket is detected.")]
    public Transform qrTrackedRacket;

    [Header("QR Calibration")]
    [Tooltip("Local-space offset (meters) from the QR marker origin to the desired physics paddle center. " +
             "Use this to align collider hits with the rendered racket when the marker is mounted off-center.")]
    public Vector3 qrMarkerLocalOffset = Vector3.zero;
    [Tooltip("Additional rotation offset (degrees) applied to QR marker rotation before driving the physics paddle.")]
    public Vector3 qrRotationOffsetEuler = Vector3.zero;
    [Tooltip("When true, QR-driven modes can use a larger proximity radius to compensate for AR drift.")]
    public bool enableQrProximityAssist = true;
    [Tooltip("Proximity hit distance used only in QR-driven modes (meters).")]
    public float qrProximityHitDistance = 0.6f;

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

    [Header("Fallback Detection")]
    public Rigidbody trackedBall;
    public bool enableProximityFallback = true;
    public float proximityHitDistance = 0.5f;

    [Header("Serve Assist")]
    [Tooltip("When true, expands proximity hit detection only while waiting for serve.")]
    public bool enableWaitingToServeAssist = true;
    [Tooltip("Proximity hit distance used only in WaitingToServe to make serves easier.")]
    public float waitingToServeHitDistance = 1.0f;

    [Header("Flick Assist (IMU Only)")]
    [Tooltip("When IMU is active and the ball is within flickRadius of the paddle face, " +
             "applies a directed hit toward the bot side. Compensates for AR positional error.")]
    public bool enableFlick = true;
    [Tooltip("Ball must be within this radius of the paddle face centre to trigger a flick (metres). " +
             "Applied as an opponent-facing hemisphere, not a full sphere.")]
    public float flickRadius = 0.2f;
    [Tooltip("Minimum swing speed (linear + wrist contribution) to trigger a flick. " +
             "Prevents accidental triggers while holding still.")]
    public float flickMinSwingSpeed = 0.5f;
    [Tooltip("Upward tilt added to the flick direction so the ball arcs over the net. " +
             "0 = flat horizontal, 0.3 = natural arc.")]
    [Range(0f, 0.8f)]
    public float flickUplift = 0.25f;
    [Tooltip("Minimum seconds between flick triggers. Prevents rapid re-triggers in one swing.")]
    public float flickCooldown = 0.3f;
    [Tooltip("Scales the IMU-derived paddle surface speed used by Flick Assist. " +
             "2.0 means flick hits are solved as if the paddle surface were moving twice as fast.")]
    public float flickVelocityMultiplier = 2f;

    private Rigidbody paddleRigidbody;
    private Collider[] paddleColliders;
    private Vector3 previousPosition;
    private Vector3 paddleVelocity;
    private Vector3 paddleAngularVelocity;
    private float lastHitTime;
    private float lastFlickTime;
    private bool isInKitchen;
    private Rigidbody cachedBallRb;
    private float lastBallSearchTime;
    private Transform cachedGameSpaceRoot;
    private bool qrPoseDrivenMode;

    /// <summary>Clears the cached ball reference so the next proximity check re-searches.</summary>
    public void ClearCachedBall() { cachedBallRb = null; lastBallSearchTime = 0f; }

    // QR position persistence: paddle stays at last known position when QR is lost
    private bool qrEverTracked;
    private Vector3 lastQrPosition;
    private Quaternion lastQrRotation;

    /// <summary>Set by PlaceTrackedImages each frame based on ARTrackedImage.trackingState.</summary>
    [HideInInspector] public bool qrActivelyTracking;

    /// <summary>Time.time when PlaceTrackedImages last set qrActivelyTracking.</summary>
    [HideInInspector] public float lastQrTrackingUpdateTime;

    /// <summary>Rotation offset baked into the QR racket prefab. Set by PlaceTrackedImages on spawn.</summary>
    [HideInInspector] public Quaternion qrPrefabRotOffset = Quaternion.identity;

    [Header("QR Tracking Timeout")]
    [Tooltip("If PlaceTrackedImages hasn't confirmed active QR tracking for this long (seconds), treat as stale.")]
    public float qrTrackingTimeout = 0.2f;

    [Header("IMU Placement")]
    [Tooltip("Distance from IMU (handle/wrist) to paddle face center (meters). 0.3 = 30cm.")]
    public float imuToFaceDistance = 0.3f;

    // Stale QR + IMU: integration state from last QR pose
    private Vector3 stalePosition;
    private Quaternion staleRotation;

    // Mode transition logging
    private string _lastMode;
    private float _lastDiagLogTime;

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

    private void FixedUpdate()
    {
        qrPoseDrivenMode = false;

        // ── Cache QR position ─────────────────────────────────────────────────────
        // Only update cached position when the QR is genuinely being tracked.
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

        // ── QR tracking timeout fallback ────────────────────────────────────────
        // If PlaceTrackedImages hasn't confirmed active tracking for a while,
        // force stale mode (handles edge case where ARFoundation stops firing events)
        if (qrActivelyTracking && lastQrTrackingUpdateTime > 0f
            && Time.time - lastQrTrackingUpdateTime > qrTrackingTimeout)
        {
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
            Debug.Log($"[PaddleHit] DIAG: qrAvail={qrAvailable} qrActive={qrActivelyTracking} " +
                      $"imuCtrl={imuController != null} imuActive={imuActive} " +
                      $"mode={_lastMode ?? "none"} " +
                      $"paddlePos=({pPos.x:F3},{pPos.y:F3},{pPos.z:F3}) " +
                      $"ballPos=({bPos.x:F3},{bPos.y:F3},{bPos.z:F3}) " +
                      $"paddleBallDist={dist:F3} qrToPhysics={qrToPhysics:F3}" +
                      (imuActive ? $" vel={imuController.PaddleVelocity.magnitude:F4}" +
                                   $" angVel={imuController.PaddleAngularVelocity.magnitude:F2}" +
                                   $" worldOff={imuController.HasWorldOffset}" +
                                   $" cal={imuController.IsCalibrated}" : ""));
        }

        // ── Fresh QR + IMU: QR actively tracked ─────────────────────────────────
        // Follow QR strictly for position and rotation (drift-free AR anchor).
        // IMU provides velocity/angular velocity for the hit impulse solver.
        // Continuously learn the IMU-to-world mapping so stale mode works correctly.
        if (qrAvailable && qrActivelyTracking && imuController != null && imuController.IsActive)
        {
            qrPoseDrivenMode = true;
            LogModeTransition("Fresh QR + IMU (strict QR)");

            imuController.ControlsTransform = false;

            // Auto-calibrate IMU-to-world alignment every frame QR is visible.
            // This maps IMU yaw to court/world orientation so stale mode is correct.
            imuController.UpdateWorldOffset(lastQrRotation);

            paddleVelocity = imuController.PaddleVelocity;
            paddleAngularVelocity = imuController.PaddleAngularVelocity;

            // Snapshot for seamless stale-mode transition
            stalePosition = imuController.ResolvePivotWorldPosition(lastQrPosition, lastQrRotation);
            staleRotation = lastQrRotation;

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

            if (enableProximityFallback)
                TryProximityHit();
            TryFlickAssist();
            return;
        }

        // ── Stale QR + IMU: QR lost, integrate from last QR pose ────────────────
        // Position: continue integrating the IMU handle pivot from the last QR-aligned pose.
        // Rotation: use IMU world orientation (QR-learned offset maps IMU yaw to court space).
        // When QR resumes (block above), snaps back to true QR pose.
        if (qrAvailable && !qrActivelyTracking && imuController != null && imuController.IsActive)
        {
            qrPoseDrivenMode = true;
            LogModeTransition("Stale QR + IMU (integration)");

            imuController.ControlsTransform = false;

            paddleVelocity = imuController.PaddleVelocity;
            paddleAngularVelocity = imuController.PaddleAngularVelocity;

            float dt = Mathf.Max(Time.fixedDeltaTime, 0.0001f);

            // Rotation FIRST: use world-space IMU orientation (auto-calibrated from QR).
            // This gives correct yaw alignment with the court because UpdateWorldOffset()
            // was called every frame while QR was active.
            staleRotation = imuController.WorldRotation;

            // Position: integrate the handle-mounted IMU pivot directly.
            stalePosition += paddleVelocity * dt;
            Vector3 appliedPosition = imuController.ResolveTransformPositionFromPivot(stalePosition, staleRotation);

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
                qrTrackedRacket.SetPositionAndRotation(appliedPosition, staleRotation);
            }

            previousPosition = appliedPosition;

            // Periodic stale-mode diagnostic
            if (Time.time - _lastDiagLogTime > 2f)
            {
                _lastDiagLogTime = Time.time;
                Debug.Log($"[PaddleHit] STALE: linVel={paddleVelocity.magnitude:F5} " +
                          $"angVel={paddleAngularVelocity.magnitude:F3} " +
                          $"worldOffset={imuController.HasWorldOffset} " +
                          $"pivotPos={stalePosition} appliedPos={appliedPosition} " +
                          $"staleRot={staleRotation.eulerAngles}");
            }

            if (enableProximityFallback)
                TryProximityHit();
            TryFlickAssist();
            return;
        }

        // ── IMU-only mode: driven by hardware IMU via MQTT ────────────────────────
        if (imuController != null && imuController.IsActive)
        {
            LogModeTransition("IMU-only");
            imuController.ControlsTransform = true;
            paddleVelocity = imuController.PaddleVelocity;
            paddleAngularVelocity = imuController.PaddleAngularVelocity;
            previousPosition = transform.position;

            // Sync visible racket to follow physics paddle
            if (qrTrackedRacket != null)
            {
                qrTrackedRacket.SetPositionAndRotation(
                    transform.position,
                    transform.rotation * qrPrefabRotOffset);
            }

            if (enableProximityFallback)
                TryProximityHit();
            TryFlickAssist();
            return;
        }

        // Re-enable IMU transform control when not in any IMU mode
        if (imuController != null)
            imuController.ControlsTransform = true;

        // ── QR-only mode: follow the physical racket card ─────────────────────────
        // Uses cached position so paddle persists when QR tracking is lost.
        if (qrAvailable)
        {
            qrPoseDrivenMode = true;
            LogModeTransition("QR-only");
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

            if (enableProximityFallback)
            {
                TryProximityHit();
            }
            return;
        }

        // ── Fallback: camera-relative mode (editor / device without QR) ──────────
        LogModeTransition("Camera fallback");
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

        if (enableProximityFallback)
        {
            TryProximityHit();
        }
    }

    private void LogModeTransition(string mode)
    {
        if (mode != _lastMode)
        {
            Debug.Log($"[PaddleHit] Mode: {mode}");
            _lastMode = mode;
        }
    }

    /// <summary>
    /// Returns the best known ball Rigidbody. Uses <see cref="trackedBall"/> first,
    /// then <see cref="cachedBallRb"/>, and falls back to a tag/name search
    /// (throttled to once per second to avoid scanning every FixedUpdate).
    /// </summary>
    private Rigidbody GetBallRigidbody()
    {
        if (trackedBall != null) return trackedBall;
        if (cachedBallRb != null) return cachedBallRb;

        PracticeBallController liveBallController = PracticeBallController.GetLiveInstance();
        if (liveBallController != null)
        {
            Rigidbody liveBallRb = liveBallController.GetComponent<Rigidbody>();
            if (liveBallRb != null)
            {
                cachedBallRb = liveBallRb;
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
                catch (UnityException) { }
            }

            if (found == null)
            {
                Rigidbody[] rigidbodies = FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
                for (int index = 0; index < rigidbodies.Length; index++)
                {
                    Rigidbody body = rigidbodies[index];
                    if (body == null || body == paddleRigidbody) continue;
                    if (body.gameObject.name.IndexOf("ball", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = body;
                        break;
                    }
                }
            }

            cachedBallRb = found;
        }

        return cachedBallRb;
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

    private void TryProximityHit()
    {
        if (!HasActiveHitControlSource())
            return;

        Rigidbody candidateBall = GetBallRigidbody();

        if (candidateBall == null)
        {
            return;
        }

        Vector3 ballPosition = candidateBall.worldCenterOfMass;
        Vector3 closestPointOnPaddle = GetClosestPointOnPaddle(ballPosition);
        float distance = Vector3.Distance(closestPointOnPaddle, ballPosition);
        float effectiveHitDistance = proximityHitDistance;
        if (enableQrProximityAssist && qrPoseDrivenMode)
            effectiveHitDistance = qrProximityHitDistance;
        if (enableWaitingToServeAssist
            && gameState != null
            && gameState.State == GameStateManager.RallyState.WaitingToServe)
        {
            effectiveHitDistance = Mathf.Max(effectiveHitDistance, waitingToServeHitDistance);
        }

        if (distance <= effectiveHitDistance)
        {
            // Normal points from the paddle surface toward the ball COM.
            Vector3 toball = ballPosition - closestPointOnPaddle;
            Vector3 surfaceNormal = toball.sqrMagnitude > 0.0001f
                ? toball.normalized
                : transform.TransformDirection(localFaceNormal).normalized;

            // Draw a debug sphere at the contact point so you can see the hit in Scene view.
            Debug.DrawLine(closestPointOnPaddle, ballPosition, Color.yellow, 0.1f);

            ApplyHitImpulse(candidateBall, closestPointOnPaddle, surfaceNormal);
        }
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

    /// <summary>
    /// IMU-assist flick: when the ball is within an opponent-facing hemisphere of
    /// radius <see cref="flickRadius"/> around the paddle face centre, and the player
    /// is actively swinging (IMU speed ≥ flickMinSwingSpeed), applies a directed
    /// impulse that steers the ball toward the bot side.
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
        if (!enableFlick) return;
        if (imuController == null || !imuController.IsActive) return;
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

        // Spatial check: ball must be within flickRadius of the paddle face centre.
        // The assist volume is an opponent-facing hemisphere, not a full sphere,
        // so it cannot counter-hit balls that are behind the player-facing plane.
        Vector3 faceCenter = transform.position;
        Vector3 toBall = ballRb.worldCenterOfMass - faceCenter;
        float dist = toBall.magnitude;
        if (dist > flickRadius) return;

        Vector3 opponentForward = ResolveOpponentForwardDirection();
        Vector3 toBallPlanar = toBall;
        toBallPlanar.y = 0f;
        if (toBallPlanar.sqrMagnitude > 0.0001f
            && Vector3.Dot(opponentForward, toBallPlanar.normalized) <= 0f)
        {
            return;
        }

        // Side check: ball must be roughly in front of the paddle face, not behind it.
        // Tolerance of -0.3 allows the ball to be slightly off-axis to the side.
        Vector3 worldFaceNormal = transform.TransformDirection(localFaceNormal).normalized;
        if (toBall.sqrMagnitude > 0.0001f && Vector3.Dot(worldFaceNormal, toBall.normalized) < -0.3f)
            return;

        // Flick direction: drive toward court +Z (bot side) so assists are independent
        // of camera heading and cannot reverse when the device rotates.
        Vector3 baseDir = ResolveFlickBaseDirection(faceCenter, ballRb.worldCenterOfMass);
        Vector3 flickDir = (baseDir + Vector3.up * flickUplift).normalized;

        Debug.Log($"[Flick] Assist triggered: swingSpd={swingSpeed:F2} dist={dist:F3} dir={flickDir}");

        // paddleVelocity is already populated from IMU data in the current mode block,
        // so the impulse solver naturally scales with the player's actual swing speed.
        ApplyHitImpulse(ballRb, faceCenter, flickDir, flickVelocityMultiplier);
        lastFlickTime = Time.time;
    }

    private Vector3 ResolveFlickBaseDirection(Vector3 faceCenter, Vector3 ballPosition)
    {
        Transform gameSpaceRoot = ResolveGameSpaceRoot();
        if (gameSpaceRoot != null)
        {
            Vector3 courtForward = gameSpaceRoot.TransformDirection(Vector3.forward);
            courtForward.y = 0f;
            if (courtForward.sqrMagnitude > 0.0001f)
                return courtForward.normalized;
        }

        // Fallback 1: push generally from paddle toward ball.
        Vector3 toBall = ballPosition - faceCenter;
        toBall.y = 0f;
        if (toBall.sqrMagnitude > 0.0001f)
            return toBall.normalized;

        // Fallback 2: legacy camera heading.
        Vector3 cameraForward = cameraTransform != null ? cameraTransform.forward : transform.forward;
        cameraForward.y = 0f;
        if (cameraForward.sqrMagnitude < 0.0001f)
            cameraForward = Vector3.forward;
        return cameraForward.normalized;
    }

    private Vector3 ResolveOpponentForwardDirection()
    {
        Transform gameSpaceRoot = ResolveGameSpaceRoot();
        if (gameSpaceRoot != null)
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
        if (collision.contactCount == 0 || collision.rigidbody == null)
        {
            return;
        }

        ContactPoint contact = collision.GetContact(0);

        // From the PADDLE's OnCollision, contact.normal points FROM ball INTO paddle.
        // Negate it to get the outward paddle-surface normal (paddle → ball).
        Vector3 surfaceNormal = -contact.normal;
        ApplyHitImpulse(collision.rigidbody, contact.point, surfaceNormal);
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
        ApplyHitImpulse(otherRigidbody, contactPoint, surfaceNormal);
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
        float surfaceVelocityMultiplier = 1f)
    {
        if (ballBody == null)
        {
            return;
        }

        if (gameState != null && !gameState.IsStarted)
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

        if (Time.time - lastHitTime < hitCooldown)
        {
            return;
        }

        // ── Kitchen violation check ───────────────────────────────────────────────
        // Non-volley zone: hitting (volleying) from the kitchen is a fault.
        if (isInKitchen && gameState != null)
        {
            gameState.OnKitchenViolation();
            lastHitTime = Time.time;
            return;
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
        float clampedSurfaceVelocityMultiplier = Mathf.Max(0f, surfaceVelocityMultiplier);
        Vector3 paddleContactVelocity = (
            paddleVelocity + Vector3.Cross(paddleAngularVelocity, contactPoint - paddleCOM))
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
        float separatingTolerance = waitingToServe ? 0.35f : 0.05f;
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
