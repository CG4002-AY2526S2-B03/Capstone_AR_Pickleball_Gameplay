using UnityEngine;

public class PracticeBallController : MonoBehaviour
{
    [Header("References")]
    public Transform servePoint;

    [Header("Serve Position (local to GameSpaceRoot)")]
    [Tooltip("Where the ball spawns relative to GameSpaceRoot. " +
             "Ignored when servePoint is set to an external Transform.")]
    public Vector3 courtServeLocalPos = new Vector3(0.44f, 0.25f, 2.0f);

    [Header("Ground Safety")]
    [Tooltip("Automatically creates an invisible floor collider at Y=0 " +
             "inside GameSpaceRoot so the ball cannot fall through the court.")]
    public bool createGroundPlane = true;

    [Header("Controls")]
    public KeyCode resetKey = KeyCode.R;

    private Rigidbody ballRigidbody;
    private Vector3 initialLocalPosition;
    private Transform gameSpaceRoot;

    private void Awake()
    {
        ballRigidbody = GetComponent<Rigidbody>();

        // Walk up the hierarchy to find the GameSpaceRoot parent.
        // Ball2 is a direct child of GameSpaceRoot.
        gameSpaceRoot = transform.parent;

        // Remember the ball's original local position (set in the prefab / scene).
        initialLocalPosition = transform.localPosition;

        // ── Gravity OFF until the ball is actually hit ───────────────────────
        // Prevents the ball from falling through the court before gameplay starts.
        if (ballRigidbody != null)
        {
            ballRigidbody.useGravity = false;
            ballRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }
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
    /// Resets the ball: stops motion, disables gravity, and places it at the
    /// serve position so the player can see it and swing again.
    /// </summary>
    public void ResetBall()
    {
        if (ballRigidbody != null)
        {
            ballRigidbody.velocity = Vector3.zero;
            ballRigidbody.angularVelocity = Vector3.zero;
            ballRigidbody.useGravity = false;
        }

        PlaceAtServePosition();
    }

    /// <summary>
    /// Called by PaddleHitController (or BotHitController) after the first
    /// impulse is applied.  Turns on gravity so the ball follows a real arc.
    /// </summary>
    public void EnableGravity()
    {
        if (ballRigidbody != null)
            ballRigidbody.useGravity = true;
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
    /// Resets the ball when it hits an out-of-bounds wall.
    /// Tag your wall objects as "Wall" for this to work.
    /// </summary>
    private void OnCollisionEnter(Collision collision)
    {
        if (collision.transform.CompareTag("Wall"))
        {
            ResetBall();
        }
    }
}
