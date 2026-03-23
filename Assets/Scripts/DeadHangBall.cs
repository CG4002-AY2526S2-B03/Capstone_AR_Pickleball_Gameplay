using UnityEngine;

/// <summary>
/// Keeps the ball completely frozen ("dead hang") in mid-air until the first
/// collision with the paddle is detected, then releases it to full physics.
///
/// Attach this to the Ball GameObject (same object that has Rigidbody +
/// PracticeBallController + BallContactDetector).
///
/// HOW IT WORKS
/// ────────────
/// Uses RigidbodyConstraints.FreezeAll to lock the ball in place.
/// The ball stays a normal dynamic Rigidbody the whole time — no kinematic
/// toggling, no coroutines, no deferred callbacks.  This is safe to call
/// from any context (physics callbacks, Update, button clicks).
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class DeadHangBall : MonoBehaviour
{
    [Header("Paddle Detection")]
    [Tooltip("Auto-found at Start if left null.")]
    public PaddleHitController paddle;

    [Tooltip("Radius around the ball centre used to detect paddle overlap while frozen.")]
    public float detectionRadius = 0.12f;

    /// <summary>True while the ball is frozen in mid-air.</summary>
    public bool IsFrozen { get; private set; }

    private Rigidbody rb;
    private Collider[] paddleColliders;

    // ─────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        Freeze();
    }

    private void Start()
    {
        if (paddle == null)
            paddle = FindFirstObjectByType<PaddleHitController>();

        CachePaddleColliders();
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Freezes the ball in place.  Can be called from anywhere — physics
    /// callbacks, Update, button clicks.  No deferred work needed.
    /// </summary>
    public void Freeze()
    {
        if (rb != null)
        {
            // Unlock first so velocity/position writes are not blocked by stale constraints.
            rb.constraints         = RigidbodyConstraints.None;
            rb.linearVelocity      = Vector3.zero;
            rb.angularVelocity     = Vector3.zero;
            rb.useGravity          = false;
            rb.constraints         = RigidbodyConstraints.FreezeAll;
        }
        IsFrozen = true;
    }

    /// <summary>
    /// Releases the ball to full physics (gravity + unconstrained motion).
    /// </summary>
    public void Release()
    {
        if (rb != null && IsFrozen)
        {
            rb.constraints = RigidbodyConstraints.None;
            rb.useGravity  = true;
        }
        IsFrozen = false;
        Debug.Log("[DeadHangBall] Ball released to physics.");
    }

    // ─────────────────────────────────────────────────────────────────────────

    private void FixedUpdate()
    {
        if (!IsFrozen) return;

        // Re-cache if paddle was wired late (e.g. QR-spawned racket).
        if (paddleColliders == null || paddleColliders.Length == 0)
            CachePaddleColliders();

        Collider[] hits = Physics.OverlapSphere(
            rb.worldCenterOfMass,
            detectionRadius,
            Physics.AllLayers,
            QueryTriggerInteraction.Collide);

        for (int i = 0; i < hits.Length; i++)
        {
            if (hits[i].attachedRigidbody == rb) continue;

            if (IsPaddleCollider(hits[i]))
            {
                Release();
                return;
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void CachePaddleColliders()
    {
        if (paddle != null)
            paddleColliders = paddle.GetComponentsInChildren<Collider>(includeInactive: true);
    }

    private bool IsPaddleCollider(Collider col)
    {
        if (paddleColliders != null)
        {
            for (int i = 0; i < paddleColliders.Length; i++)
            {
                if (paddleColliders[i] == col)
                    return true;
            }
        }

        // Fallback: name-based check for dynamically spawned paddles.
        string n  = col.gameObject.name.ToLower();
        Transform p = col.transform.parent;
        string pn = p != null ? p.gameObject.name.ToLower() : "";
        return n.Contains("paddle") || n.Contains("racket") ||
               pn.Contains("paddle") || pn.Contains("racket");
    }
}
