using UnityEngine;

public class PracticeBallController : MonoBehaviour
{
    [Header("References")]
    public Transform servePoint;

    [Header("Serve Position (local to GameSpaceRoot)")]
    [Tooltip("Where the ball spawns relative to GameSpaceRoot. " +
             "Y = height above court (1m ≈ waist height for underhand serve). " +
             "Ignored when servePoint is set to an external Transform.")]
    public Vector3 courtServeLocalPos = new Vector3(0.44f, 1.0f, 2.0f);

    [Header("Ground Safety")]
    [Tooltip("Automatically creates an invisible floor collider at Y=0 " +
             "inside GameSpaceRoot so the ball cannot fall through the court.")]
    public bool createGroundPlane = true;

    [Header("Game State")]
    [Tooltip("When set, boundary collisions trigger scoring instead of raw resets.")]
    public GameStateManager gameState;

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
        // Create an invisible floor so the ball always bounces on the court surface.
        if (createGroundPlane && gameSpaceRoot != null)
        {
            EnsureGroundCollider();
        }

        // Position the ball at the court serve point so it's visible on the court.
        PlaceAtServePosition();
    }

    private void Update()
    {
        if (Input.GetKeyDown(resetKey))
        {
            ResetBall();
        }
    }

    /// <summary>
    /// Resets the ball: freezes it in mid-air at the serve position.
    /// It will stay there until the paddle hits it.
    /// Safe to call from physics callbacks (OnCollisionEnter, etc.).
    /// </summary>
    public void ResetBall()
    {
        bounceCount = 0;
        PlaceAtServePosition();
        // Sync the Rigidbody position so it matches the Transform before freezing.
        if (ballRigidbody != null)
            ballRigidbody.position = transform.position;
        if (deadHang != null) deadHang.Freeze();
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
        // If the user wired an external servePoint (e.g. the AR camera),
        // use it — the ball appears in front of the player.
        if (servePoint != null)
        {
            transform.position = servePoint.TransformPoint(new Vector3(0.18f, -0.12f, 0.85f));
            transform.rotation = Quaternion.identity;
            return;
        }

        // Otherwise, place relative to GameSpaceRoot (court-local).
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
                        gameState.OnBallHitNet();
                        break;
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
