using UnityEngine;

public class PracticeBallController : MonoBehaviour
{
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

    [Header("Ground Safety")]
    [Tooltip("Automatically creates an invisible floor collider at Y=0 " +
             "inside GameSpaceRoot so the ball cannot fall through the court.")]
    public bool createGroundPlane = true;

    [Header("Game State")]
    [Tooltip("When set, boundary collisions trigger scoring instead of raw resets.")]
    public GameStateManager gameState;

    [Header("MQTT")]
    [Tooltip("When set, publishes /netHit events when the ball crosses the net.")]
    public MqttController mqttController;

    [Header("Controls")]
    public KeyCode resetKey = KeyCode.R;

    private Rigidbody ballRigidbody;
    private Vector3 initialLocalPosition;
    private Transform gameSpaceRoot;
    private DeadHangBall deadHang;
    private int bounceCount;

    /// <summary>True while the ball is frozen in mid-air waiting for a paddle hit.</summary>
    public bool IsFrozen => deadHang != null && deadHang.IsFrozen;

    private void Awake()
    {
        ballRigidbody = GetComponent<Rigidbody>();
        deadHang = GetComponent<DeadHangBall>();

        // Walk up the hierarchy to find the GameSpaceRoot parent.
        // Ball2 is a direct child of GameSpaceRoot.
        gameSpaceRoot = transform.parent;

        // Remember the ball's original local position (set in the prefab / scene).
        initialLocalPosition = transform.localPosition;

        // DeadHangBall.Awake() already freezes the ball.
    }

    private void Start()
    {
        if (mqttController == null)
            mqttController = FindFirstObjectByType<MqttController>();

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
            if (float.IsNaN(pos.x) || float.IsNaN(pos.y) || float.IsNaN(pos.z)
                || pos.magnitude > 500f)
            {
                Debug.LogWarning("[Ball] Position is NaN or out of bounds — forcing reset.");
                ForceRecoverBall();
            }
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

        if (!gameObject.activeInHierarchy)
            gameObject.SetActive(true);

        // Fully sanitise the Rigidbody before repositioning —
        // clears NaN and corrupted physics state
        SanitiseRigidbody();

        Camera cam = Camera.main;
        if (cam != null)
        {
            // 0.5m forward from camera (horizontal direction only)
            Vector3 camFwd = cam.transform.forward;
            camFwd.y = 0f;
            if (camFwd.sqrMagnitude < 0.0001f) camFwd = Vector3.forward;
            camFwd.Normalize();

            Vector3 worldPos = cam.transform.position + camFwd * 0.5f;

            // Set height to 3m in court-local space
            if (gameSpaceRoot != null)
            {
                Vector3 local = gameSpaceRoot.InverseTransformPoint(worldPos);
                local.y = 3f;
                // Clamp to player side so ball never spawns on the bot's side of the net
                float netZ = gameState != null ? gameState.netZPosition : 5.4f;
                if (local.z >= netZ - 1f)
                    local.z = netZ - 1f;
                transform.localPosition = local;
            }
            else
            {
                worldPos.y = 3f;
                transform.position = worldPos;
            }
        }
        else
        {
            // No camera — fall back to fixed court-local position at 3m height
            PlaceAtServePosition();
        }

        transform.localRotation = Quaternion.identity;

        // Release with zero velocity and gravity enabled so the ball falls
        if (ballRigidbody != null)
        {
            ballRigidbody.constraints = RigidbodyConstraints.None;
            ballRigidbody.linearVelocity = Vector3.zero;
            ballRigidbody.angularVelocity = Vector3.zero;
            ballRigidbody.useGravity = true;
            ballRigidbody.position = transform.position;
        }
        if (deadHang != null)
            deadHang.IsFrozen = false;

        Debug.Log("[Ball] Reset: dropped 3m high, 0.5m in front of camera.");
    }

    /// <summary>
    /// Nuclear recovery: fully reconstruct the Rigidbody when it enters
    /// a corrupted state (NaN position/velocity, out of bounds, etc.).
    /// </summary>
    private void ForceRecoverBall()
    {
        // Move transform to a known-good position first
        if (gameSpaceRoot != null)
            transform.localPosition = courtServeLocalPos;
        else
            transform.position = Vector3.up * 3f;
        transform.localRotation = Quaternion.identity;

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
        bool corrupted = float.IsNaN(ballRigidbody.position.x)
                      || float.IsInfinity(ballRigidbody.position.x)
                      || float.IsNaN(ballRigidbody.linearVelocity.x)
                      || float.IsInfinity(ballRigidbody.linearVelocity.x);

        if (corrupted)
        {
            Debug.LogWarning("[Ball] Rigidbody corrupted (NaN/Inf) — reconstructing.");
            // Temporarily disable and re-enable to force Unity to reset internal physics state
            ballRigidbody.isKinematic = true;
            transform.position = gameSpaceRoot != null
                ? gameSpaceRoot.TransformPoint(courtServeLocalPos)
                : Vector3.up * 3f;
            ballRigidbody.isKinematic = false;
        }

        ballRigidbody.constraints = RigidbodyConstraints.None;
        ballRigidbody.linearVelocity = Vector3.zero;
        ballRigidbody.angularVelocity = Vector3.zero;
        ballRigidbody.useGravity = false;
    }

    /// <summary>Alias kept for callers that used the old name.</summary>
    public void DropBallInFrontOfCamera() => ResetBall();

    /// <summary>
    /// Freezes the ball in place during the point result display.
    /// Delegates to DeadHangBall if present, otherwise stops the Rigidbody directly.
    /// </summary>
    public void FreezeInPlace()
    {
        if (deadHang != null)
        {
            deadHang.Freeze();
            return;
        }
        if (ballRigidbody != null)
        {
            ballRigidbody.linearVelocity = Vector3.zero;
            ballRigidbody.angularVelocity = Vector3.zero;
            ballRigidbody.useGravity = false;
            ballRigidbody.isKinematic = true;
        }
    }

    /// <summary>
    /// Resets the ground bounce counter. Called by GameStateManager
    /// when the ball is hit by the player or bot.
    /// </summary>
    public void ResetBounceCount()
    {
        bounceCount = 0;
    }

    /// <summary>
    /// Called by PaddleHitController (or BotHitController) when the paddle
    /// hits the ball.  Unfreezes the ball and enables gravity so it follows
    /// a real arc.
    /// </summary>
    public void EnableGravity()
    {
        if (deadHang != null) deadHang.Release();
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void PlaceAtServePosition()
    {
        // Priority 1: spawn near the paddle's last known position (from QR tracking)
        // at serveHeight above the court. This puts the ball right where the player
        // is holding the racket, ready for an underhand serve.
        if (paddleController != null && gameSpaceRoot != null)
        {
            Vector3 paddleWorld = paddleController.transform.position;
            Vector3 paddleLocal = gameSpaceRoot.InverseTransformPoint(paddleWorld);
            // Keep paddle's lateral (X) and depth (Z), override height to serveHeight
            Vector3 serveLocal = new Vector3(paddleLocal.x, serveHeight, paddleLocal.z);
            transform.localPosition = serveLocal;
            transform.localRotation = Quaternion.identity;
            return;
        }

        // Priority 2: external servePoint (e.g. the AR camera)
        if (servePoint != null)
        {
            transform.position = servePoint.TransformPoint(new Vector3(0.18f, -0.12f, 0.85f));
            transform.rotation = Quaternion.identity;
            return;
        }

        // Priority 3: fixed position relative to GameSpaceRoot (court-local).
        if (gameSpaceRoot != null)
        {
            transform.localPosition = courtServeLocalPos;
            transform.localRotation = Quaternion.identity;
            return;
        }

        // Last resort: use the position baked by Awake.
        transform.localPosition = initialLocalPosition;
        transform.localRotation = Quaternion.identity;
    }

    private void NetFault()
    {
        mqttController?.PublishNetHit(transform.position);
        if (gameState != null && gameState.State == GameStateManager.RallyState.InPlay)
            gameState.OnBallHitNet();
    }

    private void OnTriggerEnter(Collider other)
    {
        var boundary = other.GetComponent<CourtBoundary>() ?? other.GetComponentInParent<CourtBoundary>();
        if (boundary != null && boundary.boundaryType == CourtBoundary.BoundaryType.Net)
            NetFault();
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
                        gameState.OnBallOutPlayerSide();
                        break;
                    case CourtBoundary.BoundaryType.BotBackWall:
                        gameState.OnBallOutBotSide();
                        break;
                    case CourtBoundary.BoundaryType.SideWall:
                        gameState.OnBallOutSideWall();
                        break;
                    case CourtBoundary.BoundaryType.Net:
                        // Net is a trigger — scoring handled in OnTriggerEnter.
                        return;
                }
            }
            else
            {
                ResetBall();
            }
            return;
        }

        // Legacy fallback: "Wall" tag without CourtBoundary
        if (collision.transform.CompareTag("Wall"))
        {
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
}
