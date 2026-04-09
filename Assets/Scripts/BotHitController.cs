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
    [Tooltip("Optional ball tag used when the bot needs to reacquire the live ball.")]
    public string ballTag = "Ball";

    [Tooltip("Target positions on the player's side of the court the bot aims at.")]
    public Transform[] targets;

    [Header("Movement")]
    [Tooltip("How fast the bot slides laterally to track the ball.")]
    public float moveSpeed = 2f;

    [Tooltip("When true the bot also tracks the ball on the Z (forward/back) axis " +
             "within the allowed range.")]
    public bool trackZAxis = true;

    [Tooltip("Clamp Z movement to this range relative to its start position.")]
    public float zTrackRange = 3.0f;

    [Header("Court Bounds (local space)")]
    [Tooltip("Minimum local X the bot can move to (left side wall).")]
    public float courtMinX = -10.8f;
    [Tooltip("Maximum local X the bot can move to (right side wall).")]
    public float courtMaxX = 2.1f;
    [Tooltip("Minimum local Z the bot can move to (net side).")]
    public float courtMinZ = 5.4f;
    [Tooltip("Maximum local Z the bot can move to (bot back wall).")]
    public float courtMaxZ = 12.2f;

    [Header("Hit Tuning")]
    [Tooltip("Minimum time between consecutive hits (seconds).")]
    public float hitCooldown = 0.25f;

    [Header("ML Integration")]
    [Tooltip("When true, the bot uses ML predictions from /opponentBall instead of random shots.")]
    public bool useMLPredictions = true;

    [Header("Game State")]
    [Tooltip("When set, reports bot hits for scoring.")]
    public GameStateManager gameState;

    [Header("MQTT")]
    [Tooltip("When set, publishes bot return-hit events to MQTT topics.")]
    public MqttController mqttController;

    [Header("God Mode")]
    [Tooltip("Ball speed multiplier for opponent→player returns in God Mode.")]
    public float godModeSpeedMultiplier = 0.5f;

    [Header("Court Side")]
    [Tooltip("Enable when the bot is placed on the RIGHT side of the court (player POV: bot is on the left). " +
             "Mirrors the bot's visual mesh and inverts racquet offset X so ML movement stays correct.")]
    public bool mirrorXAxis = false;

    // ── cached components ────────────────────────────────────────────────────────
    private BotShotProfile shotProfile;
    private Animator animator;
    private Vector3 startPosition;
    private float lastHitTime = -10f;
    private float lastBallLookupTime = -10f;
    private float lastNullBallLogTime = -10f;
    private bool loggedBallTagLookupFailure;

    // ── ML prediction state ─────────────────────────────────────────────────────
    private Vector3 pendingBallPosition;   // world-space position where ball will be
    private int pendingSwingType;
    private bool hasPendingMLShot;

    // ─────────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        shotProfile = GetComponent<BotShotProfile>();
        animator = GetComponent<Animator>();
        if (mqttController == null)
            mqttController = FindFirstObjectByType<MqttController>();
        startPosition = transform.localPosition;
        TryResolveBall(force: true);

        // Mirror the visual mesh for right-side placement
        if (mirrorXAxis)
        {
            // Flip the first child that has a renderer (the visible model)
            foreach (Transform child in transform)
            {
                if (child.GetComponentInChildren<Renderer>() != null)
                {
                    Vector3 s = child.localScale;
                    child.localScale = new Vector3(-s.x, s.y, s.z);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Called by BotTestDriver to directly test the swing animation without ball collision.
    /// </summary>
    public void TestSwingAnimation()
    {
        PlayHitAnimationForSwingType(pendingSwingType);
        Debug.Log($"[Bot] TestSwing: playing animation for swingType={pendingSwingType} ({(ShotType)pendingSwingType})");
    }

    /// <summary>
    /// Called by MqttController when an /opponentBall message arrives.
    /// Stores the ML prediction for use when the ball reaches the bot.
    /// </summary>
    public void SetMLPrediction(Vector3 position, Vector3 velocity, int swingType)
    {
        pendingBallPosition = position;
        pendingSwingType = swingType;
        hasPendingMLShot = true;

        Debug.Log($"[Bot] ML prediction received: ballPos={position}, swing={swingType} ({(ShotType)swingType})");
    }

    private float _debugLogTimer = 0f;

    [Header("Facing Override")]
    [Tooltip("Local Y rotation applied after Animator each frame.")]
    public float facingYAngle = 90f;

    private void Update()
    {
        if (!TryResolveBall())
        {
            if (Time.unscaledTime - lastNullBallLogTime > 1f)
            {
                lastNullBallLogTime = Time.unscaledTime;
                Debug.LogWarning("[Bot] ball is null — bot cannot move");
            }
            return;
        }

        TrackBall();
    }

    private void LateUpdate()
    {
        // Force facing after Animator overrides rotation
        transform.localRotation = Quaternion.Euler(0f, facingYAngle, 0f);
    }

    // ── Movement ─────────────────────────────────────────────────────────────────

    private void TrackBall()
    {
        // Work in the parent's local space so court placement / rotation don't matter.
        Vector3 targetLocal = transform.localPosition;

        if (useMLPredictions && hasPendingMLShot)
        {
            // AI gives us where the ball will be; offset so the racquet (not body centre) meets it
            BotShotProfile.ShotConfig shot = shotProfile.GetShotByType(pendingSwingType);
            Vector3 localBallPredicted = transform.parent != null
                ? transform.parent.InverseTransformPoint(pendingBallPosition)
                : pendingBallPosition;

            // Bot body position = ball position minus the racquet offset for this shot type.
            // Mirror the X offset when bot is on the right side of the court.
            Vector3 racquetOffset = shot.racquetOffset;
            if (mirrorXAxis) racquetOffset.x = -racquetOffset.x;
            Vector3 botTarget = localBallPredicted - racquetOffset;

            targetLocal.x = botTarget.x;

            if (trackZAxis)
            {
                float clampedZ = Mathf.Clamp(botTarget.z,
                    startPosition.z - zTrackRange, startPosition.z + zTrackRange);
                targetLocal.z = clampedZ;
            }

            _debugLogTimer -= Time.deltaTime;
            if (_debugLogTimer <= 0f)
            {
                _debugLogTimer = 1f;
                Debug.Log($"[Bot Move] current={transform.localPosition} target={targetLocal} ballPos={localBallPredicted} offset={shot.racquetOffset}");
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

        // Clamp to court bounds so the bot never leaves the play area.
        targetLocal.x = Mathf.Clamp(targetLocal.x, courtMinX, courtMaxX);
        targetLocal.z = Mathf.Clamp(targetLocal.z, courtMinZ, courtMaxZ);

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
        if (!TryResolveBall()) return;

        if (gameState != null)
        {
            if (!gameState.IsStarted) return;

            if (gameState.State == GameStateManager.RallyState.MatchOver
                || gameState.State == GameStateManager.RallyState.PointScored)
            {
                return;
            }

            // Recovery path: if player hit detection was missed, the rally can get stuck
            // in WaitingToServe even though the ball reaches the bot. Promote to InPlay.
            if (gameState.State == GameStateManager.RallyState.WaitingToServe)
            {
                gameState.RegisterPlayerHit(ShotType.Drive);
                Debug.LogWarning("[Bot] Auto-promoted WaitingToServe -> InPlay (missed player hit registration).");
            }
        }

        // Only react to the ball.
        Rigidbody ballRb = other.attachedRigidbody;
        if (ballRb == null) return;
        if (other.transform != ball && ballRb.transform != ball) return;

        // Cooldown guard.
        if (Time.time - lastHitTime < hitCooldown) return;
        lastHitTime = Time.time;

        ShotType usedShotType;
        bool usedFallbackReturn = false;

        if (useMLPredictions && hasPendingMLShot)
        {
            // ML mode: use shot profile physics, aim at a target on the player's side
            usedShotType = (ShotType)pendingSwingType;
            BotShotProfile.ShotConfig shot = shotProfile.GetShotByType(usedShotType);
            Vector3 targetPos = PickTarget();
            Vector3 dir = (targetPos - transform.position).normalized;
            ballRb.linearVelocity = dir * shot.hitForce + Vector3.up * shot.upForce;
            hasPendingMLShot = false;

            PlayHitAnimationForSwingType(pendingSwingType);
        }
        else
        {
            usedFallbackReturn = true;
            // Random fallback: pick one of the 4 standard shot types
            usedShotType = PickRandomShotType();
            BotShotProfile.ShotConfig shot = shotProfile.GetShotByType(usedShotType);
            Vector3 targetPos = PickTarget();
            Vector3 dir = (targetPos - transform.position).normalized;
            ballRb.linearVelocity = dir * shot.hitForce + Vector3.up * shot.upForce;

            PlayHitAnimationForSwingType((int)usedShotType);
        }

        // God Mode: halve ball speed on opponent→player return (preserve direction)
        if (gameState != null && gameState.Mode == GameStateManager.GameMode.GodMode)
        {
            ballRb.linearVelocity *= godModeSpeedMultiplier;
        }

        Debug.Log($"[Bot Hit] shotType={usedShotType}  ballVel={ballRb.linearVelocity.magnitude:F1} m/s" +
                  (gameState != null && gameState.Mode == GameStateManager.GameMode.GodMode
                      ? $"  (GodMode {godModeSpeedMultiplier}x)" : ""));

        if (mqttController == null)
            mqttController = FindFirstObjectByType<MqttController>();

        if (mqttController != null)
        {
            if (usedFallbackReturn)
                mqttController.PublishFallbackHit(usedShotType, ballRb.linearVelocity);
            else
                mqttController.PublishAiHit(usedShotType, ballRb.linearVelocity);
        }

        // Register bot hit for scoring
        if (gameState != null)
            gameState.RegisterBotHit(usedShotType);
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

    private ShotType PickRandomShotType()
    {
        // Weighted distribution for realistic bot behaviour:
        //   Drive 40%, Drop 25%, Dink 20%, Lob 15%
        float roll = Random.value;
        if (roll < 0.40f) return ShotType.Drive;
        if (roll < 0.65f) return ShotType.Drop;
        if (roll < 0.85f) return ShotType.Dink;
        return ShotType.Lob;
    }

    private void PlayHitAnimation()
    {
        if (animator == null || ball == null) return;

        Vector3 ballDir = ball.position - transform.position;
        // Use local X to determine forehand vs backhand relative to the bot's facing.
        // Flip side when mirrored so forehand/backhand remain visually correct.
        float localX = transform.InverseTransformDirection(ballDir).x;
        if (mirrorXAxis) localX = -localX;

        if (localX >= 0f)
            animator.Play("forehand");
        else
            animator.Play("backhand");
    }

    private void PlayHitAnimationForSwingType(int swingType)
    {
        if (animator == null) return;

        switch (swingType)
        {
            case 0: // Drive — forehand or backhand based on ball side
                PlayHitAnimation();
                break;
            case 1: // Drop
                animator.Play("Drop");
                break;
            case 2: // Dink
                animator.Play("Dink");
                break;
            case 3: // Lob
                animator.Play("Lob");
                break;
            case 4: // SpeedUp
                animator.Play("SpeedUp");
                break;
            case 5: // HandBattle
                animator.Play("HandBattle");
                break;
            default:
                PlayHitAnimation();
                break;
        }
    }

    private bool TryResolveBall(bool force = false)
    {
        if (!force && ball != null && ball.gameObject.scene.isLoaded)
            return true;

        if (!force && Time.unscaledTime - lastBallLookupTime < 0.25f)
            return ball != null;

        lastBallLookupTime = Time.unscaledTime;

        Transform resolvedBall = null;

        PracticeBallController ballController = gameState != null ? gameState.ballController : null;
        if (ballController == null)
            ballController = PracticeBallController.GetLiveInstance();

        if (ballController != null)
            resolvedBall = ballController.transform;

        if (resolvedBall == null && !string.IsNullOrWhiteSpace(ballTag))
        {
            try
            {
                GameObject taggedBall = GameObject.FindWithTag(ballTag);
                if (taggedBall != null)
                {
                    PracticeBallController taggedController = taggedBall.GetComponent<PracticeBallController>();
                    if (taggedController != null)
                    {
                        resolvedBall = taggedController.transform;
                    }
                    else
                    {
                        Rigidbody taggedBody = taggedBall.GetComponent<Rigidbody>();
                        if (taggedBody != null)
                            resolvedBall = taggedBody.transform;
                    }
                }
            }
            catch (UnityException exception)
            {
                if (!loggedBallTagLookupFailure)
                {
                    loggedBallTagLookupFailure = true;
                    Debug.LogWarning($"[Bot] Ball tag lookup failed: {exception.Message}");
                }
            }
        }

        if (resolvedBall == null)
        {
            Rigidbody[] rigidbodies = FindObjectsByType<Rigidbody>(FindObjectsSortMode.None);
            for (int index = 0; index < rigidbodies.Length; index++)
            {
                Rigidbody body = rigidbodies[index];
                if (body == null) continue;
                if (body.gameObject.name.IndexOf("ball", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    resolvedBall = body.transform;
                    break;
                }
            }
        }

        if (resolvedBall != ball)
        {
            ball = resolvedBall;
            Debug.Log(ball != null
                ? $"[Bot] Reacquired ball: {ball.name}"
                : "[Bot] Failed to reacquire ball.");
        }
        else if (ball == null && Time.unscaledTime - lastNullBallLogTime > 1f)
        {
            lastNullBallLogTime = Time.unscaledTime;
            Debug.LogWarning("[Bot] TryResolveBall failed — no live PracticeBallController or ball transform found.");
        }

        return ball != null;
    }
}
