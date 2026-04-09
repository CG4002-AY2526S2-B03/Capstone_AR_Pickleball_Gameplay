using UnityEngine;

public class PracticeBallController : MonoBehaviour
{
    public static PracticeBallController Instance { get; private set; }

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
    public Vector3 courtServeLocalPos = new Vector3(0.44f, 1.0f, 2.0f);

    [Tooltip("Height above court (court-local Y) at which the ball spawns for serving.")]
    public float serveHeight = 2.5f;
    [Tooltip("Height above court (court-local Y) used by button-triggered ball resets.")]
    public float resetHeight = 3f;
    [Tooltip("Horizontal distance in front of the camera used by button-triggered resets.")]
    public float resetDistanceFromCamera = 0.5f;
    [Tooltip("Minimum forward distance (court +Z) from camera for camera-based resets.")]
    public float minResetForwardOffsetFromCamera = 1.0f;
    [Tooltip("Lowest court-local Z allowed for camera-based resets.")]
    public float minResetLocalZ = 0.0f;
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
    [Tooltip("Automatically creates an invisible floor collider at Y=0 " +
             "inside GameSpaceRoot so the ball cannot fall through the court.")]
    public bool createGroundPlane = true;

    [Header("Game State")]
    [Tooltip("When set, boundary collisions trigger scoring instead of raw resets.")]
    public GameStateManager gameState;

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

    private Rigidbody ballRigidbody;
    private Vector3 initialLocalPosition;
    private Transform gameSpaceRoot;
    private int bounceCount;
    private float stuckTimer;
    private bool isManagedFrozen;
    private float lastDebugLogTime;
    private int lastBounceFrame = -1; // prevents double-counting two colliders in same frame
    private float firstBounceLocalZ = float.NaN;
    private float servePaddleMaxY = float.NegativeInfinity;

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
        DisableTrailAutoDestruct();

        // Walk up the hierarchy to find the GameSpaceRoot parent.
        // Ball2 is a direct child of GameSpaceRoot.
        gameSpaceRoot = transform.parent;

        // Remember the ball's original local position (set in the prefab / scene).
        initialLocalPosition = transform.localPosition;

        // Detach from GameSpaceRoot so ARFoundation destroying the anchor
        // (or any other cause of GameSpaceRoot destruction) cannot cascade to the ball.
        // We keep the gameSpaceRoot *reference* for local-coordinate math.
        if (!_isCreatingBackup)
        {
            transform.SetParent(null, true);
            Object.DontDestroyOnLoad(gameObject);
            Debug.Log("[Ball] Detached from GameSpaceRoot and marked DontDestroyOnLoad.");
        }

        // Create a hidden backup clone so we can respawn if even DontDestroyOnLoad fails.
        // Guard against infinite recursion: the clone's Awake would try to clone again.
        if (_backupPrefab == null && !_isCreatingBackup)
        {
            _isCreatingBackup = true;
            _backupPrefab = Instantiate(gameObject);
            _backupPrefab.name = "_BallBackup";
            _backupPrefab.SetActive(false);
            Object.DontDestroyOnLoad(_backupPrefab);
            _isCreatingBackup = false;
            Debug.Log("[Ball] Backup prefab created in DontDestroyOnLoad.");
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

        // Create an invisible floor so the ball always bounces on the court surface.
        if (createGroundPlane && gameSpaceRoot != null)
        {
            EnsureGroundCollider();
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
                ForceRecoverBall();
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

    /// <summary>
    /// Resets the ball: drops it from 3m height, 0.5m in front of the main
    /// camera, with gravity enabled so the player can serve.
    /// Falls back to courtServeLocalPos if no camera is available.
    /// </summary>
    public void ResetBall()
    {
        CancelInvoke(nameof(NetFault));
        bounceCount = 0;
        firstBounceLocalZ = float.NaN;
        servePaddleMaxY = float.NegativeInfinity;
        lastBounceFrame = -1;
        stuckTimer = 0f;
        isManagedFrozen = false;
        LogBallEvent("ResetBall.begin");

        if (!gameObject.activeInHierarchy)
        {
            // Activate the entire parent chain — the ball being self-active
            // is useless if a parent (e.g. GameSpaceRoot) is inactive.
            Transform t = transform;
            while (t != null)
            {
                if (!t.gameObject.activeSelf)
                {
                    Debug.LogWarning($"[Ball] ResetBall: reactivating inactive ancestor '{t.name}'");
                    t.gameObject.SetActive(true);
                }
                t = t.parent;
            }
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);
        }

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
    private void ForceRecoverBall()
    {
        isManagedFrozen = false;
        stuckTimer = 0f;
        LogBallEvent("ForceRecoverBall.begin");
        SanitiseRigidbody();
        ResetBall();
    }

    /// <summary>
    /// Clears all Rigidbody state to prevent NaN propagation.
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
        firstBounceLocalZ = float.NaN;
        LogBallEvent("ResetBounceCount");
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

    private void DetectStuckBall(Vector3 worldPosition, Vector3 velocity)
    {
        if (isManagedFrozen || !ballRigidbody.useGravity || ballRigidbody.isKinematic)
        {
            stuckTimer = 0f;
            return;
        }

        float courtY = gameSpaceRoot != null ? gameSpaceRoot.position.y : 0f;
        bool nearGround = worldPosition.y <= courtY + groundCheckHeight;
        bool movingSlowly = velocity.magnitude <= stuckSpeedThreshold;

        if (nearGround && movingSlowly)
        {
            stuckTimer += Time.unscaledDeltaTime;
            if (stuckTimer >= stuckTimeout)
            {
                LogBallEvent("DetectStuckBall.thresholdReached");

                // If the rally is live, a stuck ball on the ground means the
                // receiving side failed to return — award the point instead of
                // silently resetting (which would reset bounceCount and loop).
                if (gameState != null && gameState.State == GameStateManager.RallyState.InPlay)
                {
                    float ballZ = gameSpaceRoot != null
                        ? gameSpaceRoot.InverseTransformPoint(worldPosition).z
                        : worldPosition.z;
                    bool ballOnPlayerSide = ballZ < (gameState != null ? gameState.GetNetLocalZ() : 5.4f);

                    if (enableDebugLogs)
                    {
                        Debug.LogWarning($"[BallDebug] Ball stuck during rally on {(ballOnPlayerSide ? "player" : "bot")} side — awarding point.");
                    }
                    if (ballOnPlayerSide)
                        gameState.OnBallOutPlayerSide();   // bot scores
                    else
                        gameState.OnBallOutBotSide();      // player scores
                }
                else
                {
                    if (enableDebugLogs)
                        Debug.LogWarning("[BallDebug] Ball became stuck/rolling on court — forcing reset.");
                    ForceRecoverBall();
                }
            }
            return;
        }

        stuckTimer = 0f;
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

    private void NetFault()
    {
        if (gameState != null && gameState.State == GameStateManager.RallyState.InPlay)
        {
            LogBallEvent("NetFault");
            gameState.OnBallHitNet();
        }
    }

    /// <summary>
    /// Creates a thin invisible box at Y = 0 inside GameSpaceRoot.
    /// This acts as the court floor so the ball can bounce on it.
    /// </summary>
    private void EnsureGroundCollider()
    {
        const string floorName = "_CourtFloor";
        if (gameSpaceRoot.Find(floorName) != null) return; // already exists

        var floor = new GameObject(floorName);
        floor.transform.SetParent(gameSpaceRoot, false);
        floor.transform.localPosition = new Vector3(0f, -0.005f, 4f); // centered slightly below Y=0, Z≈mid-court
        floor.transform.localRotation = Quaternion.identity;

        var box = floor.AddComponent<BoxCollider>();
        box.size = new Vector3(14f, 0.01f, 16f); // generous coverage
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
                        LogBallEvent("Boundary.PlayerBackWall");
                        gameState.OnBallOutPlayerSide();
                        break;
                    case CourtBoundary.BoundaryType.BotBackWall:
                        LogBallEvent("Boundary.BotBackWall");
                        gameState.OnBallOutBotSide();
                        break;
                    case CourtBoundary.BoundaryType.SideWall:
                        LogBallEvent("Boundary.SideWall");
                        gameState.OnBallOutSideWall();
                        break;
                    case CourtBoundary.BoundaryType.Net:
                        // Let the ball physically bounce off the net first (solid collider),
                        // then score the fault after a short delay so it looks natural.
                        Invoke(nameof(NetFault), 0.4f);
                        LogBallEvent("Boundary.Net");
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

        // ── Ground bounce detection (double bounce = point) ──────────────────
        // A ground hit has a mostly-upward contact normal.
        if (gameState != null && gameState.State == GameStateManager.RallyState.InPlay
            && collision.contactCount > 0)
        {
            Vector3 normal = collision.GetContact(0).normal;
            if (normal.y > 0.7f && Time.frameCount != lastBounceFrame)
            {
                lastBounceFrame = Time.frameCount;
                bounceCount++;
                float ballZ = gameSpaceRoot != null
                    ? gameSpaceRoot.InverseTransformPoint(transform.position).z
                    : transform.position.z;
                if (bounceCount == 1)
                    firstBounceLocalZ = ballZ;

                LogBallEvent($"GroundBounce count={bounceCount}");
                if (bounceCount >= 2)
                {
                    // Score by first-bounce side. If the second bounce crosses the
                    // net due physics artifacts, first bounce still indicates
                    // which side failed to return the ball.
                    float decisiveBounceZ = float.IsNaN(firstBounceLocalZ) ? ballZ : firstBounceLocalZ;
                    float netZ = gameState.GetNetLocalZ();
                    bool firstOnPlayerSide = decisiveBounceZ < netZ;
                    bool secondOnPlayerSide = ballZ < netZ;
                    if (firstOnPlayerSide != secondOnPlayerSide)
                    {
                        if (enableDebugLogs)
                        {
                            Debug.LogWarning($"[BallDebug] Double-bounce crossed net: firstZ={decisiveBounceZ:F3}, secondZ={ballZ:F3}, netZ={netZ:F3}. Using first bounce side for scoring.");
                        }
                    }

                    gameState.OnDoubleBounce(decisiveBounceZ);
                }
            }
        }
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
