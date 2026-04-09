using UnityEngine;

/// <summary>
/// Attach this to the Ball GameObject (the one with a Rigidbody + Collider).
///
/// WHY THIS EXISTS
/// ───────────────
/// The paddle is a kinematic Rigidbody that moves via MovePosition every physics
/// tick.  Unity only fires OnCollisionEnter on the DYNAMIC body when a kinematic
/// body moves into it – the kinematic body (paddle) itself gets no reliable callback.
/// By putting the detector on the ball (dynamic), we get Unity's authoritative
/// ContactPoint data (real surface normal, exact hit point) and forward it to the
/// paddle's impulse solver.
///
/// SETUP
/// ─────
/// 1. Drag this component onto the ball GameObject.
/// 2. Assign the PaddleHitController reference in the Inspector, OR leave it null
///    and it will search the scene at Start.
/// 3. Make sure the ball has a Rigidbody (non-kinematic) and at least one Collider.
/// 4. Make sure the paddle has at least one Collider (trigger OR solid – both work).
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class BallContactDetector : MonoBehaviour
{
    [Header("Paddle Reference")]
    [Tooltip("Drag the paddle GameObject (with PaddleHitController) here. " +
             "Leave null to auto-find at runtime.")]
    public PaddleHitController paddle;

    [Header("Continuous Overlap Fallback")]
    [Tooltip("Runs every FixedUpdate as a last resort in case Unity misses the " +
             "collision callback (e.g. very fast tunnelling or no Collider on paddle).")]
    public bool enableOverlapFallback = true;
    [Tooltip("Radius of the OverlapSphere centred on the ball. Should be at least " +
             "ball-radius + a small margin (e.g. 0.08 for a regulation pickleball).")]
    public float overlapRadius = 0.13f;

    [Header("Continuous Sweep Fallback")]
    [Tooltip("Casts a swept sphere from previous to current ball position each physics tick " +
             "to catch pass-through hits that never overlap at sampled frame positions.")]
    public bool enableSweepFallback = true;
    [Tooltip("Radius used for the swept-sphere fallback cast. Start near ball radius (e.g. 0.04).")]
    public float sweepRadius = 0.055f;
    [Tooltip("Extra cast distance margin added to the per-tick sweep to catch edge contacts.")]
    public float sweepExtraDistance = 0.06f;

    // ── private ──────────────────────────────────────────────────────────────────

    private Rigidbody ballRigidbody;
    private Vector3 previousBallCenter;
    private bool hasPreviousBallCenter;
    private float lastPaddleRefreshTime;

    // Colliders that belong to the paddle, cached to avoid per-frame GetComponent.
    private Collider[] paddleColliders;

    // ─────────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        ballRigidbody = GetComponent<Rigidbody>();

        if (ballRigidbody != null && !ballRigidbody.isKinematic)
        {
            if (ballRigidbody.collisionDetectionMode != CollisionDetectionMode.ContinuousDynamic)
                ballRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }
    }

    private void OnEnable()
    {
        if (ballRigidbody != null)
        {
            previousBallCenter = ballRigidbody.worldCenterOfMass;
            hasPreviousBallCenter = true;
        }
    }

    private void Start()
    {
        EnsurePaddleReference(force: true);
    }

    // ── Unity collision callbacks (most reliable path) ────────────────────────────

    private void OnCollisionEnter(Collision collision)
    {
        HandleCollision(collision);
    }

    private void OnCollisionStay(Collision collision)
    {
        HandleCollision(collision);
    }

    private void HandleCollision(Collision collision)
    {
        if (paddle == null || collision.contactCount == 0)
        {
            return;
        }

        // Depending on callback context, the paddle may appear as either contact
        // collider in ContactPoint data. Scan all contacts and accept the first
        // one that matches paddle colliders.
        bool paddleMatched = false;
        ContactPoint contact = collision.GetContact(0);
        for (int index = 0; index < collision.contactCount; index++)
        {
            ContactPoint candidate = collision.GetContact(index);
            Collider thisCollider = candidate.thisCollider;
            Collider otherCollider = candidate.otherCollider;

            if (IsPaddleCollider(thisCollider)
                || IsPaddleCollider(otherCollider)
                || IsEmergencyPaddleMatch(thisCollider)
                || IsEmergencyPaddleMatch(otherCollider))
            {
                contact = candidate;
                paddleMatched = true;
                break;
            }
        }
        if (!paddleMatched)
        {
            return;
        }

        // contact.normal points FROM the other body (paddle) INTO this body (ball).
        // This is exactly the outward surface normal we need for the impulse solver.
        paddle.ApplyHitImpulse(ballRigidbody, contact.point, contact.normal, paddle.playerHitVelocityMultiplier);
    }

    // ── FixedUpdate OverlapSphere fallback ────────────────────────────────────────

    private void FixedUpdate()
    {
        if (ballRigidbody == null)
        {
            return;
        }

        EnsurePaddleReference();
        if (paddle == null)
            return;

        Vector3 currentBallCenter = ballRigidbody.worldCenterOfMass;

        if (enableSweepFallback)
        {
            TrySweepFallback(previousBallCenter, currentBallCenter);
        }

        if (enableOverlapFallback)
        {
            TryOverlapFallback(currentBallCenter);
        }

        previousBallCenter = currentBallCenter;
        hasPreviousBallCenter = true;
    }

    private void EnsurePaddleReference(bool force = false)
    {
        if (!force && Time.time - lastPaddleRefreshTime < 0.5f)
            return;

        lastPaddleRefreshTime = Time.time;

        if (paddle == null || !paddle.gameObject.scene.isLoaded)
            paddle = FindFirstObjectByType<PaddleHitController>();

        if (paddle == null)
            return;

        paddleColliders = paddle.GetComponentsInChildren<Collider>(includeInactive: true);

        if (force)
        {
            if (paddleColliders.Length == 0)
            {
                // No colliders found on the paddle or its children.
                // The overlap fallback will still run but will match ANY nearby
                // non-ball Rigidbody as a last resort.
                Debug.LogWarning(
                    "[BallContactDetector] PaddleHitController found but it has NO Colliders " +
                    "on itself or its children. Add a CapsuleCollider or MeshCollider to the " +
                    "paddle (Racket_Pickelball1) in the Inspector.", this);
            }
            else
            {
                string names = "";
                for (int i = 0; i < paddleColliders.Length; i++)
                {
                    names += paddleColliders[i].gameObject.name;
                    if (i < paddleColliders.Length - 1) names += ", ";
                }
                Debug.Log($"[BallContactDetector] Registered {paddleColliders.Length} paddle " +
                          $"collider(s): {names}", this);
            }
        }
    }

    private void TrySweepFallback(Vector3 fromCenter, Vector3 toCenter)
    {
        if (!hasPreviousBallCenter)
            return;

        Vector3 displacement = toCenter - fromCenter;
        float distance = displacement.magnitude;
        if (distance <= 0.0001f)
            return;

        Vector3 direction = displacement / distance;
        float radius = Mathf.Max(0.005f, sweepRadius);
        float castDistance = distance + Mathf.Max(0f, sweepExtraDistance);

        RaycastHit[] hits = Physics.SphereCastAll(
            fromCenter,
            radius,
            direction,
            castDistance,
            Physics.AllLayers,
            QueryTriggerInteraction.Collide);

        for (int i = 0; i < hits.Length; i++)
        {
            Collider hitCollider = hits[i].collider;
            if (hitCollider == null)
                continue;
            if (hitCollider.attachedRigidbody == ballRigidbody)
                continue;

            bool isPaddle = IsPaddleCollider(hitCollider) || IsEmergencyPaddleMatch(hitCollider);
            if (!isPaddle)
                continue;

            Vector3 contactPoint = hits[i].point;
            if (contactPoint == Vector3.zero)
                contactPoint = hitCollider.ClosestPoint(toCenter);

            Vector3 surfaceNormal = hits[i].normal.sqrMagnitude > 0.0001f
                ? hits[i].normal.normalized
                : (toCenter - contactPoint).normalized;

            paddle.ApplyHitImpulse(ballRigidbody, contactPoint, surfaceNormal, paddle.playerHitVelocityMultiplier);
            return;
        }
    }

    private void TryOverlapFallback(Vector3 ballCenter)
    {
        // Broad-phase: any collider within overlapRadius of the ball centre?
        Collider[] hits = Physics.OverlapSphere(
            ballCenter,
            overlapRadius,
            Physics.AllLayers,
            QueryTriggerInteraction.Collide);

        for (int i = 0; i < hits.Length; i++)
        {
            // Skip ourselves.
            if (hits[i].attachedRigidbody == ballRigidbody)
            {
                continue;
            }

            bool isPaddle = IsPaddleCollider(hits[i]) || IsEmergencyPaddleMatch(hits[i]);

            if (!isPaddle)
            {
                continue;
            }

            // Compute the contact point as the point on the paddle collider closest
            // to the ball centre of mass.
            // ClosestPoint only supports Box/Sphere/Capsule and convex MeshColliders;
            // fall back to bounds for non-convex mesh colliders.
            MeshCollider mc = hits[i] as MeshCollider;
            Vector3 contactPoint = (mc != null && !mc.convex)
                ? hits[i].bounds.ClosestPoint(ballCenter)
                : hits[i].ClosestPoint(ballCenter);

            // Build the surface normal pointing from paddle surface → ball COM.
            Vector3 toball = ballCenter - contactPoint;
            Vector3 surfaceNormal = toball.sqrMagnitude > 0.0001f
                ? toball.normalized
                : -paddle.transform.forward; // degenerate fallback

            paddle.ApplyHitImpulse(ballRigidbody, contactPoint, surfaceNormal, paddle.playerHitVelocityMultiplier);
            return; // one hit per tick is enough
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private bool IsPaddleCollider(Collider col)
    {
        if (paddleColliders == null)
        {
            return false;
        }

        for (int i = 0; i < paddleColliders.Length; i++)
        {
            if (paddleColliders[i] == col)
            {
                return true;
            }
        }

        return false;
    }

    private bool IsEmergencyPaddleMatch(Collider col)
    {
        if (col == null || paddle == null)
            return false;

        if (!(paddleColliders == null || paddleColliders.Length == 0))
            return false;

        string n = col.gameObject.name.ToLower();
        Transform p = col.transform.parent;
        string pn = p != null ? p.gameObject.name.ToLower() : "";
        return n.Contains("paddle") || n.Contains("racket")
            || pn.Contains("paddle") || pn.Contains("racket")
            || col.transform.IsChildOf(paddle.transform)
            || paddle.transform.IsChildOf(col.transform.root);
    }
}
