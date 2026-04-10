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
    public float moveSpeed = 12f;

    [Tooltip("Court-local position the bot returns to when idle (no pending ML shot). " +
             "Set to middle-back of bot side, e.g. X=midpoint of court, Z=mid between net and back wall.")]
    public Vector3 idleLocalPosition = new Vector3(-4.35f, 0.85f, 9.0f);

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
    [Tooltip("Minimum local Y the bot racquet can reach (ground level).")]
    public float courtMinY = 0.3f;
    [Tooltip("Maximum local Y the bot racquet can reach (max reach height).")]
    public float courtMaxY = 2.5f;

    [Header("Hit Tuning")]
    [Tooltip("Minimum time between consecutive hits (seconds).")]
    public float hitCooldown = 1.5f;

    [Header("Hit Detection Assist")]
    [Tooltip("When true, bot can still return the ball if trigger contact is missed but the ball is nearby.")]
    public bool enableProximityHitFallback = false;
    [Tooltip("Distance from bot root within which proximity fallback can trigger a hit (metres).")]
    public float proximityHitRadius = 0.9f;
    [Tooltip("Minimum dot product against bot forward for proximity fallback (-1 = all directions, 0 = only in front).")]
    [Range(-1f, 1f)]
    public float proximityFrontDotThreshold = -0.25f;

    [Header("ML Integration")]
    [Tooltip("When true, the bot uses ML predictions from /opponentBall instead of random shots.")]
    public bool useMLPredictions = true;
    [Tooltip("When true, ML return hits use AI-predicted world velocity (vx, vy, vz) directly.")]
    public bool useAiPredictedReturnVelocity = true;
    [Tooltip("Scale applied to AI-predicted return velocity before applying to the ball.")]
    [Min(0f)]
    public float aiPredictedReturnVelocityScale = 0.7f;
    [Tooltip("Minimum AI-predicted speed required to trust predicted return velocity.")]
    [Min(0f)]
    public float minAiPredictedReturnSpeed = 0.1f;

    [Header("ML Z-Crossing Hit")]
    [Tooltip("When true, fires the ML return hit as soon as the ball crosses into the bot's half, " +
             "rather than waiting for physical trigger/proximity contact. Eliminates missed returns " +
             "caused by the ball not reaching the bot collider.")]
    public bool enableMLZCrossingHit = true;
    [Tooltip("Court-local Z threshold the ball must cross to trigger the Z-crossing hit. " +
             "Should match the net Z (e.g. 5.4).")]
    public float mlZCrossingThreshold = 5.4f;
    [Tooltip("Bot must be within this distance of the predicted hit position before firing the return.")]
    public float mlHitPositionTolerance = 0.5f;

    private bool _ballWasOnPlayerSide = true;
    private bool _ballOnBotSide;

    [Header("ML Endpoint Tracking")]
    [Tooltip("When true, movement targets the ML-predicted endpoint directly.")]
    public bool followMlEndpointDirectly = true;
    [Tooltip("When true, applies shot-profile racquet offset during ML movement targeting.")]
    public bool applyShotOffsetInMlTracking = true;
    [Tooltip("When true, ML movement ignores zTrackRange and uses full court Z bounds.")]
    public bool ignoreZTrackRangeForMlPredictions = true;
    [Tooltip("When true, accepts only the first /opponentBall prediction for a return. Additional updates are ignored until the bot hits.")]
    public bool lockFirstMlPredictionPerReturn = true;

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
             "Mirrors only the bot's visual mesh. AI movement/hit mapping stays single-sided.")]
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
    private Vector3 pendingBallVelocity;
    private int pendingSwingType;
    private bool hasPendingMLShot;
    private float lastIgnoredMlPredictionLogTime = -10f;

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
        if (lockFirstMlPredictionPerReturn && hasPendingMLShot)
        {
            if (Time.time - lastIgnoredMlPredictionLogTime >= 0.5f)
            {
                lastIgnoredMlPredictionLogTime = Time.time;
                Debug.Log($"[Bot] Ignoring ML update (latched): newPos={position}, currentPos={pendingBallPosition}");
            }
            return;
        }

        pendingBallPosition = position;
        pendingBallVelocity = velocity;
        pendingSwingType = swingType;
        hasPendingMLShot = true;

        // Publish where the bot is currently, and where it is moving to
        if (mqttController == null)
            mqttController = FindFirstObjectByType<MqttController>();
        if (mqttController != null)
        {
            BotShotProfile.ShotConfig shot = shotProfile != null ? shotProfile.GetShotByType(swingType) : default;
            Transform courtRoot = transform.parent;
            Vector3 botLocalPos = courtRoot != null ? courtRoot.InverseTransformPoint(transform.position) : transform.position;
            Vector3 predictedLocalPos = courtRoot != null ? courtRoot.InverseTransformPoint(position) : position;
            Vector3 trackingOffset = ResolveTrackingOffsetInParentLocal(shot.racquetOffset);
            Vector3 movingTo = predictedLocalPos - trackingOffset;
            mqttController.PublishBotReposition(botLocalPos, movingTo, predictedLocalPos);
        }

        Debug.Log($"[Bot] ML prediction received: ballPos={position}, vel={velocity}, swing={swingType} ({(ShotType)swingType})");
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
        TryMLZCrossingHit();
        TryProximityHit();
    }

    private void TryMLZCrossingHit()
    {
        if (!enableMLZCrossingHit || !useMLPredictions || !hasPendingMLShot)
            return;

        if (gameState == null || !gameState.IsStarted)
            return;

        // Only fire during an active rally — never auto-promote from WaitingToServe
        if (gameState.State != GameStateManager.RallyState.InPlay)
            return;

        if (Time.time - lastHitTime < hitCooldown)
            return;

        // Track whether ball has crossed onto bot's side
        Vector3 localBallPos = transform.parent != null
            ? transform.parent.InverseTransformPoint(ball.position)
            : ball.position;

        bool ballOnPlayerSide = localBallPos.z < mlZCrossingThreshold;

        if (_ballWasOnPlayerSide && !ballOnPlayerSide)
            _ballOnBotSide = true;

        // Ball went back to player side — missed, reset and don't chase
        if (ballOnPlayerSide)
        {
            _ballOnBotSide = false;
            _ballWasOnPlayerSide = ballOnPlayerSide;
            return;
        }

        _ballWasOnPlayerSide = ballOnPlayerSide;

        if (!_ballOnBotSide)
            return;

        // Step 1: check if bot is in position at the predicted strike point (both in court-local)
        Vector3 strikePoint = GetShotStrikePointWorld(pendingSwingType);
        Transform courtRootStep1 = transform.parent;
        Vector3 strikePtLocal = courtRootStep1 != null ? courtRootStep1.InverseTransformPoint(strikePoint) : strikePoint;
        Vector3 pendingLocal = courtRootStep1 != null ? courtRootStep1.InverseTransformPoint(pendingBallPosition) : pendingBallPosition;
        float botDistToTarget = Vector3.Distance(
            new Vector2(strikePtLocal.x, strikePtLocal.z),
            new Vector2(pendingLocal.x, pendingLocal.z));

        if (botDistToTarget > mlHitPositionTolerance)
            return; // still moving to position, wait

        // Step 2: bot is in position — wait for ball to be within strike range
        // Convert both to court-local space (ball is in DontDestroyOnLoad, different world coords)
        Transform courtRoot = transform.parent;
        Vector3 ballLocal = courtRoot != null ? courtRoot.InverseTransformPoint(ball.position) : ball.position;
        Vector3 strikeLocal = courtRoot != null ? courtRoot.InverseTransformPoint(strikePoint) : strikePoint;
        float ballDist = Vector3.Distance(ballLocal, strikeLocal);
        if (ballDist > proximityHitRadius)
            return; // ball not here yet, wait

        // Step 3: fire
        Rigidbody ballRb = ball.GetComponent<Rigidbody>();
        if (ballRb != null)
        {
            lastHitTime = Time.time;
            _ballOnBotSide = false;
            Debug.Log($"[Bot Hit][ZCrossing] In position and ball in range (ballDist={ballDist:F2}) — firing.");
            ExecuteReturnHit(ballRb);
        }
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

        bool useMlTracking = useMLPredictions && hasPendingMLShot;

        if (useMlTracking)
        {
            // AI gives us where the ball will be. By default track endpoint directly.
            BotShotProfile.ShotConfig shot = shotProfile.GetShotByType(pendingSwingType);
            Vector3 localBallPredicted = transform.parent != null
                ? transform.parent.InverseTransformPoint(pendingBallPosition)
                : pendingBallPosition;

            Vector3 botTarget = localBallPredicted;
            Vector3 trackingOffset = Vector3.zero;
            if (!followMlEndpointDirectly || applyShotOffsetInMlTracking)
            {
                // Shot racquet offsets are authored in bot-local space.
                // Convert them into parent-local space before subtracting from the
                // predicted endpoint; this keeps tracking aligned with bot facing.
                trackingOffset = ResolveTrackingOffsetInParentLocal(shot.racquetOffset);
                botTarget -= trackingOffset;
            }

            targetLocal.x = botTarget.x;

            if (trackZAxis)
            {
                if (ignoreZTrackRangeForMlPredictions)
                {
                    targetLocal.z = botTarget.z;
                }
                else
                {
                    float clampedZ = Mathf.Clamp(botTarget.z,
                        startPosition.z - zTrackRange, startPosition.z + zTrackRange);
                    targetLocal.z = clampedZ;
                }
            }

            _debugLogTimer -= Time.deltaTime;
            if (_debugLogTimer <= 0f)
            {
                _debugLogTimer = 1f;
                Debug.Log($"[Bot Move][ML] current={transform.localPosition} target={targetLocal} predictedLocal={localBallPredicted} offset={trackingOffset} vel={pendingBallVelocity}");
            }
        }
        else
        {

            // Fallback: track ball's current position
            // No ML prediction — return to idle centre-back position
            targetLocal.x = idleLocalPosition.x;
            targetLocal.y = idleLocalPosition.y;
            targetLocal.z = idleLocalPosition.z;
        }

        // Clamp to court bounds so the bot never leaves the play area.
        targetLocal.x = Mathf.Clamp(targetLocal.x, courtMinX, courtMaxX);
        targetLocal.y = Mathf.Clamp(targetLocal.y, courtMinY, courtMaxY);
        targetLocal.z = Mathf.Clamp(targetLocal.z, courtMinZ, courtMaxZ);

        transform.localPosition = Vector3.MoveTowards(
            transform.localPosition,
            targetLocal,
            moveSpeed * Time.deltaTime);
    }

    private Vector3 ResolveTrackingOffsetInParentLocal(Vector3 botLocalOffset)
    {
        Vector3 worldOffset = transform.TransformVector(botLocalOffset);
        Vector3 parentLocalOffset = transform.parent != null
            ? transform.parent.InverseTransformVector(worldOffset)
            : worldOffset;
        parentLocalOffset.y = 0f;
        return parentLocalOffset;
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

            if (gameState.State == GameStateManager.RallyState.WaitingToServe)
                return;
        }

        // Only react to the ball.
        Rigidbody ballRb = other.attachedRigidbody;
        if (ballRb == null) return;
        if (other.transform != ball && ballRb.transform != ball) return;

        // Cooldown guard.
        if (Time.time - lastHitTime < hitCooldown) return;
        lastHitTime = Time.time;

        ExecuteReturnHit(ballRb);
    }

    private void TryProximityHit()
    {
        if (!enableProximityHitFallback)
            return;

        if (!TryResolveBall() || ball == null)
            return;

        Rigidbody ballRb = ball.GetComponent<Rigidbody>();
        if (ballRb == null)
            return;

        if (gameState == null || !gameState.IsStarted) return;
        if (gameState.State != GameStateManager.RallyState.InPlay) return;

        if (Time.time - lastHitTime < hitCooldown)
            return;

        float maxRadius = Mathf.Max(0f, proximityHitRadius);
        int trackingSwingType = useMLPredictions && hasPendingMLShot
            ? pendingSwingType
            : (int)ShotType.Drive;
        Vector3 strikePointWorld = GetShotStrikePointWorld(trackingSwingType);
        Vector3 toBall = ballRb.worldCenterOfMass - strikePointWorld;
        float distanceSq = toBall.sqrMagnitude;
        if (distanceSq > maxRadius * maxRadius)
            return;

        float frontDot = toBall.sqrMagnitude > 0.0001f
            ? Vector3.Dot(transform.forward, toBall.normalized)
            : 1f;
        if (frontDot < proximityFrontDotThreshold)
            return;

        lastHitTime = Time.time;
        Debug.Log($"[Bot Hit][Proximity] dist={Mathf.Sqrt(distanceSq):F2} dot={frontDot:F2} shot={(ShotType)trackingSwingType}");

        ExecuteReturnHit(ballRb);
    }

    private Vector3 GetShotStrikePointWorld(int swingType)
    {
        if (shotProfile == null)
            return transform.position;

        BotShotProfile.ShotConfig shot = shotProfile.GetShotByType(swingType);
        return transform.TransformPoint(shot.racquetOffset);
    }

    private void ExecuteReturnHit(Rigidbody ballRb)
    {
        if (ballRb == null)
            return;

        ShotType usedShotType;
        bool usedFallbackReturn = false;

        if (useMLPredictions && hasPendingMLShot)
        {
            // ML mode: prefer AI-predicted return velocity and preserve shot type.
            usedShotType = (ShotType)pendingSwingType;
            Vector3 predictedVelocity = pendingBallVelocity;
            float predictedSpeed = predictedVelocity.magnitude;
            float speedThreshold = Mathf.Max(0f, minAiPredictedReturnSpeed);
            bool canUsePredictedVelocity = useAiPredictedReturnVelocity
                && IsFiniteVector(predictedVelocity)
                && predictedSpeed >= speedThreshold;

            if (canUsePredictedVelocity)
            {
                float velocityScale = Mathf.Max(0f, aiPredictedReturnVelocityScale);
                ballRb.linearVelocity = predictedVelocity * velocityScale;
            }
            else
            {
                BotShotProfile.ShotConfig shot = shotProfile.GetShotByType(usedShotType);
                Vector3 targetPos = PickTarget();
                Vector3 dir = (targetPos - transform.position).normalized;
                ballRb.linearVelocity = dir * shot.hitForce + Vector3.up * shot.upForce;
            }

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
            Transform courtRoot = transform.parent;
            Vector3 strikePointWorld = GetShotStrikePointWorld((int)usedShotType);

            // Convert all positions to court-local space for meaningful comparison
            Vector3 ballLocal = courtRoot != null ? courtRoot.InverseTransformPoint(ballRb.position) : ballRb.position;
            Vector3 strikeLocal = courtRoot != null ? courtRoot.InverseTransformPoint(strikePointWorld) : strikePointWorld;
            Vector3 botLocal = courtRoot != null ? courtRoot.InverseTransformPoint(transform.position) : transform.position;
            Vector3 velLocal = courtRoot != null ? courtRoot.InverseTransformDirection(ballRb.linearVelocity) : ballRb.linearVelocity;

            if (usedFallbackReturn)
                mqttController.PublishFallbackHit(usedShotType, velLocal, ballLocal, strikeLocal, botLocal);
            else
                mqttController.PublishAiHit(usedShotType, velLocal, ballLocal, strikeLocal, botLocal);
        }

        // Register bot hit for scoring
        if (gameState != null)
            gameState.RegisterBotHit(usedShotType);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFiniteVector(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
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
        // Keep this single-sided to match movement mapping.
        float localX = transform.InverseTransformDirection(ballDir).x;

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
