using UnityEngine;

public class PracticeBallController : MonoBehaviour
{
    public static PracticeBallController Instance { get; private set; }

    public static PracticeBallController GetLiveInstance()
    {
        if (Instance != null && Instance.gameObject.scene.isLoaded)
            return Instance;

        PracticeBallController found = FindFirstObjectByType<PracticeBallController>();
        if (found != null)
        {
            Instance = found;
            return found;
        }

        foreach (PracticeBallController candidate in Resources.FindObjectsOfTypeAll<PracticeBallController>())
        {
            if (candidate != null && candidate.gameObject.scene.isLoaded)
            {
                Instance = candidate;
                return candidate;
            }
        }

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
    [Tooltip("Lowest court-local Z allowed for camera-based resets.")]
    public float minResetLocalZ = 0.75f;
    [Tooltip("Keep camera-based resets this far on the player side of the net.")]
    public float resetNetClearance = 1.0f;

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
    public bool enableDebugLogs = true;
    [Tooltip("Seconds between periodic ball state logs while debugging.")]
    public float debugLogInterval = 0.75f;

    private Rigidbody ballRigidbody;
    private Vector3 initialLocalPosition;
    private Transform gameSpaceRoot;
    private int bounceCount;
    private float stuckTimer;
    private bool isManagedFrozen;
    private float lastDebugLogTime;

    private void Awake()
    {
        Instance = this;
        EnsureRuntimeBallTag();
        ballRigidbody = GetComponent<Rigidbody>();

        // Walk up the hierarchy to find the GameSpaceRoot parent.
        // Ball2 is a direct child of GameSpaceRoot.
        gameSpaceRoot = transform.parent;

        // Remember the ball's original local position (set in the prefab / scene).
        initialLocalPosition = transform.localPosition;
    }

    private void OnEnable()
    {
        Instance = this;
        EnsureRuntimeBallTag();
        LogBallEvent("OnEnable");
    }

    private void OnDisable()
    {
        LogBallEvent("OnDisable");
    }

    private void OnDestroy()
    {
        LogBallEvent("OnDestroy");
        if (Instance == this)
            Instance = null;
    }

    private void Start()
    {
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

    /// <summary>
    /// Resets the ball: drops it from 3m height, 0.5m in front of the main
    /// camera, with gravity enabled so the player can serve.
    /// Falls back to courtServeLocalPos if no camera is available.
    /// </summary>
    public void ResetBall()
    {
        CancelInvoke(nameof(NetFault));
        bounceCount = 0;
        stuckTimer = 0f;
        isManagedFrozen = false;
        LogBallEvent("ResetBall.begin");

        if (!gameObject.activeInHierarchy)
            gameObject.SetActive(true);

        // Fully sanitise the Rigidbody before repositioning —
        // clears NaN and corrupted physics state
        SanitiseRigidbody();

        Vector3 targetWorldPosition;
        Camera cam = Camera.main;
        if (cam != null)
        {
            // 0.5m forward from camera (horizontal direction only)
            Vector3 camFwd = cam.transform.forward;
            camFwd.y = 0f;
            if (camFwd.sqrMagnitude < 0.0001f) camFwd = Vector3.forward;
            camFwd.Normalize();

            Vector3 worldPos = cam.transform.position + camFwd * resetDistanceFromCamera;
            if (gameSpaceRoot != null)
            {
                Vector3 cameraLocal = gameSpaceRoot.InverseTransformPoint(cam.transform.position);
                Vector3 localForward = gameSpaceRoot.InverseTransformDirection(camFwd);
                localForward.y = 0f;
                if (localForward.sqrMagnitude < 0.0001f)
                    localForward = Vector3.forward;
                localForward.Normalize();

                bool hasNetZ = TryGetNetLocalZ(out float netZ);
                float maxResetLocalZ = hasNetZ
                    ? Mathf.Max(minResetLocalZ, netZ - resetNetClearance)
                    : float.PositiveInfinity;
                Vector3 desiredLocal = cameraLocal + localForward * resetDistanceFromCamera;

                float unclampedZ = desiredLocal.z;
                desiredLocal.z = hasNetZ
                    ? Mathf.Clamp(desiredLocal.z, minResetLocalZ, maxResetLocalZ)
                    : Mathf.Max(desiredLocal.z, minResetLocalZ);
                desiredLocal.y = resetHeight;

                if (!hasNetZ && enableDebugLogs)
                {
                    Debug.LogWarning("[BallDebug] Net local Z could not be resolved; camera reset is only clamped to minResetLocalZ.");
                }
                else if (enableDebugLogs && Mathf.Abs(unclampedZ - desiredLocal.z) > 0.001f)
                {
                    Debug.Log($"[BallDebug] Camera reset clamped from localZ={unclampedZ:F3} to {desiredLocal.z:F3} " +
                              $"(netZ={netZ:F3}, clearance={resetNetClearance:F3})");
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
            Debug.LogWarning("[BallDebug] Rigidbody corrupted (NaN/Inf) — reconstructing.");
            // Temporarily disable and re-enable to force Unity to reset internal physics state
            ballRigidbody.isKinematic = true;
            transform.position = GetFallbackServeWorldPosition();
            ballRigidbody.isKinematic = false;
            LogBallEvent("SanitiseRigidbody.reconstructed");
        }

        ballRigidbody.constraints = RigidbodyConstraints.None;
        ballRigidbody.linearVelocity = Vector3.zero;
        ballRigidbody.angularVelocity = Vector3.zero;
        ballRigidbody.useGravity = false;
        ballRigidbody.isKinematic = false;
        ballRigidbody.detectCollisions = true;
        LogBallEvent("SanitiseRigidbody.end");
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
        catch (UnityException)
        {
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
                Debug.LogWarning("[BallDebug] Ball became stuck/rolling on court — forcing reset.");
                LogBallEvent("DetectStuckBall.thresholdReached");
                ForceRecoverBall();
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
            if (normal.y > 0.7f)
            {
                bounceCount++;
                LogBallEvent($"GroundBounce count={bounceCount}");
                if (bounceCount >= 2)
                {
                    // Ball bounced twice — point to the opponent of whoever
                    // should have returned it. Use court-local Z to determine side.
                    float ballZ = gameSpaceRoot != null
                        ? transform.localPosition.z
                        : transform.position.z;

                    gameState.OnDoubleBounce(ballZ);
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
