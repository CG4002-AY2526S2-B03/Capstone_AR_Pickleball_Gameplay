using UnityEngine;

public class PaddleHitController : MonoBehaviour
{
    [Header("References")]
    public Transform cameraTransform;

    [Header("QR-Tracked Racket (AR Mode)")]
    [Tooltip("When set, the physics paddle teleports to this transform every FixedUpdate " +
             "instead of using camera-relative positioning. Assign this at runtime " +
             "when the QR-spawned racket is detected.")]
    public Transform qrTrackedRacket;

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

    [Header("Fallback Detection")]
    public Rigidbody trackedBall;
    public bool enableProximityFallback = true;
    public float proximityHitDistance = 0.12f;

    private Rigidbody paddleRigidbody;
    private Collider[] paddleColliders;
    private Vector3 previousPosition;
    private Vector3 paddleVelocity;
    private Vector3 paddleAngularVelocity;
    private float lastHitTime;

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

        if (lockCursorOnPlay)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        paddleColliders = GetComponentsInChildren<Collider>();

        previousPosition = transform.position;
    }

    private void FixedUpdate()
    {
        // ── QR-tracked mode: follow the physical racket card ──────────────────────
        if (qrTrackedRacket != null && qrTrackedRacket.gameObject.activeInHierarchy)
        {
            Vector3 qrPos = qrTrackedRacket.position;
            Quaternion qrRot = qrTrackedRacket.rotation;

            paddleVelocity = (qrPos - previousPosition) / Mathf.Max(Time.fixedDeltaTime, 0.0001f);

            Quaternion prevRot = paddleRigidbody != null
                ? paddleRigidbody.rotation
                : transform.rotation;
            Quaternion dRot = qrRot * Quaternion.Inverse(prevRot);
            dRot.ToAngleAxis(out float dAngle, out Vector3 dAxis);
            if (dAngle > 180f) { dAngle -= 360f; }
            paddleAngularVelocity = dAxis * (dAngle * Mathf.Deg2Rad / Mathf.Max(Time.fixedDeltaTime, 0.0001f));

            if (paddleRigidbody != null)
            {
                paddleRigidbody.MovePosition(qrPos);
                paddleRigidbody.MoveRotation(qrRot);
            }
            else
            {
                transform.SetPositionAndRotation(qrPos, qrRot);
            }

            previousPosition = qrPos;

            if (enableProximityFallback)
            {
                TryProximityHit();
            }
            return;
        }

        // ── Fallback: camera-relative mode (editor / device without QR) ──────────
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

    private void TryProximityHit()
    {
        Rigidbody candidateBall = trackedBall;

        if (candidateBall == null)
        {
            if (!string.IsNullOrWhiteSpace(ballTag))
            {
                try
                {
                    GameObject ballObject = GameObject.FindWithTag(ballTag);
                    if (ballObject != null)
                    {
                        candidateBall = ballObject.GetComponent<Rigidbody>();
                    }
                }
                catch (UnityException)
                {
                }
            }

            if (candidateBall == null)
            {
                Rigidbody[] rigidbodies = FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
                for (int index = 0; index < rigidbodies.Length; index++)
                {
                    Rigidbody body = rigidbodies[index];
                    if (body == null || body == paddleRigidbody)
                    {
                        continue;
                    }

                    if (body.gameObject.name.IndexOf("ball", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        candidateBall = body;
                        break;
                    }
                }
            }
        }

        if (candidateBall == null)
        {
            return;
        }

        Vector3 ballPosition = candidateBall.worldCenterOfMass;
        Vector3 closestPointOnPaddle = GetClosestPointOnPaddle(ballPosition);
        float distance = Vector3.Distance(closestPointOnPaddle, ballPosition);

        if (distance <= proximityHitDistance)
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
        HandlePaddleTrigger(other);
    }

    private void OnTriggerStay(Collider other)
    {
        HandlePaddleTrigger(other);
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
    public void ApplyHitImpulse(Rigidbody ballBody, Vector3 contactPoint, Vector3 surfaceNormal)
    {
        if (ballBody == null)
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
        Vector3 paddleContactVelocity =
            paddleVelocity + Vector3.Cross(paddleAngularVelocity, contactPoint - paddleCOM);

        // ── Relative velocity of the ball w.r.t. the paddle surface ──────────────
        Vector3 relativeVelocity = ballBody.velocity - paddleContactVelocity;
        float vN = Vector3.Dot(relativeVelocity, faceNormal);

        // Guard: only apply an impulse when the paddle is moving INTO the ball
        // (vN < 0) OR when the paddle is actively approaching (paddleVelocity toward
        // ball has sufficient magnitude).  This prevents phantom impulses when the
        // paddle is withdrawing after a hit but the cooldown hasn't expired.
        //
        // We use a small positive tolerance (0.05 m/s) to accept near-zero relative
        // velocity contacts, e.g. a stationary paddle resting against the ball.
        if (vN > 0.05f)
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
        Vector3 newVelocity = ballBody.velocity + normalDeltaV + tangentialDeltaV;

        if (newVelocity.magnitude > maxBallSpeed)
        {
            newVelocity = newVelocity.normalized * maxBallSpeed;
        }

        // ForceMode.VelocityChange applies Δv directly, independent of ball mass.
        ballBody.AddForce(newVelocity - ballBody.velocity, ForceMode.VelocityChange);
        // ── Enable gravity on the ball after the first hit ─────────────────────────
        if (!ballBody.useGravity)
        {
            ballBody.useGravity = true;
        }
        // Also notify PracticeBallController so it knows gravity is active.
        var ballCtrl = ballBody.GetComponent<PracticeBallController>();
        if (ballCtrl != null)
        {
            ballCtrl.EnableGravity();
        }
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

        Debug.Log($"[Hit] vN={vN:F2}  paddleSpeed={paddleVelocity.magnitude:F2} m/s" +
                  $"  paddleContactVel={paddleContactVelocity.magnitude:F2} m/s" +
                  $"  ball-out={newVelocity.magnitude:F1} m/s" +
                  $"  normalDeltaV={normalDeltaV.magnitude:F2}");
    }
}
