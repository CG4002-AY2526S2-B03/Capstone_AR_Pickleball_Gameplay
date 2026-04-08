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
    public float netZPosition = 5.4f;

    [Header("Timing")]
    [Tooltip("Seconds to display point result before next rally.")]
    public float pointDisplayDuration = 1.5f;

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

    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        if (ballController == null)
            ballController = PracticeBallController.GetLiveInstance();
        if (imageTracker == null)
            imageTracker = FindFirstObjectByType<PlaceTrackedImages>();

        SetState(RallyState.WaitingToServe);
        OnScoreChanged?.Invoke();
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
            bool ballMissing = ballController == null
                || !ballController.gameObject.activeInHierarchy;
            if (ballMissing)
            {
                waitingToServeTimer += Time.deltaTime;
                if (waitingToServeTimer >= WaitingToServeTimeout)
                {
                    Debug.LogWarning("[GameState] Ball missing during WaitingToServe — attempting recovery.");
                    OnMessage?.Invoke("Recovering ball...");
                    RecoverBall();
                    waitingToServeTimer = 0f;
                }
            }
            else
            {
                waitingToServeTimer = 0f;
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
        LastHitter = Hitter.Bot;
        LastBotShotType = shotType;
        if (ballController != null) ballController.ResetBounceCount();
        Debug.Log($"[GameState] Bot hit: {shotType}");
    }

    // ── Called by PracticeBallController on boundary collisions ───────────────

    public void OnBallOutPlayerSide()
    {
        if (State != RallyState.InPlay) return;
        AwardPoint(toPlayer: false, "Out — Bot scores");
    }

    public void OnBallOutBotSide()
    {
        if (State != RallyState.InPlay) return;
        AwardPoint(toPlayer: true, "Out — Player scores");
    }

    public void OnBallOutSideWall()
    {
        if (State != RallyState.InPlay) return;
        bool toPlayer = LastHitter == Hitter.Bot;
        AwardPoint(toPlayer, "Side out");
    }

    public void OnBallHitNet()
    {
        if (State != RallyState.InPlay) return;
        bool toPlayer = LastHitter == Hitter.Bot;
        AwardPoint(toPlayer, "Net fault");
    }

    // ── Called by PracticeBallController on second ground bounce ────────────

    public void OnDoubleBounce(float ballLocalZ)
    {
        if (State != RallyState.InPlay) return;

        // Ball bounced twice on one side — that side's player loses the point.
        bool ballOnPlayerSide = ballLocalZ < netZPosition;
        AwardPoint(toPlayer: !ballOnPlayerSide, "Double bounce");
    }

    // ── Called by PaddleHitController on kitchen violation ────────────────────

    public void OnKitchenViolation()
    {
        if (State != RallyState.InPlay) return;
        AwardPoint(toPlayer: false, "Kitchen violation");
    }

    // ── Core scoring logic ───────────────────────────────────────────────────

    private void AwardPoint(bool toPlayer, string reason)
    {
        // ── Tutorial / GodMode: no scoring, just show what happened and reset ──
        if (Mode == GameMode.Tutorial || Mode == GameMode.GodMode)
        {
            string scorer = toPlayer ? "Player" : "Bot";
            OnMessage?.Invoke($"{reason} — {scorer} side");
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
        OnMessage?.Invoke($"{reason} — {scorerName} point");
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
        LastHitter = Hitter.None;
        RecoverBall();
        SetState(RallyState.WaitingToServe);
        OnMessage?.Invoke("Serve!");
    }

    /// <summary>
    /// Aggressively finds the ball, activates it (and its parent hierarchy), and resets it.
    /// Used by StartNewRally and the watchdog timer.
    /// </summary>
    private void RecoverBall()
    {
        // Step 1: try the cached reference
        if (ballController != null && ballController.gameObject.scene.isLoaded)
        {
            ActivateAndReset(ballController);
            return;
        }

        // Step 2: GetLiveInstance (searches active, then all objects)
        ballController = PracticeBallController.GetLiveInstance();
        if (ballController != null)
        {
            ActivateAndReset(ballController);
            return;
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
                    ActivateAndReset(ctrl);
                    return;
                }
            }
        }
        catch (UnityException) { }

        // Step 4: ball was destroyed — try runtime backup prefab
        Debug.LogWarning("[GameState] Ball destroyed — respawning from backup.");
        Transform parent = FindGameSpaceRoot();
        ballController = PracticeBallController.RespawnFromBackup(parent);
        if (ballController != null)
        {
            ActivateAndReset(ballController);
            OnMessage?.Invoke("Ball respawned!");
            return;
        }

        // Step 5: absolute last resort — instantiate from the Inspector-assigned prefab
        if (ballPrefab != null)
        {
            Debug.LogWarning("[GameState] Spawning from Inspector ballPrefab.");
            ballController = Instantiate(ballPrefab, parent);
            ActivateAndReset(ballController);
            OnMessage?.Invoke("Ball created from prefab!");
            return;
        }

        Debug.LogError("[GameState] RecoverBall FAILED — no ball and no backup prefab.");
        OnMessage?.Invoke("ERROR: Ball lost!");
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

    private void ActivateAndReset(PracticeBallController ball)
    {
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
        SetState(RallyState.WaitingToServe);
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
            IsStarted = true;
            if (imageTracker != null)
                imageTracker.StartGame();
            SetState(RallyState.WaitingToServe);
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
        SetState(RallyState.WaitingToServe);
        OnScoreChanged?.Invoke();
        if (ballController == null)
            ballController = PracticeBallController.GetLiveInstance();
        if (ballController != null)
            ballController.ResetBall();
        OnMessage?.Invoke("Game Reset");
        Debug.Log("[GameState] Full gameplay reset.");
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

        OnModeChanged?.Invoke(Mode);
        OnMessage?.Invoke($"Mode: {Mode}");
        Debug.Log($"[GameState] Mode changed to {Mode}.");
    }
}
