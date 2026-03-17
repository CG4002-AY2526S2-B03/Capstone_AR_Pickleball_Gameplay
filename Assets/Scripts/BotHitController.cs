using UnityEngine;

/// <summary>
/// Drives the bot's movement and ball-hitting behaviour.
///
/// This replaces the keyboard-oriented Physics/Bot.cs for the AR game.
/// It reuses the BotTargeting (look-at-ball) and BotShotProfile (shot configs)
/// components already attached to the bot prefab inside GameSpaceRoot.
///
/// Setup (Inspector):
///   • Ball          → drag Ball2 here
///   • Targets       → 3 empty GameObjects on the player's side (left/center/right)
///   • Move Speed    → lateral tracking speed (start with 2)
///   • The bot must also have a BoxCollider (isTrigger = true) for hit detection.
///   • An Animator with player.controller assigned for forehand/backhand anims.
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

    // ── cached components ────────────────────────────────────────────────────────
    private BotShotProfile shotProfile;
    private Animator animator;
    private Vector3 startPosition;
    private float lastHitTime = -10f;

    // ─────────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        shotProfile = GetComponent<BotShotProfile>();
        animator = GetComponent<Animator>();
        startPosition = transform.localPosition;
    }

    private void Update()
    {
        if (ball == null) return;
        TrackBall();
    }

    // ── Movement ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Moves the bot laterally (X axis in local space) to stay aligned with the
    /// ball, mimicking the Physics/Bot.cs behaviour but using local coordinates
    /// so it works correctly inside a scaled/rotated GameSpaceRoot.
    /// </summary>
    private void TrackBall()
    {
        // Work in the parent's local space so court placement / rotation don't matter.
        Vector3 localBallPos = transform.parent != null
            ? transform.parent.InverseTransformPoint(ball.position)
            : ball.position;

        Vector3 targetLocal = transform.localPosition;
        targetLocal.x = localBallPos.x;

        if (trackZAxis)
        {
            float clampedZ = Mathf.Clamp(localBallPos.z, startPosition.z - zTrackRange, startPosition.z + zTrackRange);
            targetLocal.z = clampedZ;
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

        // Pick a random shot profile (top-spin or flat).
        BotShotProfile.ShotConfig shot = PickShot();

        // Pick a random target on the player's court side.
        Vector3 targetPos = PickTarget();

        // Direction from bot → target, normalised.
        Vector3 dir = (targetPos - transform.position).normalized;

        // Apply force, same formula as the original Physics/Bot.cs.
        ballRb.velocity = dir * shot.hitForce + Vector3.up * shot.upForce;

        // Play forehand / backhand animation based on ball side.
        PlayHitAnimation();
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
}
