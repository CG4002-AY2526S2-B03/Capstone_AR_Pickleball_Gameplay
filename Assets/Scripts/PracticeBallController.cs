using UnityEngine;

public class PracticeBallController : MonoBehaviour
{
    private const string CourtFloorName = "_CourtFloor";
    private const string MainCourtSurfaceName = "pickleball court (4)";

    private enum BounceCourtSide
    {
        Unknown,
        Player,
        Bot
    }

    private const float BounceSideNetTolerance = 0.05f;

    public static PracticeBallController Instance { get; private set; }

    /// <summary>
    /// True once the most recent paddle hit has been followed by a valid first
    /// ground bounce on the opposite side of the net. When true, the rally is
    /// "owned" by the last hitter: any subsequent terminating event (second bounce,
    /// wall, net, out, stall) should award the point to the last hitter. Cleared on
    /// the next accepted paddle hit (via ResetBounceCount) or on ball reset.
    /// </summary>
    public bool HasCrossedNetAfterLastHit => hasCrossedNetAfterLastHit;

    public static PracticeBallController GetLiveInstance()
    {
        // Fast path: cached singleton is still alive and in a loaded scene
        if (Instance != null && Instance.gameObject.scene.isLoaded)
        {
            if (IsBackupBallObject(Instance.gameObject))
            {
                Instance = null;
            }
            else
            {
                // If the ball exists but is inactive in hierarchy (parent deactivated),
                // still return it — callers are responsible for activating.
                return Instance;
            }
        }

        // Active-object search (fast, only finds active GameObjects)
        PracticeBallController found = FindFirstObjectByType<PracticeBallController>();
        if (found != null && !IsBackupBallObject(found.gameObject))
        {
            Instance = found;
            // If the ball exists but is inactive in hierarchy (parent deactivated),
            // still return it — callers are responsible for activating.
            return found;
        }

        // Deep search: includes inactive GameObjects and prefab assets.
        // Filter to scene objects only (scene.isLoaded).
        foreach (PracticeBallController candidate in Resources.FindObjectsOfTypeAll<PracticeBallController>())
        {
            if (candidate != null
                && candidate.gameObject.scene.isLoaded
                && !IsBackupBallObject(candidate.gameObject))
            {
                Instance = candidate;
                Debug.Log($"[Ball] GetLiveInstance recovered inactive ball: " +
                          $"active={candidate.gameObject.activeInHierarchy}, " +
                          $"selfActive={candidate.gameObject.activeSelf}, " +
                          $"parent={(candidate.transform.parent != null ? candidate.transform.parent.name : "null")}");
                return candidate;
            }
        }

        Debug.LogWarning("[Ball] GetLiveInstance: no PracticeBallController found in any loaded scene.");
        return null;
    }

    [Header("References")]
    public Transform servePoint;

    [Tooltip("When set, the ball spawns near the paddle's last known position " +
             "(from QR tracking) at serveHeight above the court. Best for serving.")]
    public PaddleHitController paddleController;

    [Header("Serve Position (local to GameSpaceRoot)")]
    [Tooltip("Where the ball spawns relative to GameSpaceRoot. " +
             "Used as fallback when no paddle position is available. " +
             "Ignored when servePoint is set to an external Transform.")]
    public Vector3 courtServeLocalPos = new Vector3(0.44f, 1.0f, -3.4f);

    [Tooltip("Height above court (court-local Y) at which the ball spawns for serving.")]
    public float serveHeight = 2.5f;
    [Tooltip("Height above court (court-local Y) used by button-triggered ball resets.")]
    public float resetHeight = 4.5f;
    [Tooltip("Horizontal distance in front of the camera used by button-triggered resets.")]
    public float resetDistanceFromCamera = 0.5f;
    [Tooltip("Minimum forward distance (court +Z) from camera for camera-based resets.")]
    public float minResetForwardOffsetFromCamera = 1.0f;
    [Tooltip("Lowest court-local Z allowed for camera-based resets.")]
    public float minResetLocalZ = -4.65f;
    [Tooltip("Keep camera-based resets this far on the player side of the net.")]
    public float resetNetClearance = 1.0f;

    [Header("Serve Height Guard")]
    [Tooltip("When true, the ball won't drop too low while waiting to serve.")]
    public bool enforceWaitingServeMinHeight = false;
    [Tooltip("Ball stays at least this far below the maximum paddle height observed during the current serve phase.")]
    public float serveBelowPaddleMax = 0.18f;
    [Tooltip("Absolute minimum serve height in court-local Y, used as a safety floor.")]
    public float minServeLocalY = 1.35f;
    [Tooltip("Minimum upward rebound speed when the serve-height floor is reached.")]
    public float serveFloorReboundSpeed = 1.8f;
    [Tooltip("Scales rebound speed from incoming downward speed at the serve-height floor.")]
    [Range(0f, 1f)]
    public float serveFloorReboundFactor = 0.35f;

    [Header("Ground Safety")]
    [Tooltip("Legacy invisible safety floor. Keep disabled to use the main court collider as the only bounce surface.")]
    public bool createGroundPlane = false;

    [Header("Ground Physics")]
    [Tooltip("Bounciness of the runtime court floor material. 1 = nearly perfectly elastic, 0 = no bounce.")]
    [Range(0f, 1f)]
    public float groundPlaneBounciness = 0.82f;
    [Tooltip("Dynamic friction of the runtime court floor material.")]
    [Range(0f, 1f)]
    public float groundPlaneDynamicFriction = 0.4f;
    [Tooltip("Static friction of the runtime court floor material.")]
    [Range(0f, 1f)]
    public float groundPlaneStaticFriction = 0.4f;
    [Tooltip("How floor bounce combines with the ball PhysicsMaterial.")]
    public PhysicsMaterialCombine groundPlaneBounceCombine = PhysicsMaterialCombine.Maximum;
    [Tooltip("How floor friction combines with the ball PhysicsMaterial.")]
    public PhysicsMaterialCombine groundPlaneFrictionCombine = PhysicsMaterialCombine.Average;
    [Tooltip("When true, the floor uses a separate bounciness value while WaitingToServe so the pre-serve ball can keep bouncing.")]
    public bool useWaitingToServeGroundBounceOverride = true;
    [Tooltip("Floor bounciness used only while WaitingToServe.")]
    [Range(0f, 1f)]
    public float waitingToServeGroundPlaneBounciness = 1f;

    [Header("Out-Of-Bounds (Court Local Space)")]
    [Tooltip("When true, the first ground bounce outside the in-bounds rectangle is called out immediately.")]
    public bool detectOutOfBoundsOnFirstGroundBounce = true;
    [Tooltip("Minimum in-bounds local X (meters) in GameSpaceRoot coordinates.")]
    public float inBoundsMinLocalX = -10.8f;
    [Tooltip("Maximum in-bounds local X (meters) in GameSpaceRoot coordinates.")]
    public float inBoundsMaxLocalX = 2.1f;
    [Tooltip("Minimum in-bounds local Z (meters) in GameSpaceRoot coordinates.")]
    public float inBoundsMinLocalZ = -22.4f;
    [Tooltip("Maximum in-bounds local Z (meters) in GameSpaceRoot coordinates.")]
    public float inBoundsMaxLocalZ = 6.8f;
    [Tooltip("Extra tolerance applied to each in-bounds edge (meters). Helps absorb AR drift near lines.")]
    public float inBoundsEdgeTolerance = 0.05f;
    [Tooltip("Padding added around the in-bounds rectangle when building the invisible safety floor (meters).")]
    public float groundPlanePadding = 2f;
    [Tooltip("Minimum seconds between accepted ground-bounce registrations to avoid duplicate counting from contact jitter.")]
    public float bounceDedupeSeconds = 0.05f;

    [Header("Game State")]
    [Tooltip("When set, boundary collisions trigger scoring instead of raw resets.")]
    public GameStateManager gameState;

    [Header("God Mode")]
    [Tooltip("Global ball speed multiplier applied to player-hit and waiting-to-serve rebound velocities in God Mode.")]
    [Range(0f, 1f)]
    public float godModeSlowdownMultiplier = 0.6f;

    [Header("Bounce Diagnostics")]
    [Tooltip("Optional MQTT controller used to publish bounce decision telemetry.")]
    public MqttController mqttController;
    [Tooltip("When true, publishes bounce decision events to MQTT topic /ballBounceDebug.")]
    public bool publishBounceDiagnosticsToMqtt = true;

    [Header("Controls")]
    public KeyCode resetKey = KeyCode.R;

    [Header("Recovery")]
    [Tooltip("Ball is force-reset if it exceeds this distance from the court root.")]
    public float maxWorldDistance = 100f;
    [Tooltip("Ball is force-reset if its court-local Y falls below this value.")]
    public float minWorldY = -2f;
    [Tooltip("If the ball crawls on the ground below this speed for too long, reset it.")]
    public float stuckSpeedThreshold = 0.2f;
    [Tooltip("Seconds the ball may remain nearly stationary on the ground before reset.")]
    public float stuckTimeout = 1.5f;
    [Tooltip("How close to the court floor counts as 'on the ground' for stuck detection.")]
    public float groundCheckHeight = 0.2f;
    [Tooltip("Enable verbose ball lifecycle logs for Xcode debugging.")]
    public bool enableDebugLogs = false;
    [Tooltip("Seconds between periodic ball state logs while debugging.")]
    public float debugLogInterval = 0.75f;

    [Header("Fault Validation")]
    [Tooltip("Minimum inbound ball speed toward back/side boundaries before an out fault is allowed.")]
    public float outFaultMinInboundImpactSpeed = 0.08f;
    [Tooltip("Minimum inbound ball speed toward the net before a net fault is allowed.")]
    public float netFaultMinInboundImpactSpeed = 0.08f;
    [Tooltip("Extra tolerance around back/side boundary planes when validating out faults.")]
    public float outFaultPlaneTolerance = 0.12f;
    [Tooltip("Extra tolerance around the net plane/top when validating net faults.")]
    public float netFaultPlaneTolerance = 0.12f;
    [Tooltip("When true, a stuck live ball prefers double-bounce scoring or silent recovery instead of inventing an out call.")]
    public bool useConservativeStuckBallScoring = true;
    private Rigidbody ballRigidbody;
    private Vector3 initialLocalPosition;
    private Transform gameSpaceRoot;
    private int bounceCount;
    private float stuckTimer;
    private bool isManagedFrozen;
    private Vector3 lastFixedLinearVelocity;
    private float lastDebugLogTime;
    private float lastAppliedGroundPlaneBounciness = float.NaN;
    private Vector3 lastGroundColliderRootPosition = new Vector3(float.NaN, float.NaN, float.NaN);
    private Quaternion lastGroundColliderRootRotation = Quaternion.identity;
    private bool hasGroundColliderRootPose;
    private int lastBounceFrame = -1; // prevents double-counting two colliders in same frame
    private float lastBounceTime = -10f;
    private float firstBounceLocalZ = float.NaN;
    private Vector3 firstBounceLocalPoint = new Vector3(float.NaN, float.NaN, float.NaN);
    private float firstBounceTime = float.NaN;
    private BounceCourtSide consecutiveBounceSide = BounceCourtSide.Unknown;
    private int consecutiveBounceCount;
    private bool hasCrossedNetAfterLastHit;
    private float servePaddleMaxY = float.NegativeInfinity;
    private static readonly Vector3 InvalidLocalContactPoint =
        new Vector3(float.NaN, float.NaN, float.NaN);

    /// <summary>
    /// Hidden backup clone stored in DontDestroyOnLoad.
    /// If the ball is ever destroyed (e.g. anchor removal cascades),
    /// we can instantiate a fresh ball from this backup.
    /// </summary>
    private static GameObject _backupPrefab;
    private static bool _isCreatingBackup;

    private static bool IsBackupBallObject(GameObject candidate)
    {
        return candidate != null && candidate.name == "_BallBackup";
    }

    private void Awake()
    {
        bool isBackupClone = _isCreatingBackup;
        if (!isBackupClone)
            Instance = this;

        if (isBackupClone)
            return;

        EnsureRuntimeBallTag();
        ballRigidbody = GetComponent<Rigidbody>();
        lastFixedLinearVelocity = ballRigidbody != null ? ballRigidbody.linearVelocity : Vector3.zero;
        DisableTrailAutoDestruct();

        // Walk up the hierarchy to find the GameSpaceRoot parent.
        // Ball2 is a direct child of GameSpaceRoot.
        gameSpaceRoot = transform.parent;

        // Remember the ball's original local position (set in the prefab / scene).
        initialLocalPosition = transform.localPosition;

        // Detach from GameSpaceRoot so ARFoundation destroying the anchor
        // (or any other cause of GameSpaceRoot destruction) cannot cascade to the ball.
        // We keep the gameSpaceRoot *reference* for local-coordinate math.
        // IMPORTANT: Do NOT call DontDestroyOnLoad — that moves the ball to a separate
        // physics scene and breaks collision detection with the paddle and court floor.
        if (!_isCreatingBackup)
        {
            transform.SetParent(null, true);
            Debug.Log("[Ball] Detached from GameSpaceRoot — stays in main scene for physics.");
        }
    }

    private void OnEnable()
    {
        if (_isCreatingBackup || IsBackupBallObject(gameObject))
            return;

        Instance = this;
        EnsureRuntimeBallTag();
        DisableTrailAutoDestruct();
        LogBallEvent("OnEnable");
    }

    private void OnDisable()
    {
        LogBallEvent("OnDisable");
    }

    private void OnDestroy()
    {
        LogBallEvent("OnDestroy");
        Debug.LogWarning($"[Ball] OnDestroy STACK TRACE — who destroyed the ball?\n{System.Environment.StackTrace}");
        if (Instance == this)
            Instance = null;
    }

    /// <summary>
    /// Instantiates a new ball from the backup prefab. Called by GameStateManager
    /// when RecoverBall cannot find any live ball instance.
    /// Returns null if no backup exists.
    /// </summary>
    public static PracticeBallController RespawnFromBackup(Transform parent)
    {
        RefreshBackupReference();

        if (_backupPrefab == null)
        {
            Debug.LogError("[Ball] RespawnFromBackup: no backup prefab exists!");
            return null;
        }

        GameObject newBall = Instantiate(_backupPrefab, parent);
        newBall.name = "Ball2_Respawned";
        newBall.SetActive(true);

        PracticeBallController ctrl = newBall.GetComponent<PracticeBallController>();
        if (ctrl != null)
        {
            Instance = ctrl;
            Debug.Log($"[Ball] Respawned ball under {(parent != null ? parent.name : "scene root")}");
        }

        return ctrl;
    }

    private void Start()
    {
        if (paddleController == null)
            paddleController = FindFirstObjectByType<PaddleHitController>();

        if (createGroundPlane && gameSpaceRoot != null)
        {
            EnsureGroundCollider();
        }
        else
        {
            RemoveLegacyGroundCollider();
        }

        // Drop the ball so the player can serve immediately.
        ResetBall();
    }

    private void Update()
    {
        if (Input.GetKeyDown(resetKey))
        {
            ResetBall();
        }

        // Safety net: if the ball drifts too far or goes NaN, force reset
        if (ballRigidbody != null)
        {
            RefreshGroundColliderIfNeeded();
            EnforceWaitingServeMinHeight();

            Vector3 pos = transform.position;
            Vector3 velocity = ballRigidbody.linearVelocity;
            float distanceFromCourt = gameSpaceRoot != null
                ? Vector3.Distance(pos, gameSpaceRoot.position)
                : pos.magnitude;
            float localY = gameSpaceRoot != null
                ? gameSpaceRoot.InverseTransformPoint(pos).y
                : pos.y;

            if (HasInvalidVector(pos) || HasInvalidVector(velocity)
                || distanceFromCourt > maxWorldDistance
                || localY < minWorldY)
            {
                Debug.LogWarning("[Ball] Invalid or out-of-bounds state detected — forcing reset.");
                ForceRecoverBall("InvalidOrOutOfBoundsState");
                return;
            }

            DetectStuckBall(pos, velocity);
            MaybeLogBallState("Update");
        }
    }

    private void EnforceWaitingServeMinHeight()
    {
        if (!enforceWaitingServeMinHeight || ballRigidbody == null)
            return;

        bool waitingToServe = gameState == null || gameState.State == GameStateManager.RallyState.WaitingToServe;
        if (!waitingToServe)
        {
            servePaddleMaxY = float.NegativeInfinity;
            return;
        }

        if (paddleController == null)
            paddleController = FindFirstObjectByType<PaddleHitController>();

        if (paddleController != null)
            servePaddleMaxY = Mathf.Max(servePaddleMaxY, paddleController.transform.position.y);

        float targetMinWorldY = gameSpaceRoot != null
            ? gameSpaceRoot.TransformPoint(new Vector3(0f, minServeLocalY, 0f)).y
            : minServeLocalY;

        if (!float.IsNegativeInfinity(servePaddleMaxY))
            targetMinWorldY = Mathf.Max(targetMinWorldY, servePaddleMaxY - serveBelowPaddleMax);

        Vector3 worldPos = transform.position;
        if (worldPos.y >= targetMinWorldY)
            return;

        worldPos.y = targetMinWorldY;
        transform.position = worldPos;
        ballRigidbody.position = worldPos;

        Vector3 velocity = ballRigidbody.linearVelocity;
        if (velocity.y <= 0f)
        {
            float reboundSpeed = Mathf.Max(
                serveFloorReboundSpeed,
                Mathf.Abs(velocity.y) * serveFloorReboundFactor);
            velocity.y = reboundSpeed;
        }
        ballRigidbody.linearVelocity = velocity;
        ballRigidbody.WakeUp();
    }

    private void FixedUpdate()
    {
        if (ballRigidbody == null)
            return;

        // Cache pre-solve velocity so collision callbacks can estimate
        // inbound speed even when relativeVelocity is damped by solver timing.
        lastFixedLinearVelocity = ballRigidbody.linearVelocity;
    }


    /// <summary>
    /// Resets the ball: drops it from 3m height, 0.5m in front of the main
    /// camera, with gravity enabled so the player can serve.
    /// Falls back to courtServeLocalPos if no camera is available.
    /// </summary>
    public void ResetBall()
    {
        CancelInvoke(nameof(NetFault));
        bounceCount = 0;
        ResetBounceSequence();
        servePaddleMaxY = float.NegativeInfinity;
        lastBounceFrame = -1;
        lastBounceTime = -10f;
        stuckTimer = 0f;
        isManagedFrozen = false;
        LogBallEvent("ResetBall.begin");

        if (!TryReactivateForReset())
            return;

        // Fully sanitise the Rigidbody before repositioning —
        // clears NaN and corrupted physics state
        SanitiseRigidbody();

        Vector3 targetWorldPosition;
        Camera cam = Camera.main;
        if (cam != null)
        {
            // Base world-space position, used only when GameSpaceRoot is unavailable.
            Vector3 camFwd = cam.transform.forward;
            camFwd.y = 0f;
            if (camFwd.sqrMagnitude < 0.0001f) camFwd = Vector3.forward;
            camFwd.Normalize();

            float minForwardOffset = Mathf.Max(1.0f, minResetForwardOffsetFromCamera);
            float desiredForwardOffset = Mathf.Max(resetDistanceFromCamera, minForwardOffset);
            Vector3 worldPos = cam.transform.position + camFwd * desiredForwardOffset;
            if (gameSpaceRoot != null)
            {
                Vector3 cameraLocal = gameSpaceRoot.InverseTransformPoint(cam.transform.position);
                // Always reset toward court +Z (opponent side), independent of camera yaw.
                Vector3 localForward = Vector3.forward;

                bool hasNetZ = TryGetNetLocalZ(out float netZ);
                float maxResetLocalZ = hasNetZ
                    ? Mathf.Max(minResetLocalZ, netZ - resetNetClearance)
                    : float.PositiveInfinity;
                float minForwardLocalZ = Mathf.Max(minResetLocalZ, cameraLocal.z + minForwardOffset);
                if (hasNetZ && minForwardLocalZ > maxResetLocalZ)
                {
                    minForwardLocalZ = maxResetLocalZ;
                    if (enableDebugLogs)
                    {
                        Debug.LogWarning($"[BallDebug] Camera is close to net; cannot keep full forward reset offset. " +
                                         $"Using max localZ={maxResetLocalZ:F3}.");
                    }
                }

                Vector3 desiredLocal = cameraLocal + localForward * desiredForwardOffset;

                float unclampedZ = desiredLocal.z;
                desiredLocal.z = hasNetZ
                    ? Mathf.Clamp(desiredLocal.z, minForwardLocalZ, maxResetLocalZ)
                    : Mathf.Max(desiredLocal.z, minForwardLocalZ);
                desiredLocal.y = resetHeight;

                if (!hasNetZ && enableDebugLogs)
                {
                    Debug.LogWarning("[BallDebug] Net local Z could not be resolved; camera reset uses camera-relative forward minimum only.");
                }
                else if (enableDebugLogs && Mathf.Abs(unclampedZ - desiredLocal.z) > 0.001f)
                {
                    Debug.Log($"[BallDebug] Camera reset clamped from localZ={unclampedZ:F3} to {desiredLocal.z:F3} " +
                              $"(netZ={netZ:F3}, minForwardZ={minForwardLocalZ:F3}, clearance={resetNetClearance:F3})");
                }
                targetWorldPosition = gameSpaceRoot.TransformPoint(desiredLocal);
            }
            else
            {
                worldPos.y = resetHeight;
                targetWorldPosition = worldPos;
            }
        }
        else
        {
            // No camera — fall back to a known-good serve position at resetHeight
            targetWorldPosition = GetFallbackServeWorldPosition();
        }

        ApplyResetPose(targetWorldPosition);

        // Validate the final position isn't NaN or absurdly far
        Vector3 finalPos = transform.position;
        if (HasInvalidVector(finalPos))
        {
            Debug.LogError("[Ball] ResetBall resulted in NaN position! Using fallback.");
            transform.position = GetFallbackServeWorldPosition();
            if (ballRigidbody != null)
            {
                ballRigidbody.position = transform.position;
                Physics.SyncTransforms();
            }
        }

        LogBallEvent("ResetBall.end");
    }

    /// <summary>
    /// Freezes the ball in its current position during the point result display.
    /// </summary>
    public void FreezeInPlace()
    {
        if (ballRigidbody == null)
            return;

        isManagedFrozen = true;
        stuckTimer = 0f;
        ballRigidbody.constraints = RigidbodyConstraints.None;
        ballRigidbody.linearVelocity = Vector3.zero;
        ballRigidbody.angularVelocity = Vector3.zero;
        ballRigidbody.useGravity = false;
        ballRigidbody.isKinematic = true;
        ballRigidbody.detectCollisions = true;
        Physics.SyncTransforms();
        LogBallEvent("FreezeInPlace");
    }

    /// <summary>
    /// Nuclear recovery: fully reconstruct the Rigidbody when it enters
    /// a corrupted state (NaN position/velocity, out of bounds, etc.).
    /// </summary>
    private void ForceRecoverBall(string reason = "Unknown")
    {
        isManagedFrozen = false;
        stuckTimer = 0f;
        PublishBounceLifecycleEvent("BallRecover", reason);
        LogBallEvent("ForceRecoverBall.begin");
        SanitiseRigidbody();
        ResetBall();
    }

    /// <summary>
    /// Clears all Rigidbody state to prevent NaN propagation.
    private bool IsCourtPlacementStillPending()
    {
        ARPlaneGameSpacePlacer placer = FindFirstObjectByType<ARPlaneGameSpacePlacer>();
        if (placer == null)
            return false;

        Transform currentRoot = gameSpaceRoot;
        if (currentRoot == null)
        {
            Transform t = transform.parent;
            while (t != null)
            {
                if (t.name == "GameSpaceRoot")
                {
                    currentRoot = t;
                    break;
                }
                t = t.parent;
            }
        }

        Transform placerRoot = placer.GameSpaceRoot;
        if (placerRoot == null || currentRoot == null || placerRoot != currentRoot)
            return false;

        return placer.PlaceOnlyFromQrAnchor && !placer.IsPlaced;
    }

    private bool TryReactivateForReset()
    {
        if (gameObject.activeInHierarchy)
            return true;

        bool placementPending = IsCourtPlacementStillPending();

        Transform t = transform;
        while (t != null)
        {
            if (!t.gameObject.activeSelf)
            {
                if (placementPending && gameSpaceRoot != null && t == gameSpaceRoot)
                {
                    Debug.Log("[Ball] ResetBall deferred: GameSpaceRoot is intentionally hidden until court QR placement.");
                    return false;
                }

                Debug.LogWarning($"[Ball] ResetBall: reactivating inactive ancestor '{t.name}'");
                t.gameObject.SetActive(true);
            }
            t = t.parent;
        }

        if (!gameObject.activeSelf)
            gameObject.SetActive(true);

        return gameObject.activeInHierarchy;
    }

    /// Safe to call even if the Rigidbody is already clean.
    /// </summary>
    private void SanitiseRigidbody()
    {
        if (ballRigidbody == null) return;

        // Check for NaN/Infinity corruption
        bool corrupted = HasInvalidVector(ballRigidbody.position)
                      || HasInvalidVector(ballRigidbody.linearVelocity)
                      || HasInvalidVector(ballRigidbody.angularVelocity);

        if (corrupted)
        {
            if (enableDebugLogs)
                Debug.LogWarning("[BallDebug] Rigidbody corrupted (NaN/Inf) — reconstructing.");
            // Temporarily disable and re-enable to force Unity to reset internal physics state
            ballRigidbody.isKinematic = true;
            transform.position = GetFallbackServeWorldPosition();
            ballRigidbody.isKinematic = false;
            LogBallEvent("SanitiseRigidbody.reconstructed");
        }

        ballRigidbody.constraints = RigidbodyConstraints.None;
        ballRigidbody.isKinematic = false;
        ballRigidbody.detectCollisions = true;
        ballRigidbody.linearVelocity = Vector3.zero;
        ballRigidbody.angularVelocity = Vector3.zero;
        ballRigidbody.useGravity = false;
        LogBallEvent("SanitiseRigidbody.end");
    }

    private void DisableTrailAutoDestruct()
    {
        TrailRenderer[] trailRenderers = GetComponentsInChildren<TrailRenderer>(true);
        for (int index = 0; index < trailRenderers.Length; index++)
        {
            TrailRenderer trail = trailRenderers[index];
            if (trail == null || !trail.autodestruct)
                continue;

            trail.autodestruct = false;
            Debug.LogWarning($"[Ball] Disabled TrailRenderer.autodestruct on '{trail.gameObject.name}' to prevent unintended ball destruction.");
        }
    }

    private static void RefreshBackupReference()
    {
        if (_backupPrefab != null)
            return;

        foreach (PracticeBallController candidate in Resources.FindObjectsOfTypeAll<PracticeBallController>())
        {
            if (candidate == null)
                continue;

            GameObject candidateObject = candidate.gameObject;
            if (candidateObject == null || !candidateObject.scene.isLoaded)
                continue;

            if (candidateObject.name == "_BallBackup")
            {
                _backupPrefab = candidateObject;
                Debug.Log("[Ball] Recovered backup prefab reference from loaded scene object.");
                return;
            }
        }
    }

    /// <summary>
    /// Re-assigns the GameSpaceRoot reference (e.g. after the ball is detached
    /// from its original parent or respawned from the backup prefab).
    /// </summary>
    public void SetGameSpaceRoot(Transform root)
    {
        gameSpaceRoot = root;
        if (createGroundPlane && gameSpaceRoot != null)
            EnsureGroundCollider();
        else
            RemoveLegacyGroundCollider();
        Debug.Log($"[Ball] gameSpaceRoot set to {(root != null ? root.name : "null")}");
    }

    /// <summary>Alias kept for callers that used the old name.</summary>
    public void DropBallInFrontOfCamera() => ResetBall();

    /// <summary>
    /// Resets the ground bounce counter. Called by GameStateManager
    /// when the ball is hit by the player or bot.
    /// </summary>
    public void ResetBounceCount()
    {
        bounceCount = 0;
        lastBounceFrame = -1;
        lastBounceTime = -10f;
        ResetBounceSequence();
        LogBallEvent("ResetBounceCount");
    }

    /// <summary>
    /// Returns the number of ground bounces registered since the last hit reset.
    /// 0 = volley candidate, 1+ = ball has bounced.
    /// </summary>
    public int GetBounceCount()
    {
        return bounceCount;
    }

    public bool IsGodModeActive()
    {
        return gameState != null && gameState.Mode == GameStateManager.GameMode.GodMode;
    }

    public Vector3 ApplyGodModeBallSpeed(Vector3 velocity)
    {
        if (!IsGodModeActive())
            return velocity;

        return velocity * Mathf.Clamp01(godModeSlowdownMultiplier);
    }

    /// <summary>
    /// Called by PaddleHitController (or BotHitController) when the paddle
    /// hits the ball.  Unfreezes the ball and enables gravity so it follows
    /// a real arc.
    /// </summary>
    public void EnableGravity()
    {
        if (ballRigidbody == null)
            return;

        isManagedFrozen = false;
        ballRigidbody.isKinematic = false;
        ballRigidbody.useGravity = true;
        ballRigidbody.WakeUp();
        LogBallEvent("EnableGravity");
    }

    // ─────────────────────────────────────────────────────────────────────────

    private Vector3 GetFallbackServeWorldPosition()
    {
        // Priority 1: spawn near the paddle's last known position (from QR tracking)
        // at resetHeight above the court. This puts the ball right where the player
        // is holding the racket, ready for an underhand serve.
        if (paddleController != null && gameSpaceRoot != null)
        {
            Vector3 paddleWorld = paddleController.transform.position;
            Vector3 paddleLocal = gameSpaceRoot.InverseTransformPoint(paddleWorld);
            // Keep paddle's lateral (X) and depth (Z), override height to resetHeight
            Vector3 serveLocal = new Vector3(paddleLocal.x, resetHeight, paddleLocal.z);
            return gameSpaceRoot.TransformPoint(serveLocal);
        }

        // Priority 2: external servePoint (e.g. the AR camera)
        if (servePoint != null)
        {
            Vector3 worldPos = servePoint.TransformPoint(new Vector3(0.18f, -0.12f, 0.85f));
            if (gameSpaceRoot != null)
            {
                Vector3 local = gameSpaceRoot.InverseTransformPoint(worldPos);
                local.y = resetHeight;
                return gameSpaceRoot.TransformPoint(local);
            }

            worldPos.y = resetHeight;
            return worldPos;
        }

        // Priority 3: fixed position relative to GameSpaceRoot (court-local).
        if (gameSpaceRoot != null)
        {
            Vector3 serveLocal = courtServeLocalPos;
            serveLocal.y = resetHeight;
            return gameSpaceRoot.TransformPoint(serveLocal);
        }

        // Last resort: use the position baked by Awake.
        Vector3 fallback = transform.parent != null
            ? transform.parent.TransformPoint(initialLocalPosition)
            : initialLocalPosition;
        fallback.y = resetHeight;
        return fallback;
    }

    private void ApplyResetPose(Vector3 targetWorldPosition)
    {
        if (ballRigidbody == null)
            return;

        transform.position = targetWorldPosition;
        transform.rotation = Quaternion.identity;
        Physics.SyncTransforms();

        ballRigidbody.constraints = RigidbodyConstraints.None;
        ballRigidbody.isKinematic = false;
        ballRigidbody.detectCollisions = true;
        ballRigidbody.linearVelocity = Vector3.zero;
        ballRigidbody.angularVelocity = Vector3.zero;
        ballRigidbody.useGravity = true;
        ballRigidbody.position = targetWorldPosition;
        ballRigidbody.rotation = Quaternion.identity;
        ballRigidbody.WakeUp();
        LogBallEvent($"ApplyResetPose target={FormatVector(targetWorldPosition)}");
    }

    private void EnsureRuntimeBallTag()
    {
        if (CompareTag("Ball"))
            return;

        try
        {
            gameObject.tag = "Ball";
        }
        catch (UnityException exception)
        {
            Debug.LogWarning($"[Ball] Unable to assign 'Ball' tag at runtime: {exception.Message}");
        }
    }

    private bool TryGetNetLocalZ(out float netLocalZ)
    {
        if (gameState != null)
        {
            netLocalZ = gameState.GetNetLocalZ();
            return true;
        }

        if (gameSpaceRoot != null)
        {
            Transform netTransform = gameSpaceRoot.Find("Net");
            if (netTransform != null)
            {
                netLocalZ = netTransform.localPosition.z;
                return true;
            }

            CourtBoundary[] boundaries = gameSpaceRoot.GetComponentsInChildren<CourtBoundary>(true);
            for (int index = 0; index < boundaries.Length; index++)
            {
                CourtBoundary boundary = boundaries[index];
                if (boundary != null && boundary.boundaryType == CourtBoundary.BoundaryType.Net)
                {
                    netLocalZ = boundary.transform.localPosition.z;
                    return true;
                }
            }
        }

        CourtBoundarySetup boundarySetup = FindFirstObjectByType<CourtBoundarySetup>();
        if (boundarySetup != null)
        {
            netLocalZ = boundarySetup.netLocalPosition.z;
            return true;
        }

        if (gameState != null)
        {
            netLocalZ = gameState.netZPosition;
            return true;
        }

        netLocalZ = 0f;
        return false;
    }

    private bool IsOutsideInBoundsZone(Vector3 localContactPoint)
    {
        float edgeTolerance = Mathf.Max(0f, inBoundsEdgeTolerance);

        float minX = Mathf.Min(inBoundsMinLocalX, inBoundsMaxLocalX) - edgeTolerance;
        float maxX = Mathf.Max(inBoundsMinLocalX, inBoundsMaxLocalX) + edgeTolerance;
        float minZ = Mathf.Min(inBoundsMinLocalZ, inBoundsMaxLocalZ) - edgeTolerance;
        float maxZ = Mathf.Max(inBoundsMinLocalZ, inBoundsMaxLocalZ) + edgeTolerance;

        return localContactPoint.x < minX
            || localContactPoint.x > maxX
            || localContactPoint.z < minZ
            || localContactPoint.z > maxZ;
    }

    private BounceCourtSide ClassifyBounceCourtSide(float localZ)
    {
        float netZ = 0f;
        if (gameState != null)
            netZ = gameState.GetNetLocalZ();
        else
            TryGetNetLocalZ(out netZ);

        if (localZ < netZ - BounceSideNetTolerance)
            return BounceCourtSide.Player;
        if (localZ > netZ + BounceSideNetTolerance)
            return BounceCourtSide.Bot;
        return BounceCourtSide.Unknown;
    }

    private static string DescribeBounceCourtSide(BounceCourtSide side)
    {
        return side switch
        {
            BounceCourtSide.Player => "Player",
            BounceCourtSide.Bot => "Bot",
            _ => "Unknown"
        };
    }

    private void ResetBounceSequence()
    {
        firstBounceLocalZ = float.NaN;
        firstBounceLocalPoint = new Vector3(float.NaN, float.NaN, float.NaN);
        firstBounceTime = float.NaN;
        consecutiveBounceSide = BounceCourtSide.Unknown;
        consecutiveBounceCount = 0;
        hasCrossedNetAfterLastHit = false;
    }

    private bool IsOppositeCourtSideFromLastHitter(BounceCourtSide bounceSide)
    {
        if (gameState == null || bounceSide == BounceCourtSide.Unknown)
            return false;

        GameStateManager.Hitter hitter = gameState.LastHitter;
        if (hitter == GameStateManager.Hitter.Player)
            return bounceSide == BounceCourtSide.Bot;
        if (hitter == GameStateManager.Hitter.Bot)
            return bounceSide == BounceCourtSide.Player;
        return false;
    }

    private float GetApproxGroundHeightWorld()
    {
        GameObject floor = GameObject.Find(MainCourtSurfaceName);
        if (floor != null)
        {
            Collider floorCollider = floor.GetComponent<Collider>();
            if (floorCollider != null)
                return floorCollider.bounds.max.y;

            return floor.transform.position.y;
        }

        return gameSpaceRoot != null ? gameSpaceRoot.position.y : 0f;
    }

    private static void RemoveLegacyGroundCollider()
    {
        GameObject floor = GameObject.Find(CourtFloorName);
        if (floor != null)
            Destroy(floor);
    }

    private void RefreshGroundColliderIfNeeded()
    {
        if (!createGroundPlane || gameSpaceRoot == null)
            return;

        bool rootPoseChanged = !hasGroundColliderRootPose
            || Vector3.Distance(gameSpaceRoot.position, lastGroundColliderRootPosition) > 0.0005f
            || Quaternion.Angle(gameSpaceRoot.rotation, lastGroundColliderRootRotation) > 0.05f;

        if (!rootPoseChanged && Mathf.Approximately(lastAppliedGroundPlaneBounciness, GetActiveGroundPlaneBounciness()))
            return;

        EnsureGroundCollider();
    }

    private void DetectStuckBall(Vector3 worldPosition, Vector3 velocity)
    {
        if (isManagedFrozen || !ballRigidbody.useGravity || ballRigidbody.isKinematic)
        {
            stuckTimer = 0f;
            return;
        }

        float groundY = GetApproxGroundHeightWorld();
        Collider ballCollider = GetComponent<Collider>();
        float ballBottomY = ballCollider != null
            ? ballCollider.bounds.min.y
            : worldPosition.y;
        bool nearGround = ballBottomY <= groundY + groundCheckHeight;

        GameStateManager.RallyState rallyState = gameState != null
            ? gameState.State
            : GameStateManager.RallyState.WaitingToServe;

        bool allowStuckRecovery = gameState == null
            || rallyState == GameStateManager.RallyState.WaitingToServe
            || rallyState == GameStateManager.RallyState.InPlay;
        if (!allowStuckRecovery)
        {
            stuckTimer = 0f;
            return;
        }

        // Any ball that stays within groundCheckHeight of the court floor for longer
        // than stuckTimeout is considered rolling/stuck — regardless of speed. A
        // normally bouncing ball only grazes that band briefly between bounces so
        // the timer resets; only a rolling or stalled ball accumulates past the
        // timeout.
        if (nearGround)
        {
            stuckTimer += Time.unscaledDeltaTime;
            if (stuckTimer >= stuckTimeout)
            {
                stuckTimer = 0f;
                LogBallEvent("DetectStuckBall.thresholdReached");

                if (gameState != null && rallyState == GameStateManager.RallyState.InPlay)
                {
                    if (TryAwardStuckBallPoint())
                        return;

                    if (enableDebugLogs)
                    {
                        Debug.LogWarning("[BallDebug] Ball rolled/stalled before a valid return bounce — recovering without scoring.");
                    }
                    PublishBounceLifecycleEvent("StuckRecoveryReset", "InPlayBeforeValidReturn");
                    ForceRecoverBall("StuckInPlayBeforeValidReturn");
                }
                else
                {
                    if (enableDebugLogs)
                        Debug.LogWarning("[BallDebug] Ball became stuck/rolling outside active rally — forcing reset.");
                    PublishBounceLifecycleEvent("StuckRecoveryReset", "WaitingToServe");
                    ForceRecoverBall("StuckWaitingToServe");
                }
            }
            return;
        }

        stuckTimer = 0f;
    }

    private bool TryAwardStuckBallPoint()
    {
        if (gameState == null)
            return false;

        // Primary: the last hitter successfully cleared the net. Opponent failed to
        // return the ball before it stalled — award to the rally owner.
        if (hasCrossedNetAfterLastHit && gameState.LastHitter != GameStateManager.Hitter.None)
        {
            bool lastHitterIsPlayer = gameState.LastHitter == GameStateManager.Hitter.Player;
            if (enableDebugLogs)
            {
                Debug.LogWarning($"[BallDebug] Stuck live ball — rally owner wins (HasCrossedNet). LastHitter={gameState.LastHitter}");
            }
            PublishBounceLifecycleEvent("StuckRecoveryOwnerAward", "HasCrossedNet");
            // OnDoubleBounceOnSide awards to the side opposite bouncedOnPlayerSide,
            // so pass the hitter's opponent-side flag to award the hitter.
            gameState.OnDoubleBounceOnSide(bouncedOnPlayerSide: !lastHitterIsPlayer);
            return true;
        }

        // Secondary: two or more accepted bounces on a known side — finalise as a
        // double-bounce fault against that side.
        if (consecutiveBounceCount >= 2 && consecutiveBounceSide != BounceCourtSide.Unknown)
        {
            bool bouncedOnPlayerSide = consecutiveBounceSide == BounceCourtSide.Player;
            if (enableDebugLogs)
            {
                Debug.LogWarning($"[BallDebug] Stuck live ball had 2+ accepted same-side bounces — awarding via side={DescribeBounceCourtSide(consecutiveBounceSide)}");
            }
            PublishBounceLifecycleEvent("StuckRecoveryDoubleBounceAward", "TwoSameSideBouncesRecorded");
            gameState.OnDoubleBounceOnSide(bouncedOnPlayerSide);
            return true;
        }

        return false;
    }

    private void MaybeLogBallState(string source)
    {
        if (!enableDebugLogs || ballRigidbody == null)
            return;

        if (Time.unscaledTime - lastDebugLogTime < debugLogInterval)
            return;

        lastDebugLogTime = Time.unscaledTime;
        LogBallEvent($"State.{source}");
    }

    private static bool HasInvalidVector(Vector3 value)
    {
        return float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z)
            || float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z);
    }

    private bool TryGetBallLocalState(out Vector3 localPosition, out Vector3 localVelocity)
    {
        localPosition = transform.position;
        localVelocity = ballRigidbody != null ? ballRigidbody.linearVelocity : Vector3.zero;

        if (gameSpaceRoot != null)
        {
            localPosition = gameSpaceRoot.InverseTransformPoint(localPosition);
            localVelocity = gameSpaceRoot.InverseTransformDirection(localVelocity);
        }

        return !HasInvalidVector(localPosition) && !HasInvalidVector(localVelocity);
    }

    private bool TryGetBoundaryLocalPosition(CourtBoundary boundary, out Vector3 localPosition)
    {
        localPosition = Vector3.zero;
        if (boundary == null)
            return false;

        localPosition = boundary.transform.position;
        if (gameSpaceRoot != null)
            localPosition = gameSpaceRoot.InverseTransformPoint(localPosition);

        return !HasInvalidVector(localPosition);
    }

    private float GetBallRadius()
    {
        Collider col = GetComponent<Collider>();
        if (col == null)
            return 0.04f;

        if (col is SphereCollider sphere)
        {
            Vector3 scale = transform.lossyScale;
            float scaleFactor = Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
            return Mathf.Max(0.01f, sphere.radius * scaleFactor);
        }

        Vector3 extents = col.bounds.extents;
        return Mathf.Max(0.01f, Mathf.Min(extents.x, Mathf.Min(extents.y, extents.z)));
    }

    private bool ShouldScoreBoundaryFault(CourtBoundary boundary, Collision collision)
    {
        if (boundary == null || ballRigidbody == null)
            return true;

        if (!TryGetBallLocalState(out Vector3 ballLocalPosition, out _))
            return true;

        if (!TryGetBoundaryLocalPosition(boundary, out Vector3 boundaryLocalPosition))
            return true;

        float inboundImpactSpeed = 0f;
        if (collision != null && collision.contactCount > 0)
        {
            ContactPoint contact = collision.GetContact(0);
            inboundImpactSpeed = GetInboundImpactSpeed(collision, contact.normal);
        }

        float ballRadius = GetBallRadius();
        float outPlaneSlack = Mathf.Max(0f, outFaultPlaneTolerance);
        float netPlaneSlack = Mathf.Max(0f, netFaultPlaneTolerance);
        float minOutSpeed = Mathf.Max(0f, outFaultMinInboundImpactSpeed);
        float minNetSpeed = Mathf.Max(0f, netFaultMinInboundImpactSpeed);

        switch (boundary.boundaryType)
        {
            case CourtBoundary.BoundaryType.PlayerBackWall:
                return inboundImpactSpeed >= minOutSpeed
                    && ballLocalPosition.z <= boundaryLocalPosition.z + ballRadius + outPlaneSlack;

            case CourtBoundary.BoundaryType.BotBackWall:
                return inboundImpactSpeed >= minOutSpeed
                    && ballLocalPosition.z >= boundaryLocalPosition.z - ballRadius - outPlaneSlack;

            case CourtBoundary.BoundaryType.SideWall:
                return inboundImpactSpeed >= minOutSpeed
                    && Mathf.Abs(ballLocalPosition.x - boundaryLocalPosition.x) <= ballRadius + outPlaneSlack;

            case CourtBoundary.BoundaryType.Net:
            {
                float planeDelta = Mathf.Abs(ballLocalPosition.z - boundaryLocalPosition.z);
                if (inboundImpactSpeed < minNetSpeed)
                    return false;

                if (planeDelta > ballRadius + netPlaneSlack)
                    return false;

                Collider ballCollider = GetComponent<Collider>();
                Collider boundaryCollider = boundary.GetComponent<Collider>();
                float ballBottomY = ballCollider != null ? ballCollider.bounds.min.y : transform.position.y - ballRadius;
                float boundaryTopY = boundaryCollider != null ? boundaryCollider.bounds.max.y : boundary.transform.position.y;
                return ballBottomY <= boundaryTopY + netPlaneSlack;
            }

            default:
                    return true;
        }
    }

    private void NetFault()
    {
        if (gameState != null && gameState.State == GameStateManager.RallyState.InPlay)
        {
            LogBallEvent("NetFault");
            gameState.OnBallHitNet();
        }
    }

    private float GetInboundImpactSpeed(Collision collision, Vector3 surfaceNormal)
    {
        if (collision == null)
            return 0f;

        Vector3 normal = surfaceNormal.sqrMagnitude > 0.0001f
            ? surfaceNormal.normalized
            : Vector3.up;

        float relativeInbound = Mathf.Max(0f, -Vector3.Dot(collision.relativeVelocity, normal));
        float preSolveInbound = Mathf.Max(0f, -Vector3.Dot(lastFixedLinearVelocity, normal));
        return Mathf.Max(relativeInbound, preSolveInbound);
    }

    private void ApplyWaitingToServePerfectBounce(Vector3 surfaceNormal)
    {
        if (ballRigidbody == null)
            return;

        Vector3 normal = surfaceNormal.sqrMagnitude > 0.0001f
            ? surfaceNormal.normalized
            : Vector3.up;

        Vector3 incomingVelocity = HasInvalidVector(lastFixedLinearVelocity)
            ? ballRigidbody.linearVelocity
            : lastFixedLinearVelocity;

        if (HasInvalidVector(incomingVelocity) || incomingVelocity.sqrMagnitude <= 0.0001f)
            return;

        Vector3 reflectedVelocity = Vector3.Reflect(incomingVelocity, normal);
        float incomingSpeed = incomingVelocity.magnitude;
        if (reflectedVelocity.sqrMagnitude > 0.0001f && incomingSpeed > 0.0001f)
            reflectedVelocity = reflectedVelocity.normalized * incomingSpeed;

        // Keep the serve bounce vertical and lossless while waiting for the player to serve.
        reflectedVelocity.x = 0f;
        reflectedVelocity.z = 0f;
        reflectedVelocity.y = Mathf.Abs(reflectedVelocity.y);

        ballRigidbody.linearVelocity = reflectedVelocity;
        if (ballRigidbody.angularVelocity.sqrMagnitude > 0.0001f)
            ballRigidbody.angularVelocity = Vector3.zero;

        LogBallEvent($"WaitingToServePerfectBounce vel={FormatVector(reflectedVelocity)}");
    }

    private float GetActiveGroundPlaneBounciness()
    {
        bool waitingToServe = useWaitingToServeGroundBounceOverride
            && gameState != null
            && gameState.State == GameStateManager.RallyState.WaitingToServe;
        return waitingToServe
            ? Mathf.Clamp01(waitingToServeGroundPlaneBounciness)
            : Mathf.Clamp01(groundPlaneBounciness);
    }

    /// <summary>
    /// Creates a thin invisible box at Y = 0 inside GameSpaceRoot.
    /// This acts as the court floor so the ball can bounce on it.
    /// </summary>
    private void EnsureGroundCollider()
    {
        // Find or create floor as a scene-root object (not under GameSpaceRoot,
        // so anchor destruction doesn't take it with it).
        GameObject floor = GameObject.Find(CourtFloorName);
        if (floor == null)
            floor = new GameObject(CourtFloorName);

        float padding = Mathf.Max(0f, groundPlanePadding);
        float minX = Mathf.Min(inBoundsMinLocalX, inBoundsMaxLocalX) - padding;
        float maxX = Mathf.Max(inBoundsMinLocalX, inBoundsMaxLocalX) + padding;
        float minZ = Mathf.Min(inBoundsMinLocalZ, inBoundsMaxLocalZ) - padding;
        float maxZ = Mathf.Max(inBoundsMinLocalZ, inBoundsMaxLocalZ) + padding;

        float centerX = (minX + maxX) * 0.5f;
        float centerZ = (minZ + maxZ) * 0.5f;
        float sizeX = Mathf.Max(0.1f, maxX - minX);
        float sizeZ = Mathf.Max(0.1f, maxZ - minZ);

        // Convert court-local center to world position
        Vector3 worldCenter = gameSpaceRoot.TransformPoint(new Vector3(centerX, -0.005f, centerZ));
        floor.transform.SetPositionAndRotation(worldCenter, gameSpaceRoot.rotation);

        BoxCollider box = floor.GetComponent<BoxCollider>();
        if (box == null)
            box = floor.AddComponent<BoxCollider>();

        box.size = new Vector3(sizeX, 0.01f, sizeZ);
        box.isTrigger = false;

        // Create/update a bouncy physics material so the ball bounces during serve.
        // These values are exposed in the PracticeBallController inspector.
        PhysicsMaterial material = box.sharedMaterial;
        if (material == null)
        {
            material = new PhysicsMaterial("CourtFloor");
            box.sharedMaterial = material;
        }

        material.bounciness = GetActiveGroundPlaneBounciness();
        material.dynamicFriction = Mathf.Clamp01(groundPlaneDynamicFriction);
        material.staticFriction = Mathf.Clamp01(groundPlaneStaticFriction);
        material.bounceCombine = groundPlaneBounceCombine;
        material.frictionCombine = groundPlaneFrictionCombine;
        lastAppliedGroundPlaneBounciness = material.bounciness;
        lastGroundColliderRootPosition = gameSpaceRoot.position;
        lastGroundColliderRootRotation = gameSpaceRoot.rotation;
        hasGroundColliderRootPose = true;
    }

    private static bool IsCourtBounceSurface(Collision collision, float normalY)
    {
        if (collision == null)
            return false;

        return collision.gameObject.name == MainCourtSurfaceName;
    }

    /// <summary>
    /// Handles ball collisions with court boundaries.
    /// If CourtBoundary + GameStateManager are configured, triggers proper scoring.
    /// Falls back to simple reset for legacy "Wall"-tagged objects.
    /// </summary>
    private void OnCollisionEnter(Collision collision)
    {
        // Check for CourtBoundary component (new scoring system)
        var boundary = collision.gameObject.GetComponent<CourtBoundary>();
        if (boundary == null)
            boundary = collision.transform.GetComponentInParent<CourtBoundary>();

        if (boundary != null)
        {
            if (gameState != null)
            {
                switch (boundary.boundaryType)
                {
                    case CourtBoundary.BoundaryType.PlayerBackWall:
                        if (ShouldScoreBoundaryFault(boundary, collision))
                        {
                            LogBallEvent("Boundary.PlayerBackWall");
                            gameState.OnBallOutPlayerSide();
                        }
                        else if (enableDebugLogs)
                        {
                            Debug.LogWarning("[BallDebug] Ignored PlayerBackWall fault due to weak/non-planar impact.");
                        }
                        break;
                    case CourtBoundary.BoundaryType.BotBackWall:
                        if (ShouldScoreBoundaryFault(boundary, collision))
                        {
                            LogBallEvent("Boundary.BotBackWall");
                            gameState.OnBallOutBotSide();
                        }
                        else if (enableDebugLogs)
                        {
                            Debug.LogWarning("[BallDebug] Ignored BotBackWall fault due to weak/non-planar impact.");
                        }
                        break;
                    case CourtBoundary.BoundaryType.SideWall:
                        if (ShouldScoreBoundaryFault(boundary, collision))
                        {
                            LogBallEvent("Boundary.SideWall");
                            gameState.OnBallOutSideWall();
                        }
                        else if (enableDebugLogs)
                        {
                            Debug.LogWarning("[BallDebug] Ignored SideWall fault due to weak/non-planar impact.");
                        }
                        break;
                    case CourtBoundary.BoundaryType.Net:
                        CancelInvoke(nameof(NetFault));
                        if (ShouldScoreBoundaryFault(boundary, collision))
                        {
                            Invoke(nameof(NetFault), 0.4f);
                            LogBallEvent("Boundary.Net");
                        }
                        else if (enableDebugLogs)
                        {
                            Debug.LogWarning("[BallDebug] Ignored Net fault due to weak/non-planar/high-clear impact.");
                        }
                        return; // don't skip physics — let the ball bounce
                }
            }
            else
            {
                LogBallEvent("Boundary.ResetBallFallback");
                ResetBall();
            }
            return;
        }

        // Legacy fallback: "Wall" tag without CourtBoundary
        if (collision.transform.CompareTag("Wall"))
        {
            LogBallEvent("LegacyWall.ResetBall");
            ResetBall();
            return;
        }

        // ── Ground bounce detection (double/triple bounce faulting) ─────────
        if (collision.contactCount <= 0)
            return;

        ContactPoint contact = collision.GetContact(0);
        Vector3 normal = contact.normal;
        float normalY = normal.y;
        float inboundBounceSpeed = GetInboundImpactSpeed(collision, normal);
        float dedupeWindow = Mathf.Max(0f, bounceDedupeSeconds);
        Vector3 localContactPoint = gameSpaceRoot != null
            ? gameSpaceRoot.InverseTransformPoint(contact.point)
            : contact.point;
        const float noRequiredNormal = 0f;
        const float noRequiredSpeed = 0f;

        if (!IsCourtBounceSurface(collision, normalY))
        {
            PublishBounceDiagnostic("GroundContactRejected", false, "NonCourtFloorSurface",
                localContactPoint, normalY, inboundBounceSpeed, noRequiredNormal, noRequiredSpeed);
            return;
        }

        if (gameState == null)
        {
            PublishBounceDiagnostic("GroundContactRejected", false, "MissingGameState",
                localContactPoint, normalY, inboundBounceSpeed, noRequiredNormal, noRequiredSpeed);
            return;
        }

        if (gameState.State == GameStateManager.RallyState.WaitingToServe)
        {
            if (normalY > 0.7f)
                ApplyWaitingToServePerfectBounce(normal);

            PublishBounceDiagnostic("GroundContactRejected", false, "WaitingToServePerfectBounce",
                localContactPoint, normalY, inboundBounceSpeed, noRequiredNormal, noRequiredSpeed);
            return;
        }

        if (gameState.State != GameStateManager.RallyState.InPlay)
        {
            PublishBounceDiagnostic("GroundContactRejected", false, "StateNotInPlay",
                localContactPoint, normalY, inboundBounceSpeed, noRequiredNormal, noRequiredSpeed);
            return;
        }

        if (Time.frameCount == lastBounceFrame)
        {
            PublishBounceDiagnostic("GroundContactRejected", false, "DuplicateFrame",
                localContactPoint, normalY, inboundBounceSpeed, noRequiredNormal, noRequiredSpeed);
            return;
        }

        float elapsedSinceLastBounce = Time.time - lastBounceTime;
        if (elapsedSinceLastBounce < dedupeWindow)
        {
            PublishBounceDiagnostic("GroundContactRejected", false, "WithinDedupeWindow",
                localContactPoint, normalY, inboundBounceSpeed, noRequiredNormal, noRequiredSpeed);
            return;
        }

        lastBounceFrame = Time.frameCount;
        lastBounceTime = Time.time;

        float ballZ = localContactPoint.z;
        bool isOutsideInBoundsZone = IsOutsideInBoundsZone(localContactPoint);
        bool isFirstAcceptedGroundBounce = bounceCount == 0;
        if (detectOutOfBoundsOnFirstGroundBounce && isFirstAcceptedGroundBounce && isOutsideInBoundsZone)
        {
            if (enableDebugLogs)
            {
                Debug.LogWarning($"[BallDebug] Out-of-bounds bounce detected at local={FormatVector(localContactPoint)}");
            }

            PublishBounceDiagnostic("GroundBounceFaultOutOfBounds", true, string.Empty,
                localContactPoint, normalY, inboundBounceSpeed, noRequiredNormal, noRequiredSpeed);
            gameState.OnBallOutOfBounds(ballZ);
            return;
        }

        bounceCount++;
        BounceCourtSide bounceSide = ClassifyBounceCourtSide(ballZ);
        if (bounceCount == 1)
        {
            consecutiveBounceSide = bounceSide;
            if (bounceSide == BounceCourtSide.Unknown)
            {
                consecutiveBounceCount = 0;
                firstBounceLocalZ = float.NaN;
                firstBounceLocalPoint = InvalidLocalContactPoint;
                firstBounceTime = float.NaN;
            }
            else
            {
                consecutiveBounceCount = 1;
                firstBounceLocalZ = ballZ;
                firstBounceLocalPoint = localContactPoint;
                firstBounceTime = Time.time;
                // Mark the rally as "owned" by the last hitter if the first bounce
                // cleared the net onto the opponent's court. Every subsequent event
                // (second bounce, wall, net, out, stall) will now award to them.
                if (IsOppositeCourtSideFromLastHitter(bounceSide))
                {
                    hasCrossedNetAfterLastHit = true;
                    if (enableDebugLogs)
                    {
                        Debug.Log($"[BallDebug] HasCrossedNet=true after first bounce on {DescribeBounceCourtSide(bounceSide)} " +
                                  $"(last hitter={gameState.LastHitter}).");
                    }
                }
            }
        }
        else if (bounceSide != BounceCourtSide.Unknown && consecutiveBounceSide == bounceSide)
        {
            consecutiveBounceCount++;
        }
        else
        {
            consecutiveBounceSide = bounceSide;
            consecutiveBounceCount = bounceSide == BounceCourtSide.Unknown ? 0 : 1;
        }

        LogBallEvent($"GroundBounce count={bounceCount} sameSideCount={consecutiveBounceCount} side={DescribeBounceCourtSide(bounceSide)}");
        PublishBounceDiagnostic("GroundBounceAccepted", true, string.Empty,
            localContactPoint, normalY, inboundBounceSpeed, noRequiredNormal, noRequiredSpeed);

        if (bounceCount >= 2)
        {
            if (consecutiveBounceCount < 2 || consecutiveBounceSide == BounceCourtSide.Unknown)
            {
                PublishBounceDiagnostic("DoubleBounceRejected", false, "SecondBounceDifferentSide",
                    localContactPoint, normalY, inboundBounceSpeed, noRequiredNormal, noRequiredSpeed);
                return;
            }

            bool bouncedOnPlayerSide = consecutiveBounceSide == BounceCourtSide.Player;
            PublishBounceDiagnostic("DoubleBounceAwardAttempt", true, string.Empty,
                localContactPoint, normalY, inboundBounceSpeed, noRequiredNormal, noRequiredSpeed);
            gameState.OnDoubleBounceOnSide(bouncedOnPlayerSide);
        }
    }

    private void PublishBounceLifecycleEvent(string eventType, string reason)
    {
        PublishBounceDiagnostic(eventType, false, reason,
            InvalidLocalContactPoint, float.NaN, float.NaN, float.NaN, float.NaN);
    }

    private void PublishBounceDiagnostic(
        string eventType,
        bool accepted,
        string rejectReason,
        Vector3 localContactPoint,
        float normalY,
        float inboundSpeed,
        float requiredNormalY,
        float requiredInboundSpeed)
    {
        if (!publishBounceDiagnosticsToMqtt)
            return;

        if (mqttController == null)
            mqttController = FindFirstObjectByType<MqttController>();
        if (mqttController == null)
            return;

        string rallyState = gameState != null
            ? gameState.State.ToString()
            : "NoGameState";

        mqttController.PublishBallBounceDebug(
            eventType,
            rallyState,
            bounceCount,
            accepted,
            rejectReason,
            normalY,
            inboundSpeed,
            requiredNormalY,
            requiredInboundSpeed,
            localContactPoint);
    }

    private void LogBallEvent(string eventName)
    {
        if (!enableDebugLogs)
            return;

        Vector3 worldPos = transform.position;
        Vector3 localPos = gameSpaceRoot != null
            ? gameSpaceRoot.InverseTransformPoint(worldPos)
            : worldPos;
        Vector3 velocity = ballRigidbody != null ? ballRigidbody.linearVelocity : Vector3.zero;
        Vector3 angularVelocity = ballRigidbody != null ? ballRigidbody.angularVelocity : Vector3.zero;
        string state = gameState != null ? gameState.State.ToString() : "NoGameState";

        Debug.Log(
            $"[BallDebug] event={eventName} active={gameObject.activeInHierarchy} state={state} " +
            $"managedFrozen={isManagedFrozen} bounceCount={bounceCount} " +
            $"worldPos={FormatVector(worldPos)} localPos={FormatVector(localPos)} " +
            $"vel={FormatVector(velocity)} angVel={FormatVector(angularVelocity)} " +
            $"gravity={(ballRigidbody != null && ballRigidbody.useGravity)} " +
            $"kinematic={(ballRigidbody != null && ballRigidbody.isKinematic)} " +
            $"detectCollisions={(ballRigidbody != null && ballRigidbody.detectCollisions)} " +
            $"timeScale={Time.timeScale:F2}");
    }

    private static string FormatVector(Vector3 value)
    {
        return $"({value.x:F3},{value.y:F3},{value.z:F3})";
    }
}
