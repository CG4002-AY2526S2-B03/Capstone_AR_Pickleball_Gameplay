using UnityEngine;

/// <summary>
/// Drives the bot's movement and ball-hitting behaviour.
///
/// Supports two modes:
///   1. ML mode (useMLPredictions = true): receives position, velocity, and swing type
///      from the ML model via SetMLPrediction(). The bot moves to the predicted position
///      and applies the predicted velocity directly to the ball.
///   2. Random mode (fallback): tracks ball laterally and picks random shots.
///
/// Setup (Inspector):
///   - Ball          -> drag Ball2 here
///   - Targets       -> 3 empty GameObjects on the player's side (left/center/right)
///   - Move Speed    -> lateral tracking speed (start with 2)
///   - The bot must also have a BoxCollider (isTrigger = true) for hit detection.
///   - An Animator with player.controller assigned for forehand/backhand anims.
/// </summary>
[RequireComponent(typeof(BotShotProfile))]
public class BotHitController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The ball Transform (Ball2 in GameSpaceRoot).")]
    public Transform ball;

    [Tooltip("Target positions on the player's side of the court the bot aims at.")]
    public Transform[] targets;

    [Header("Movement")]
    [Tooltip("How fast the bot slides laterally to track the ball.")]
    public float moveSpeed = 2f;

    [Tooltip("When true the bot also tracks the ball on the Z (forward/back) axis " +
             "within the allowed range.")]
    public bool trackZAxis = false;

    [Tooltip("Clamp Z movement to this range relative to its start position.")]
    public float zTrackRange = 0.3f;

    [Header("Hit Tuning")]
    [Tooltip("Minimum time between consecutive hits (seconds).")]
    public float hitCooldown = 0.25f;

    [Header("ML Integration")]
    [Tooltip("When true, the bot uses ML predictions from /opponentBall instead of random shots.")]
    public bool useMLPredictions = true;

    // ── cached components ────────────────────────────────────────────────────────
    private BotShotProfile shotProfile;
    private Animator animator;
    private Vector3 startPosition;
    private float lastHitTime = -10f;

    // ── ML prediction state ─────────────────────────────────────────────────────
    private Vector3 pendingHitPosition;
    private Vector3 pendingHitVelocity;
    private int pendingSwingType;
    private bool hasPendingMLShot;

    // ─────────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        shotProfile = GetComponent<BotShotProfile>();
        animator = GetComponent<Animator>();
        startPosition = transform.localPosition;
    }

    /// <summary>
    /// Called by MqttController when an /opponentBall message arrives.
    /// Stores the ML prediction for use when the ball reaches the bot.
    /// </summary>
    public void SetMLPrediction(Vector3 position, Vector3 velocity, int swingType)
    {
        pendingHitPosition = position;
        pendingHitVelocity = velocity;
        pendingSwingType = swingType;
        hasPendingMLShot = true;

        Debug.Log($"[Bot] ML prediction received: pos={position}, vel={velocity}, swing={swingType}");
    }

    private void Update()
    {
        if (ball == null) return;
        TrackBall();
    }

    // ── Movement ─────────────────────────────────────────────────────────────────

    private void TrackBall()
    {
        // Work in the parent's local space so court placement / rotation don't matter.
        Vector3 targetLocal = transform.localPosition;

        if (useMLPredictions && hasPendingMLShot)
        {
            // Move toward the ML-predicted hit position
            Vector3 localPredicted = transform.parent != null
                ? transform.parent.InverseTransformPoint(pendingHitPosition)
                : pendingHitPosition;

            targetLocal.x = localPredicted.x;

            if (trackZAxis)
            {
                float clampedZ = Mathf.Clamp(localPredicted.z,
                    startPosition.z - zTrackRange, startPosition.z + zTrackRange);
                targetLocal.z = clampedZ;
            }
        }
        else
        {
            // Fallback: track ball's current position
            Vector3 localBallPos = transform.parent != null
                ? transform.parent.InverseTransformPoint(ball.position)
                : ball.position;

            targetLocal.x = localBallPos.x;

            if (trackZAxis)
            {
                float clampedZ = Mathf.Clamp(localBallPos.z,
                    startPosition.z - zTrackRange, startPosition.z + zTrackRange);
                targetLocal.z = clampedZ;
            }
        }

        transform.localPosition = Vector3.MoveTowards(
            transform.localPosition,
            targetLocal,
            moveSpeed * Time.deltaTime);
    }

    // ── Hit Detection ────────────────────────────────────────────────────────────

    private void OnTriggerEnter(Collider other)
    {
        TryHit(other);
    }

    // OnTriggerStay as a safety net in case the ball lingers inside the trigger.
    private void OnTriggerStay(Collider other)
    {
        TryHit(other);
    }

    private void TryHit(Collider other)
    {
        if (ball == null) return;

        // Only react to the ball.
        Rigidbody ballRb = other.attachedRigidbody;
        if (ballRb == null) return;
        if (other.transform != ball && ballRb.transform != ball) return;

        // Cooldown guard.
        if (Time.time - lastHitTime < hitCooldown) return;
        lastHitTime = Time.time;

        if (useMLPredictions && hasPendingMLShot)
        {
            // ML mode: apply predicted velocity directly
            ballRb.velocity = pendingHitVelocity;
            hasPendingMLShot = false;

            PlayHitAnimationForSwingType(pendingSwingType);
        }
        else
        {
            // Random fallback mode
            BotShotProfile.ShotConfig shot = PickShot();
            Vector3 targetPos = PickTarget();
            Vector3 dir = (targetPos - transform.position).normalized;
            ballRb.velocity = dir * shot.hitForce + Vector3.up * shot.upForce;

            PlayHitAnimation();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private Vector3 PickTarget()
    {
        if (targets == null || targets.Length == 0)
        {
            // Fallback: aim straight ahead from the bot.
            return transform.position + transform.forward * 2f;
        }

        int index = Random.Range(0, targets.Length);
        return targets[index].position;
    }

    private BotShotProfile.ShotConfig PickShot()
    {
        if (shotProfile == null)
        {
            // Sensible fallback so the game still works if the profile is missing.
            return new BotShotProfile.ShotConfig { upForce = 4f, hitForce = 15f };
        }

        return Random.value < 0.5f ? shotProfile.topSpin : shotProfile.flat;
    }

    private void PlayHitAnimation()
    {
        if (animator == null || ball == null) return;

        Vector3 ballDir = ball.position - transform.position;
        // Use local X to determine forehand vs backhand relative to the bot's facing.
        float localX = transform.InverseTransformDirection(ballDir).x;

        if (localX >= 0f)
            animator.Play("forehand");
        else
            animator.Play("backhand");
    }

    private void PlayHitAnimationForSwingType(int swingType)
    {
        if (animator == null) return;

        // Map swing types to available animations.
        // Drive/Attack use forehand/backhand based on ball position.
        // Dink and Lob default to forehand (can be expanded with dedicated clips).
        switch (swingType)
        {
            case 2: // Dink
                animator.Play("forehand");
                break;
            case 3: // Lob
                animator.Play("forehand");
                break;
            default: // Drive (0), Attack (1)
                PlayHitAnimation();
                break;
        }
    }
}
