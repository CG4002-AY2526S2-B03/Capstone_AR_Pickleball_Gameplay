using UnityEngine;
using System;

/// <summary>
/// Manages rally state, scoring, and match progression.
///
/// Match format (Section 6.7):
///   - Best-of-3 sets
///   - Each set played to 11 points, win-by-2
///   - Rally scoring: every rally produces a point outcome
///
/// Game modes:
///   Normal   — Standard match rules. Game ends when a player wins best-of-N sets.
///   Tutorial — No scoring. Ball resets after each rally. Practice only.
///   GodMode  — No scoring, match never ends. Opponent ball returns at 0.5x speed
///              to give player advantage. State transitions still fire for demo.
///
/// Attach to the GameFlowManager GameObject.
/// 
/// Wire ballController in Inspector (or it auto-finds PracticeBallController).
/// </summary>
public class GameStateManager : MonoBehaviour
{
    public enum RallyState { WaitingToServe, InPlay, PointScored, MatchOver }
    public enum Hitter { None, Player, Bot }
    public enum GameMode { Normal, Tutorial, GodMode }

    // ── Singleton ────────────────────────────────────────────────────────────────
    private static GameStateManager _instance;
    public static GameStateManager Instance
    {
        get
        {
            if (_instance == null)
                _instance = FindFirstObjectByType<GameStateManager>();
            return _instance;
        }
    }

    [Header("Game Mode")]
    [Tooltip("Normal = standard match. Tutorial = no scoring, practice. GodMode = no scoring, 0.5x opponent ball speed, never ends.")]
    public GameMode Mode = GameMode.Normal;

    [Header("Match Rules")]
    [Tooltip("Points needed to win a set.")]
    public int pointsToWin = 11;
    [Tooltip("Must win by this margin.")]
    public int winByMargin = 2;
    [Tooltip("Sets needed to win the match (best-of-3 = 2).")]
    public int setsToWin = 2;

    [Header("Court Layout")]
    [Tooltip("Z position of the net in GameSpaceRoot local space. " +
             "Ball Z < this = player side, Z > this = bot side.")]
    public float netZPosition = 0f;

    [Header("Rule Enforcement")]
    [Tooltip("When false, double-bounce events are ignored (useful for free-practice mode).")]
    public bool enforceDoubleBounceFault = true;
    [Tooltip("When false, kitchen / non-volley-zone faults are ignored.")]
    public bool enforceKitchenViolation = false;

    [Header("Timing")]
    [Tooltip("Seconds to display point result before next rally.")]
    public float pointDisplayDuration = 1.5f;

    [Header("Recovery Watchdog")]
    [Tooltip("Maximum automatic ball recovery attempts while waiting to serve before entering a terminal error state.")]
    public int maxWaitingToServeRecoverAttempts = 3;

    [Header("References")]
    public PracticeBallController ballController;
    [Tooltip("Assign the Ball prefab here. Used as last-resort respawn if all runtime balls are destroyed.")]
    public PracticeBallController ballPrefab;
    [Tooltip("Auto-found if null. Needed for unlocking image tracking on game start.")]
    public PlaceTrackedImages imageTracker;

    // ── Public state ─────────────────────────────────────────────────────────
    public RallyState State { get; private set; } = RallyState.WaitingToServe;
    public Hitter LastHitter { get; private set; } = Hitter.None;
    public ShotType LastPlayerShotType { get; private set; } = ShotType.Drive;
    public ShotType LastBotShotType { get; private set; } = ShotType.Drive;
    public int PlayerScore { get; private set; }
    public int BotScore { get; private set; }
    public int PlayerSets { get; private set; }
    public int BotSets { get; private set; }
    public int CurrentSet => PlayerSets + BotSets + 1;
    public bool IsStarted { get; set; }
    public bool IsPaused { get; private set; }

    // ── Events ───────────────────────────────────────────────────────────────
    public event Action OnScoreChanged;
    public event Action<RallyState> OnStateChanged;
    public event Action<string> OnMessage;
    public event Action<bool> OnPauseChanged;
    public event Action<GameMode> OnModeChanged;

    private float pointTimer;
    private float waitingToServeTimer;
    private const float WaitingToServeTimeout = 3f;
    private int waitingToServeRecoveryAttempts;
    private bool waitingToServeRecoveryExhausted;
    private bool requirePlayButtonBeforeNextNormalRally;

    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        if (ballController == null)
            ballController = PracticeBallController.GetLiveInstance();
        if (imageTracker == null)
            imageTracker = FindFirstObjectByType<PlaceTrackedImages>();

        ResetRecoveryWatchdog();
        SetState(RallyState.WaitingToServe);
        OnScoreChanged?.Invoke();

        if (Mode == GameMode.Normal)
            EnterNormalModePrePlayState(IsCourtPlacementPending()
                ? "Scan court QR first"
                : "Press Button 1 to Play game");
    }

    private void Update()
    {
        if (State == RallyState.PointScored)
        {
            pointTimer -= Time.deltaTime;
            if (pointTimer <= 0f)
                StartNewRally();
        }

        // Watchdog: if we're waiting to serve but the ball is missing, recover it.
        if (State == RallyState.WaitingToServe && IsStarted)
        {
            if (IsCourtPlacementPending())
            {
                waitingToServeTimer = 0f;
                return;
            }

            bool ballMissing = ballController == null
                || !ballController.gameObject.activeInHierarchy;
            if (ballMissing)
            {
                if (waitingToServeRecoveryExhausted)
                    return;

                waitingToServeTimer += Time.deltaTime;
                if (waitingToServeTimer >= WaitingToServeTimeout)
                {
                    Debug.LogWarning("[GameState] Ball missing during WaitingToServe — attempting recovery.");
                    OnMessage?.Invoke("Recovering ball...");
                    TryRecoverBallWithAccounting("WaitingToServe watchdog");
                    waitingToServeTimer = 0f;
                }
            }
            else
            {
                ResetRecoveryWatchdog();
            }
        }
        else
        {
            waitingToServeTimer = 0f;
        }
    }

    // ── Called by PaddleHitController ─────────────────────────────────────────

    public void RegisterPlayerHit(ShotType shotType = ShotType.Drive)
    {
        if (!IsStarted)
            return;

        if (State != RallyState.WaitingToServe && State != RallyState.InPlay)
            return;

        LastHitter = Hitter.Player;
        LastPlayerShotType = shotType;
        if (ballController != null) ballController.ResetBounceCount();
        if (State == RallyState.WaitingToServe)
            SetState(RallyState.InPlay);
        Debug.Log($"[GameState] Player hit: {shotType}");
    }

    // ── Called by BotHitController ────────────────────────────────────────────

    public void RegisterBotHit(ShotType shotType = ShotType.Drive)
    {
        if (!IsStarted)
            return;

        if (State != RallyState.InPlay)
            return;

        LastHitter = Hitter.Bot;
        LastBotShotType = shotType;
        if (ballController != null) ballController.ResetBounceCount();
        Debug.Log($"[GameState] Bot hit: {shotType}");
    }

    // ── Called by PracticeBallController on boundary collisions ───────────────

    /// <summary>
    /// True when the ball already had a valid first ground bounce on the opponent
    /// side after the last paddle hit. When true, any subsequent terminating event
    /// (wall, net, out, stall) should award the rally to the last hitter because
    /// it's the opponent who failed to return.
    /// </summary>
    private bool HasCrossedNetAfterLastHit()
    {
        if (ballController == null)
            ballController = PracticeBallController.GetLiveInstance();
        return ballController != null && ballController.HasCrossedNetAfterLastHit;
    }

    public void OnBallOutPlayerSide()
    {
        if (State != RallyState.InPlay) return;
        if (HasCrossedNetAfterLastHit())
        {
            AwardRallyToLastHitter("Player back wall");
            return;
        }
        AwardPoint(toPlayer: false, "Player out");
    }

    public void OnBallOutBotSide()
    {
        if (State != RallyState.InPlay) return;
        if (HasCrossedNetAfterLastHit())
        {
            AwardRallyToLastHitter("Bot back wall");
            return;
        }
        AwardPoint(toPlayer: true, "Bot out");
    }

    public void OnBallOutSideWall()
    {
        if (State != RallyState.InPlay) return;
        if (HasCrossedNetAfterLastHit())
        {
            AwardRallyToLastHitter("Side wall");
            return;
        }
        bool toPlayer = LastHitter == Hitter.Bot;
        AwardPoint(toPlayer, "Side out");
    }

    /// <summary>
    /// Called when the ball's first ground bounce is outside the in-bounds floor zone.
    /// Awards the point against the last hitter. If no hitter is known, falls back
    /// to side-based scoring from the provided local Z coordinate.
    /// </summary>
    public void OnBallOutOfBounds(float ballLocalZ)
    {
        if (State != RallyState.InPlay) return;

        // Note: PracticeBallController only fires this for the FIRST accepted ground
        // bounce, so HasCrossedNetAfterLastHit is always false here. We keep the
        // existing "fault against last hitter" behaviour so a clearly-out shot loses
        // the rally for the player who hit it.
        bool toPlayer;
        string reason;
        if (LastHitter == Hitter.Player)
        {
            toPlayer = false;
            reason = "Player out of bounds";
        }
        else if (LastHitter == Hitter.Bot)
        {
            toPlayer = true;
            reason = "Bot out of bounds";
        }
        else
        {
            bool ballOnPlayerSide = ballLocalZ < GetNetLocalZ();
            toPlayer = !ballOnPlayerSide;
            reason = "Out of bounds";
        }

        AwardPoint(toPlayer, reason);
    }

    public void OnBallHitNet()
    {
        if (State != RallyState.InPlay) return;
        if (HasCrossedNetAfterLastHit())
        {
            AwardRallyToLastHitter("Net rebound");
            return;
        }
        bool toPlayer = LastHitter == Hitter.Bot;
        string faultOwner = LastHitter == Hitter.Bot ? "Bot" : "Player";
        AwardPoint(toPlayer, $"{faultOwner} net fault");
    }

    /// <summary>
    /// Awards the rally to whichever player last hit the ball. Used when the ball's
    /// first bounce already cleared the net (HasCrossedNetAfterLastHit==true) and a
    /// subsequent event (wall, net rebound, stall) ends the rally. Mirrors the
    /// "Player scores" / "Bot scores" messaging used by OnDoubleBounceOnSide so the
    /// UI stays consistent with a rally-win result.
    /// </summary>
    private void AwardRallyToLastHitter(string debugContext)
    {
        bool toPlayer = LastHitter == Hitter.Player;
        string scorer = toPlayer ? "Player scores" : "Bot scores";
        Debug.Log($"[GameState] Rally win after valid return ({debugContext}) — {scorer}");
        AwardPoint(toPlayer, scorer, appendScorerSuffix: false);
    }

    // ── Called by PracticeBallController on second ground bounce ────────────

    public void OnDoubleBounce(float ballLocalZ)
    {
        OnDoubleBounceOnSide(ballLocalZ < GetNetLocalZ());
    }

    public void OnDoubleBounceOnSide(bool bouncedOnPlayerSide)
    {
        if (State != RallyState.InPlay) return;
        if (!enforceDoubleBounceFault || Mode == GameMode.GodMode)
        {
            Debug.Log("[GameState] Double-bounce detected but enforcement is disabled.");
            return;
        }

        // Ball bounced twice on one side — that side's player loses the point.
        bool toPlayer = !bouncedOnPlayerSide;
        AwardPoint(toPlayer, toPlayer ? "Player scores" : "Bot scores", appendScorerSuffix: false);
    }

    /// <summary>
    /// Resolves the net Z position in court-local space from live scene geometry.
    /// Falls back to the inspector value if geometry is unavailable.
    /// </summary>
    public float GetNetLocalZ()
    {
        if (TryResolveNetLocalZ(out float netLocalZ))
            return netLocalZ;
        return netZPosition;
    }

    private bool TryResolveNetLocalZ(out float netLocalZ)
    {
        Transform gameSpaceRoot = FindGameSpaceRoot();
        if (gameSpaceRoot != null)
        {
            Transform netTransform = gameSpaceRoot.Find("Net");
            if (netTransform != null)
            {
                netLocalZ = netTransform.localPosition.z;
                return true;
            }

            CourtBoundary[] boundaries = gameSpaceRoot.GetComponentsInChildren<CourtBoundary>(true);
            for (int index = 0; index < boundaries.Length; index++)
            {
                CourtBoundary boundary = boundaries[index];
                if (boundary != null && boundary.boundaryType == CourtBoundary.BoundaryType.Net)
                {
                    netLocalZ = boundary.transform.localPosition.z;
                    return true;
                }
            }
        }

        CourtBoundarySetup boundarySetup = FindFirstObjectByType<CourtBoundarySetup>();
        if (boundarySetup != null)
        {
            netLocalZ = boundarySetup.netLocalPosition.z;
            return true;
        }

        netLocalZ = 0f;
        return false;
    }

    // ── Called by PaddleHitController on kitchen violation ────────────────────

    public void OnKitchenViolation()
    {
        if (State != RallyState.InPlay) return;
        if (!enforceKitchenViolation) return;
        AwardPoint(toPlayer: false, "Kitchen violation");
    }

    // ── Core scoring logic ───────────────────────────────────────────────────

    private void AwardPoint(bool toPlayer, string reason, bool appendScorerSuffix = true)
    {
        // ── Tutorial / GodMode: no scoring, just show what happened and reset ──
        if (Mode == GameMode.Tutorial || Mode == GameMode.GodMode)
        {
            string scorer = toPlayer ? "Player" : "Bot";
            OnMessage?.Invoke(appendScorerSuffix ? $"{reason} — {scorer} side" : reason);
            FreezeBall();
            pointTimer = pointDisplayDuration;
            SetState(RallyState.PointScored);
            return;
        }

        // ── Normal mode: award point ──
        if (toPlayer)
            PlayerScore++;
        else
            BotScore++;

        string scorerName = toPlayer ? "Player" : "Bot";
        OnMessage?.Invoke(appendScorerSuffix ? $"{reason} — {scorerName} point" : reason);
        OnScoreChanged?.Invoke();

        // Check set win
        if (CheckSetWin(out bool playerWonSet))
        {
            if (playerWonSet) PlayerSets++; else BotSets++;

            string setWinner = playerWonSet ? "Player" : "Bot";
            OnMessage?.Invoke($"{setWinner} wins Set {CurrentSet - 1}!");
            OnScoreChanged?.Invoke();

            // Check match win
            if (PlayerSets >= setsToWin || BotSets >= setsToWin)
            {
                string matchWinner = PlayerSets >= setsToWin ? "Player" : "Bot";
                OnMessage?.Invoke($"{matchWinner} wins the match!");
                FreezeBall();
                SetState(RallyState.MatchOver);
                return;
            }

            // Reset scores for new set
            PlayerScore = 0;
            BotScore = 0;
            if (Mode == GameMode.Normal)
                requirePlayButtonBeforeNextNormalRally = true;
            OnScoreChanged?.Invoke();
        }

        // Freeze ball in place during point display
        FreezeBall();

        pointTimer = pointDisplayDuration;
        SetState(RallyState.PointScored);
    }

    private bool CheckSetWin(out bool playerWon)
    {
        playerWon = false;
        if (PlayerScore >= pointsToWin && PlayerScore - BotScore >= winByMargin)
        {
            playerWon = true;
            return true;
        }
        if (BotScore >= pointsToWin && BotScore - PlayerScore >= winByMargin)
        {
            return true;
        }
        return false;
    }

    private void StartNewRally()
    {
        if (Mode == GameMode.Normal && requirePlayButtonBeforeNextNormalRally)
        {
            requirePlayButtonBeforeNextNormalRally = false;
            LastHitter = Hitter.None;

            if (!IsCourtPlacementPending())
                TryRecoverBallWithAccounting("Next set play gate");

            EnterNormalModePrePlayState(IsCourtPlacementPending()
                ? "Scan court QR first"
                : "Press Button 1 to Play game");
            return;
        }

        LastHitter = Hitter.None;
        SetState(RallyState.WaitingToServe);

        if (TryRecoverBallWithAccounting("StartNewRally"))
        {
            OnMessage?.Invoke("Serve!");
            return;
        }

        if (!waitingToServeRecoveryExhausted)
            OnMessage?.Invoke("Recovering ball...");
    }

    /// <summary>
    /// Aggressively finds the ball, activates it (and its parent hierarchy), and resets it.
    /// Used by StartNewRally and the watchdog timer.
    /// </summary>
    private bool RecoverBall()
    {
        // Step 1: try the cached reference
        if (ballController != null && ballController.gameObject.scene.isLoaded)
        {
            if (ActivateAndReset(ballController))
                return true;
        }

        // Step 2: GetLiveInstance (searches active, then all objects)
        ballController = PracticeBallController.GetLiveInstance();
        if (ballController != null)
        {
            if (ActivateAndReset(ballController))
                return true;
        }

        // Step 3: search by tag as a last resort
        try
        {
            GameObject taggedBall = GameObject.FindWithTag("Ball");
            if (taggedBall != null)
            {
                var ctrl = taggedBall.GetComponent<PracticeBallController>();
                if (ctrl != null)
                {
                    ballController = ctrl;
                    if (ActivateAndReset(ctrl))
                        return true;
                }
            }
        }
        catch (UnityException exception)
        {
            Debug.LogWarning($"[GameState] Ball tag lookup failed during recovery: {exception.Message}");
        }

        // Step 4: ball was destroyed — try runtime backup prefab
        Debug.LogWarning("[GameState] Ball destroyed — respawning from backup.");
        Transform parent = FindGameSpaceRoot();
        ballController = PracticeBallController.RespawnFromBackup(parent);
        if (ballController != null)
        {
            if (ActivateAndReset(ballController))
            {
                OnMessage?.Invoke("Ball respawned!");
                return true;
            }
        }

        // Step 5: absolute last resort — instantiate from the Inspector-assigned prefab
        if (ballPrefab != null)
        {
            Debug.LogWarning("[GameState] Spawning from Inspector ballPrefab.");
            ballController = Instantiate(ballPrefab, parent);
            if (ActivateAndReset(ballController))
            {
                OnMessage?.Invoke("Ball created from prefab!");
                return true;
            }
        }

        Debug.LogError("[GameState] RecoverBall FAILED — no ball and no backup prefab.");
        OnMessage?.Invoke("ERROR: Ball lost!");
        return false;
    }

    private bool TryRecoverBallWithAccounting(string source)
    {
        if (RecoverBall())
        {
            ResetRecoveryWatchdog();
            return true;
        }

        waitingToServeRecoveryAttempts++;
        int maxAttempts = Mathf.Max(1, maxWaitingToServeRecoverAttempts);

        if (waitingToServeRecoveryAttempts < maxAttempts)
            return false;

        waitingToServeRecoveryExhausted = true;
        SetState(RallyState.MatchOver);
        OnMessage?.Invoke("ERROR: Ball recovery failed. Press Reset (Button 2).");
        Debug.LogError($"[GameState] {source} exhausted ball recovery after {waitingToServeRecoveryAttempts} attempts.");
        return false;
    }

    private void ResetRecoveryWatchdog()
    {
        waitingToServeTimer = 0f;
        waitingToServeRecoveryAttempts = 0;
        waitingToServeRecoveryExhausted = false;
    }

    /// <summary>Finds the GameSpaceRoot transform, searching by name if needed.</summary>
    private static Transform FindGameSpaceRoot()
    {
        // Search by common name
        GameObject go = GameObject.Find("GameSpaceRoot");
        if (go != null) return go.transform;

        // Fallback: find any object with BotHitController (it's always under GameSpaceRoot)
        var bot = FindFirstObjectByType<BotHitController>();
        if (bot != null && bot.transform.parent != null)
            return bot.transform.parent;

        return null;
    }

    private bool IsCourtPlacementPending()
    {
        if (imageTracker == null)
            imageTracker = FindFirstObjectByType<PlaceTrackedImages>();

        ARPlaneGameSpacePlacer placer = imageTracker != null && imageTracker.gamePlacer != null
            ? imageTracker.gamePlacer
            : FindFirstObjectByType<ARPlaneGameSpacePlacer>();

        if (placer == null || !placer.PlaceOnlyFromQrAnchor || placer.IsPlaced)
            return false;

        Transform courtRoot = placer.GameSpaceRoot != null
            ? placer.GameSpaceRoot
            : FindGameSpaceRoot();

        // Button 1 should require a fresh QR scan only when the QR-only court is
        // genuinely unavailable. If an active GameSpaceRoot is already present,
        // keep manual ball reset/rally flow usable on that court.
        return courtRoot == null || !courtRoot.gameObject.activeInHierarchy;
    }

    private bool ActivateAndReset(PracticeBallController ball)
    {
        if (ball == null)
            return false;

        if (IsCourtPlacementPending())
        {
            Debug.Log("[GameState] Ball recovery deferred until court QR placement.");
            return false;
        }

        // Ensure the entire parent chain is active so the ball is actually visible.
        Transform t = ball.transform;
        while (t != null)
        {
            if (!t.gameObject.activeSelf)
            {
                Debug.LogWarning($"[GameState] Reactivating inactive ancestor: {t.name}");
                t.gameObject.SetActive(true);
            }
            t = t.parent;
        }

        if (!ball.gameObject.activeInHierarchy)
            ball.gameObject.SetActive(true);

        // If the ball lost its GameSpaceRoot reference (e.g. was the backup ball
        // or was detached via DontDestroyOnLoad), re-assign it now.
        Transform gsr = FindGameSpaceRoot();
        if (gsr != null)
            ball.SetGameSpaceRoot(gsr);

        ball.ResetBall();

        Vector3 pos = ball.transform.position;
        bool active = ball.gameObject.activeInHierarchy;
        Renderer rend = ball.GetComponentInChildren<Renderer>();
        bool visible = rend != null && rend.enabled;
        float scale = ball.transform.lossyScale.magnitude;

        Debug.Log($"[GameState] Ball reset: pos={pos}, active={active}, " +
                  $"rendererOn={visible}, scale={scale:F3}");

        // Show on screen so user can see ball state
        if (!active)
            OnMessage?.Invoke($"Ball INACTIVE!");
        else if (!visible)
            OnMessage?.Invoke($"Ball renderer OFF!");
        else if (scale < 0.001f)
            OnMessage?.Invoke($"Ball scale=0!");

        return true;
    }

    private void FreezeBall()
    {
        if (ballController == null)
            ballController = PracticeBallController.GetLiveInstance();
        if (ballController == null)
        {
            Debug.LogWarning("[GameState] FreezeBall: ball not found — cannot freeze.");
            return;
        }
        ballController.FreezeInPlace();
    }

    public bool ResetBallForManualServe()
    {
        if (IsCourtPlacementPending())
        {
            OnMessage?.Invoke("Scan court QR first");
            Debug.Log("[GameState] Manual serve reset blocked until court QR placement.");
            return false;
        }

        if (IsPaused)
        {
            IsPaused = false;
            Time.timeScale = 1f;
            OnPauseChanged?.Invoke(false);
        }

        pointTimer = 0f;
        LastHitter = Hitter.None;

        if (ballController == null)
            ballController = PracticeBallController.GetLiveInstance();
        if (ballController == null)
            return false;

        if (!ballController.gameObject.activeInHierarchy)
            ballController.gameObject.SetActive(true);

        ballController.ResetBall();
        ResetRecoveryWatchdog();
        SetState(RallyState.WaitingToServe);

        if (Mode == GameMode.Normal)
        {
            // Button 3 should behave like a live manual serve reset, not drop
            // back into the Normal-mode pre-play freeze gate.
            IsStarted = true;
            requirePlayButtonBeforeNextNormalRally = false;
            Time.timeScale = 1f;
            OnPauseChanged?.Invoke(false);
            OnMessage?.Invoke("Serve!");
        }

        return true;
    }

    private void SetState(RallyState state)
    {
        State = state;
        OnStateChanged?.Invoke(state);
    }

    /// <summary>Resets the entire match to initial state.</summary>
    public void ResetMatch()
    {
        PlayerScore = 0;
        BotScore = 0;
        PlayerSets = 0;
        BotSets = 0;
        LastHitter = Hitter.None;
        pointTimer = 0f;
        ResetRecoveryWatchdog();
        OnScoreChanged?.Invoke();
        StartNewRally();
    }

    // ── Start / Pause / Resume / Reset ────────────────────────────────────

    /// <summary>
    /// Button 1: starts the game on first press, then toggles pause/resume.
    /// </summary>
    public void StartOrTogglePause()
    {
        if (!IsStarted)
        {
            if (Mode == GameMode.Normal)
            {
                if (IsCourtPlacementPending())
                {
                    EnterNormalModePrePlayState("Scan court QR first");
                    Debug.Log("[GameState] Play blocked until court QR placement.");
                    return;
                }

                IsStarted = true;
                requirePlayButtonBeforeNextNormalRally = false;
                ResetRecoveryWatchdog();
                Time.timeScale = 1f;
                SetState(RallyState.WaitingToServe);
                OnPauseChanged?.Invoke(false);
                OnScoreChanged?.Invoke();
                OnMessage?.Invoke("Play game");
                Debug.Log("[GameState] Normal mode play started.");
                return;
            }

            IsStarted = true;
            IsPaused = false;
            ResetRecoveryWatchdog();
            if (imageTracker != null)
                imageTracker.StartGame();
            Time.timeScale = 1f;
            SetState(RallyState.WaitingToServe);
            OnPauseChanged?.Invoke(false);
            OnScoreChanged?.Invoke();
            OnMessage?.Invoke("Game Started");
            Debug.Log("[GameState] Game started.");
        }
        else
        {
            if (IsPaused) ResumeGame(); else PauseGame();
        }
    }

    public void PauseGame()
    {
        if (!IsStarted || IsPaused) return;
        IsPaused = true;
        Time.timeScale = 0f;
        OnPauseChanged?.Invoke(true);
        OnMessage?.Invoke("Paused");
        Debug.Log("[GameState] Paused.");
    }

    public void ResumeGame()
    {
        if (!IsPaused) return;
        IsPaused = false;
        Time.timeScale = 1f;
        OnPauseChanged?.Invoke(false);
        OnMessage?.Invoke("Resumed");
        Debug.Log("[GameState] Resumed.");
    }

    /// <summary>
    /// Button 4: full gameplay reset (scores, sets, ball, pause state).
    /// Court placement and image tracking are preserved.
    /// After this, Button 1 becomes "Start" again.
    /// </summary>
    public void ResetGameplay()
    {
        if (IsPaused)
        {
            IsPaused = false;
            Time.timeScale = 1f;
            OnPauseChanged?.Invoke(false);
        }
        IsStarted = false;
        PlayerScore = 0;
        BotScore = 0;
        PlayerSets = 0;
        BotSets = 0;
        LastHitter = Hitter.None;
        pointTimer = 0f;
        ResetRecoveryWatchdog();
        SetState(RallyState.WaitingToServe);
        OnScoreChanged?.Invoke();
        if (ballController == null)
            ballController = PracticeBallController.GetLiveInstance();
        if (ballController != null && !IsCourtPlacementPending())
            ballController.ResetBall();

        if (Mode == GameMode.Normal)
        {
            requirePlayButtonBeforeNextNormalRally = false;
            EnterNormalModePrePlayState(IsCourtPlacementPending()
                ? "Scan court QR first"
                : "Press Button 1 to Play game");
        }
        else
        {
            Time.timeScale = 1f;
            OnMessage?.Invoke("Game Reset");
        }

        Debug.Log("[GameState] Full gameplay reset.");
    }

    public void NotifyCourtQrPlaced()
    {
        if (Mode != GameMode.Normal || IsStarted)
            return;

        EnterNormalModePrePlayState("Court ready — Press Button 1 to Play game");
    }

    public void NotifyCourtReset()
    {
        if (Mode != GameMode.Normal)
            return;

        EnterNormalModePrePlayState("Scan court QR first");
    }

    private void EnterNormalModePrePlayState(string message)
    {
        if (Mode != GameMode.Normal)
            return;

        IsStarted = false;
        if (IsPaused)
        {
            IsPaused = false;
            OnPauseChanged?.Invoke(false);
        }

        Time.timeScale = 0f;
        pointTimer = 0f;
        LastHitter = Hitter.None;
        ResetRecoveryWatchdog();
        SetState(RallyState.WaitingToServe);

        if (ballController == null)
            ballController = PracticeBallController.GetLiveInstance();
        if (ballController != null && ballController.gameObject.activeInHierarchy)
            ballController.FreezeInPlace();

        if (!string.IsNullOrEmpty(message))
            OnMessage?.Invoke(message);
    }

    /// <summary>
    /// Cycles through game modes: Normal → Tutorial → GodMode → Normal.
    /// Only allowed when the game is NOT started (pre-game lobby).
    /// </summary>
    public void CycleMode()
    {
        if (IsStarted)
        {
            Debug.Log("[GameState] Cannot change mode while game is running. Reset first.");
            return;
        }

        Mode = Mode switch
        {
            GameMode.Normal   => GameMode.Tutorial,
            GameMode.Tutorial => GameMode.GodMode,
            GameMode.GodMode  => GameMode.Normal,
            _ => GameMode.Normal
        };

        if (!IsStarted)
        {
            if (Mode == GameMode.Normal)
                EnterNormalModePrePlayState(IsCourtPlacementPending() ? "Scan court QR first" : "Press Button 1 to Play game");
            else
                Time.timeScale = 1f;
        }

        OnModeChanged?.Invoke(Mode);
        OnMessage?.Invoke($"Mode: {Mode}");
        Debug.Log($"[GameState] Mode changed to {Mode}.");
    }
}
