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
/// Attach to the GameFlowManager GameObject.
/// Wire ballController in Inspector (or it auto-finds PracticeBallController).
/// </summary>
public class GameStateManager : MonoBehaviour
{
    public enum RallyState { WaitingToServe, InPlay, PointScored, MatchOver }
    public enum Hitter { None, Player, Bot }

    [Header("Match Rules")]
    [Tooltip("Points needed to win a set.")]
    public int pointsToWin = 11;
    [Tooltip("Must win by this margin.")]
    public int winByMargin = 2;
    [Tooltip("Sets needed to win the match (best-of-3 = 2).")]
    public int setsToWin = 2;

    [Header("Timing")]
    [Tooltip("Seconds to display point result before next rally.")]
    public float pointDisplayDuration = 1.5f;

    [Header("References")]
    public PracticeBallController ballController;

    // ── Public state ─────────────────────────────────────────────────────────
    public RallyState State { get; private set; } = RallyState.WaitingToServe;
    public Hitter LastHitter { get; private set; } = Hitter.None;
    public int PlayerScore { get; private set; }
    public int BotScore { get; private set; }
    public int PlayerSets { get; private set; }
    public int BotSets { get; private set; }
    public int CurrentSet => PlayerSets + BotSets + 1;

    // ── Events ───────────────────────────────────────────────────────────────
    public event Action OnScoreChanged;
    public event Action<RallyState> OnStateChanged;
    public event Action<string> OnMessage;

    private float pointTimer;

    // ─────────────────────────────────────────────────────────────────────────

    private void Start()
    {
        if (ballController == null)
            ballController = FindFirstObjectByType<PracticeBallController>();

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
    }

    // ── Called by PaddleHitController ─────────────────────────────────────────

    public void RegisterPlayerHit()
    {
        LastHitter = Hitter.Player;
        if (State == RallyState.WaitingToServe)
            SetState(RallyState.InPlay);
    }

    // ── Called by BotHitController ────────────────────────────────────────────

    public void RegisterBotHit()
    {
        LastHitter = Hitter.Bot;
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

    // ── Called by PaddleHitController on kitchen violation ────────────────────

    public void OnKitchenViolation()
    {
        if (State != RallyState.InPlay) return;
        AwardPoint(toPlayer: false, "Kitchen violation");
    }

    // ── Core scoring logic ───────────────────────────────────────────────────

    private void AwardPoint(bool toPlayer, string reason)
    {
        if (toPlayer)
            PlayerScore++;
        else
            BotScore++;

        string scorer = toPlayer ? "Player" : "Bot";
        OnMessage?.Invoke($"{reason} — {scorer} point");
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
        if (ballController != null)
            ballController.ResetBall();
        SetState(RallyState.WaitingToServe);
    }

    private void FreezeBall()
    {
        if (ballController == null) return;
        var deadHang = ballController.GetComponent<DeadHangBall>();
        if (deadHang != null)
            deadHang.Freeze();
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
        OnScoreChanged?.Invoke();
        StartNewRally();
    }
}
