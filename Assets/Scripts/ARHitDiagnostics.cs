using UnityEngine;

/// <summary>
/// Diagnostic overlay that logs paddle ↔ ball positions, distances, and hit events
/// every frame.  Attach to any scene GameObject (e.g. GameFlowManager) — it auto-finds
/// all relevant objects at runtime.
///
/// USAGE
/// ─────
/// 1.  Drag this onto any active GameObject in the scene (or the XR Origin).
/// 2.  Build & run on device.
/// 3.  Open the Unity Console (or Logcat on Android) and filter by "[DIAG]".
/// 4.  The on-screen GUI also shows a small live readout in the bottom-left corner.
///
/// The script will tell you:
///  • Whether the physics paddle (PlayerPaddle) was found
///  • Whether the visual AprilTag racket was found
///  • The world-space positions of the ball, physics paddle, and AprilTag racket
///  • The distance between each pair
///  • When a hit impulse fires (hooks into PaddleHitController)
///  • When BallContactDetector registers a paddle reference
/// </summary>
public class ARHitDiagnostics : MonoBehaviour
{
    // ── Auto-discovered references ───────────────────────────────────────────────
    private PaddleHitController physicsPaddle;
    private BallContactDetector ballDetector;
    private Transform ballTransform;
    private Rigidbody ballRigidbody;
    private Transform qrRacketTransform;  // the spawned Racket_Pickleball4
    private ImuPaddleController imuController;
    private BotHitController botController;
    private Transform gameSpaceRoot;

    // ── Logging control ──────────────────────────────────────────────────────────
    [Header("Logging")]
    [Tooltip("Log a full status line every N seconds (0 = every frame, which is very spammy).")]
    public float logIntervalSeconds = 1.0f;

    [Tooltip("Also draw Debug.DrawLine gizmos in the Scene view.")]
    public bool drawGizmos = true;

    // ── On-screen HUD ────────────────────────────────────────────────────────────
    [Header("On-Screen HUD")]
    public bool showOnScreenHUD = true;

    [Header("Auto-Fix")]
    [Tooltip("Automatically fix known issues at runtime (paddle collider size, ball collision mode, etc).")]
    public bool autoFixIssues = true;

    // ── Private state ────────────────────────────────────────────────────────────
    private float nextLogTime;
    private string hudText = "";
    private int hitCount;
    private float lastHitTime;
    private GUIStyle hudStyle;

    // ─────────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        Discover();
        InvokeRepeating(nameof(Discover), 2f, 3f);  // re-scan periodically (objects spawn late)
    }

    /// <summary>
    /// Searches for all relevant objects.  Safe to call repeatedly — objects that
    /// spawn later (AprilTag racket) will be picked up within a few seconds.
    /// </summary>
    private void Discover()
    {
        // ── Physics paddle (PlayerPaddle with PaddleHitController) ────────────────
        if (physicsPaddle == null)
        {
            physicsPaddle = FindFirstObjectByType<PaddleHitController>();
            if (physicsPaddle != null)
            {
                Debug.Log($"[DIAG] Found physics paddle: '{physicsPaddle.gameObject.name}' " +
                          $"at world {physicsPaddle.transform.position}  " +
                          $"parent={(physicsPaddle.transform.parent != null ? physicsPaddle.transform.parent.name : "ROOT")}");

                // Log collider info
                Collider[] cols = physicsPaddle.GetComponentsInChildren<Collider>();
                foreach (var c in cols)
                {
                    Debug.Log($"[DIAG]   Paddle collider: {c.GetType().Name} on '{c.gameObject.name}' " +
                              $"bounds.size={c.bounds.size} isTrigger={c.isTrigger}");

                    // Auto-fix: shrink 1m³ box to realistic paddle size
                    if (autoFixIssues && c is BoxCollider box)
                    {
                        if (box.size.x >= 0.9f && box.size.y >= 0.9f && box.size.z >= 0.9f)
                        {
                            box.size = new Vector3(0.22f, 0.26f, 0.02f); // ~22cm wide, 26cm tall, 2cm thick
                            Debug.Log($"[DIAG] ✔ Auto-fixed paddle BoxCollider size → {box.size}");
                        }
                    }
                }

                // Log rigidbody
                Rigidbody rb = physicsPaddle.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    Debug.Log($"[DIAG]   Paddle Rigidbody: isKinematic={rb.isKinematic} " +
                              $"useGravity={rb.useGravity} collisionMode={rb.collisionDetectionMode}");

                    // Auto-fix: ensure ContinuousSpeculative for kinematic paddle
                    if (autoFixIssues && rb.collisionDetectionMode == CollisionDetectionMode.Discrete)
                    {
                        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                        Debug.Log("[DIAG] ✔ Auto-fixed paddle collisionDetection → ContinuousSpeculative");
                    }
                }
            }
            else
            {
                Debug.LogWarning("[DIAG] ⚠ PaddleHitController NOT found in scene!");
            }
        }

        // ── BallContactDetector (on Ball2) ────────────────────────────────────────
        if (ballDetector == null)
        {
            ballDetector = FindFirstObjectByType<BallContactDetector>();
            if (ballDetector != null)
            {
                ballTransform = ballDetector.transform;
                ballRigidbody = ballDetector.GetComponent<Rigidbody>();
                Debug.Log($"[DIAG] Found ball: '{ballDetector.gameObject.name}' " +
                          $"at world {ballTransform.position}  " +
                          $"parent={(ballTransform.parent != null ? ballTransform.parent.name : "ROOT")}");

                // Check if BallContactDetector found the paddle
                if (ballDetector.paddle != null)
                {
                    Debug.Log($"[DIAG]   BallContactDetector.paddle → '{ballDetector.paddle.gameObject.name}' ✓");
                }
                else
                {
                    Debug.LogWarning("[DIAG]   ⚠ BallContactDetector.paddle is NULL — ball cannot detect hits!");
                }

                // Ball collider
                Collider bc = ballDetector.GetComponent<Collider>();
                if (bc != null)
                {
                    Debug.Log($"[DIAG]   Ball collider: {bc.GetType().Name} bounds.size={bc.bounds.size}");
                }
                else
                {
                    Debug.LogWarning("[DIAG]   ⚠ Ball has NO collider!");
                }

                // Ball rigidbody
                if (ballRigidbody != null)
                {
                    Debug.Log($"[DIAG]   Ball Rigidbody: isKinematic={ballRigidbody.isKinematic} " +
                              $"useGravity={ballRigidbody.useGravity} " +
                              $"collisionMode={ballRigidbody.collisionDetectionMode} " +
                              $"tag='{ballDetector.gameObject.tag}'");

                    // Auto-fix: ball should use ContinuousDynamic to avoid tunneling
                    if (autoFixIssues && ballRigidbody.collisionDetectionMode == CollisionDetectionMode.Discrete)
                    {
                        ballRigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                        Debug.Log("[DIAG] ✔ Auto-fixed ball collisionDetection → ContinuousDynamic");
                    }
                }
            }
            else
            {
                Debug.LogWarning("[DIAG] ⚠ BallContactDetector NOT found — ball has no hit detector!");
            }
        }

        // Ball may exist even without BallContactDetector
        if (ballTransform == null)
        {
            GameObject ballGO = GameObject.Find("Ball2");
            if (ballGO != null)
            {
                ballTransform = ballGO.transform;
                ballRigidbody = ballGO.GetComponent<Rigidbody>();
                Debug.Log($"[DIAG] Found Ball2 by name (no BallContactDetector) at {ballTransform.position}");
            }
        }

        // ── AprilTag-spawned racket visual ──────────────────────────────────────────────
        if (qrRacketTransform == null)
        {
            // PlaceTrackedImages spawns Racket_Pickleball4 as child of ARTrackedImage.
            // The spawned clone will be named "Racket_Pickelball4(Clone)" or similar.
            GameObject[] allObjects = FindObjectsByType<GameObject>(FindObjectsSortMode.None);
            foreach (var go in allObjects)
            {
                string n = go.name.ToLower();
                if ((n.Contains("racket") || n.Contains("racquet")) && n.Contains("picke"))
                {
                    // Skip the one inside GameSpaceRoot (bot's racquet)
                    if (go.transform.parent != null &&
                        go.transform.root.name.Contains("GameSpace"))
                    {
                        continue;
                    }

                    qrRacketTransform = go.transform;
                    Debug.Log($"[DIAG] Found AprilTag-spawned racket: '{go.name}' " +
                              $"at world {go.transform.position}  " +
                              $"parent={(go.transform.parent != null ? go.transform.parent.name : "ROOT")}");

                    Collider rc = go.GetComponentInChildren<Collider>();
                    if (rc != null)
                    {
                        Debug.Log($"[DIAG]   AprilTag racket has collider: {rc.GetType().Name}");
                    }
                    else
                    {
                        Debug.LogWarning("[DIAG]   ⚠ AprilTag-spawned racket has NO Collider — it CANNOT participate in physics!");
                    }

                    Rigidbody rrb = go.GetComponentInChildren<Rigidbody>();
                    if (rrb == null)
                    {
                        Debug.LogWarning("[DIAG]   ⚠ AprilTag-spawned racket has NO Rigidbody!");
                    }

                    PaddleHitController phc = go.GetComponentInChildren<PaddleHitController>();
                    if (phc == null)
                    {
                        Debug.LogWarning("[DIAG]   ⚠ AprilTag-spawned racket has NO PaddleHitController — " +
                                         "it is purely visual and WILL NOT hit the ball!");
                    }
                    break;
                }
            }
        }

        // ── IMU controller ──────────────────────────────────────────────────────
        if (imuController == null)
            imuController = FindFirstObjectByType<ImuPaddleController>();

        // ── Bot controller ──────────────────────────────────────────────────────
        if (botController == null)
            botController = FindFirstObjectByType<BotHitController>();

        // ── GameSpaceRoot (for court-local ↔ AI coordinate display) ─────────────
        if (gameSpaceRoot == null)
        {
            MqttController mqtt = FindFirstObjectByType<MqttController>();
            if (mqtt != null && mqtt.gameSpaceRoot != null)
                gameSpaceRoot = mqtt.gameSpaceRoot;
        }
    }

    /// <summary>
    /// Converts a Unity world position to the AI coordinate space for display.
    /// Unity: x=right, y=up, z=forward.  AI: x=right, y=depth(forward), z=height(up).
    /// </summary>
    private Vector3 WorldToAI(Vector3 worldPos)
    {
        Vector3 local = gameSpaceRoot != null
            ? gameSpaceRoot.InverseTransformPoint(worldPos)
            : worldPos;
        // y↔z swap: Unity (x, y=up, z=fwd) → AI (x, y=depth, z=height)
        return new Vector3(local.x, local.z, local.y);
    }

    // ─────────────────────────────────────────────────────────────────────────────

    private void Update()
    {
        if (ballTransform == null) return;

        Vector3 ballPos = ballTransform.position;
        Vector3 ballVel = ballRigidbody != null ? ballRigidbody.linearVelocity : Vector3.zero;

        // ── Distances ─────────────────────────────────────────────────────────────
        float paddleToBall = -1f;
        float qrToBall = -1f;
        float paddleToQR = -1f;
        Vector3 paddlePos = Vector3.zero;
        Vector3 qrPos = Vector3.zero;

        if (physicsPaddle != null)
        {
            paddlePos = physicsPaddle.transform.position;
            paddleToBall = Vector3.Distance(paddlePos, ballPos);
        }
        if (qrRacketTransform != null)
        {
            qrPos = qrRacketTransform.position;
            qrToBall = Vector3.Distance(qrPos, ballPos);
        }
        if (physicsPaddle != null && qrRacketTransform != null)
        {
            paddleToQR = Vector3.Distance(paddlePos, qrPos);
        }

        // ── Check BallContactDetector.paddle reference live ──────────────────────
        bool ballHasPaddleRef = ballDetector != null && ballDetector.paddle != null;

        // ── IMU data ──────────────────────────────────────────────────────────────
        Vector3 imuLinVel = Vector3.zero;
        Vector3 imuAngVel = Vector3.zero;
        bool imuActive = false;
        if (imuController != null && imuController.IsActive)
        {
            imuLinVel = imuController.PaddleVelocity;
            imuAngVel = imuController.PaddleAngularVelocity;
            imuActive = true;
        }

        // ── Bot position ─────────────────────────────────────────────────────────
        Vector3 botPos = Vector3.zero;
        if (botController != null)
            botPos = botController.transform.position;

        // ── Build HUD text ────────────────────────────────────────────────────────
        // Shows both Unity world coords and AI coords (y<->z swapped) so you can
        // verify the coordinate mapping between Ultra96 AI and Unity in real time.
        hudText = "--- IMU Paddle ---\n";
        if (imuActive)
        {
            hudText += $"  linVel: ({imuLinVel.x:F2}, {imuLinVel.y:F2}, {imuLinVel.z:F2})  |{imuLinVel.magnitude:F2}| m/s\n";
            hudText += $"  angVel: ({imuAngVel.x:F2}, {imuAngVel.y:F2}, {imuAngVel.z:F2})  |{imuAngVel.magnitude:F2}| rad/s\n";
        }
        else
        {
            hudText += "  IMU: inactive\n";
        }

        hudText += "\n--- Racket ---\n";
        if (physicsPaddle != null)
        {
            Vector3 aiPaddle = WorldToAI(paddlePos);
            hudText += $"  world: ({paddlePos.x:F2}, {paddlePos.y:F2}, {paddlePos.z:F2})\n";
            hudText += $"  AI:    ({aiPaddle.x:F2}, {aiPaddle.y:F2}, {aiPaddle.z:F2})\n";
        }
        else
            hudText += "  NOT FOUND\n";

        hudText += "\n--- Bot ---\n";
        if (botController != null)
        {
            Vector3 aiBot = WorldToAI(botPos);
            hudText += $"  world: ({botPos.x:F2}, {botPos.y:F2}, {botPos.z:F2})\n";
            hudText += $"  AI:    ({aiBot.x:F2}, {aiBot.y:F2}, {aiBot.z:F2})\n";
        }
        else
            hudText += "  NOT FOUND\n";

        hudText += "\n--- Ball ---\n";
        {
            Vector3 aiBall = WorldToAI(ballPos);
            hudText += $"  world: ({ballPos.x:F2}, {ballPos.y:F2}, {ballPos.z:F2})  vel: {ballVel.magnitude:F1} m/s\n";
            hudText += $"  AI:    ({aiBall.x:F2}, {aiBall.y:F2}, {aiBall.z:F2})\n";
        }

        hudText += $"\nd(paddle>ball): {(paddleToBall >= 0 ? paddleToBall.ToString("F2") + "m" : "-")}";
        hudText += $"  Hits: {hitCount}";

        // ── Draw debug lines in Scene view ────────────────────────────────────────
        if (drawGizmos)
        {
            if (physicsPaddle != null)
                Debug.DrawLine(ballPos, paddlePos, paddleToBall < 0.15f ? Color.red : Color.cyan);
            if (qrRacketTransform != null)
                Debug.DrawLine(ballPos, qrPos, Color.magenta);
            if (physicsPaddle != null && qrRacketTransform != null)
                Debug.DrawLine(paddlePos, qrPos, Color.yellow);
        }

        // ── Periodic console log ──────────────────────────────────────────────────
        if (Time.time >= nextLogTime)
        {
            nextLogTime = Time.time + logIntervalSeconds;

            Debug.Log($"[DIAG] ball=({ballPos.x:F3},{ballPos.y:F3},{ballPos.z:F3}) vel={ballVel.magnitude:F2} | " +
                      $"paddle=({paddlePos.x:F3},{paddlePos.y:F3},{paddlePos.z:F3}) | " +
                      $"d(p→b)={paddleToBall:F3} | " +
                      (qrRacketTransform != null
                          ? $"qr=({qrPos.x:F3},{qrPos.y:F3},{qrPos.z:F3}) d(qr→b)={qrToBall:F3} d(p→qr)={paddleToQR:F3}"
                          : "qr=notSpawned") +
                      $" | ballDetector.paddle={(ballHasPaddleRef ? "OK" : "NULL")}");

            // Warn if paddle and AprilTag racket are far apart
            if (paddleToQR > 0.05f)
            {
                Debug.LogWarning($"[DIAG] ⚠ Physics paddle is {paddleToQR:F3}m away from AprilTag racket! " +
                                 "The player sees the racket in one place but physics paddle is elsewhere.");
            }

            // Warn if ball is very close to the physics paddle but no hit has happened recently
            if (paddleToBall >= 0 && paddleToBall < 0.15f && Time.time - lastHitTime > 0.5f)
            {
                Debug.LogWarning($"[DIAG] ⚠ Ball is {paddleToBall:F3}m from paddle but no recent hit. " +
                                 "Check: colliders, layers, physics contact generation.");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Call this from PaddleHitController.ApplyHitImpulse to log every hit.
    // We hook in via a monkey-patch approach: the diagnostic logger checks for
    // velocity changes on the ball each FixedUpdate as a non-invasive alternative.
    // ─────────────────────────────────────────────────────────────────────────────

    private Vector3 prevBallVelocity;

    private void FixedUpdate()
    {
        if (ballRigidbody == null) return;

        Vector3 currentVel = ballRigidbody.linearVelocity;
        float deltaV = (currentVel - prevBallVelocity).magnitude;

        // A sudden velocity change > 1 m/s is almost certainly a hit impulse
        if (deltaV > 1f && Time.time - lastHitTime > 0.05f)
        {
            hitCount++;
            lastHitTime = Time.time;

            float paddleDist = physicsPaddle != null
                ? Vector3.Distance(physicsPaddle.transform.position, ballTransform.position)
                : -1f;

            float qrDist = qrRacketTransform != null
                ? Vector3.Distance(qrRacketTransform.position, ballTransform.position)
                : -1f;

            Debug.Log($"<color=green>[DIAG] ★ HIT #{hitCount}</color> " +
                      $"ΔV={deltaV:F2} m/s  newVel={currentVel.magnitude:F1} m/s  " +
                      $"d(paddle→ball)={paddleDist:F3}m  " +
                      (qrDist >= 0 ? $"d(AprilTag→ball)={qrDist:F3}m  " : "") +
                      $"ball@({ballTransform.position.x:F3},{ballTransform.position.y:F3},{ballTransform.position.z:F3})");

            if (physicsPaddle != null && qrRacketTransform != null)
            {
                float pqDist = Vector3.Distance(physicsPaddle.transform.position, qrRacketTransform.position);
                if (pqDist > 0.05f)
                {
                    Debug.LogWarning($"[DIAG] ★ Hit registered {pqDist:F3}m away from the visible AprilTag racket! " +
                                     "Player will perceive this as a phantom hit.");
                }
            }
        }

        prevBallVelocity = currentVel;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // On-screen GUI for device testing (no console on phone)
    // ─────────────────────────────────────────────────────────────────────────────

    private void OnGUI()
    {
        if (!showOnScreenHUD) return;

        if (hudStyle == null)
        {
            hudStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = (int)(Screen.height * 0.018f),
                fontStyle = FontStyle.Bold,
                wordWrap = true
            };
            hudStyle.normal.textColor = Color.white;
        }

        // Semi-transparent background — sized for expanded HUD
        float boxWidth = Screen.width * 0.60f;
        float boxHeight = Screen.height * 0.48f;
        float boxX = 10f;
        float boxY = Screen.height - boxHeight - 10f;

        Rect bgRect = new Rect(boxX, boxY, boxWidth, boxHeight);

        // Draw background
        Texture2D bgTex = new Texture2D(1, 1);
        bgTex.SetPixel(0, 0, new Color(0, 0, 0, 0.65f));
        bgTex.Apply();
        GUI.DrawTexture(bgRect, bgTex);

        GUI.Label(new Rect(boxX + 8, boxY + 4, boxWidth - 16, boxHeight - 8), hudText, hudStyle);
    }
}
