using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using TMPro;
using System.Collections.Generic;

/// <summary>
/// Displays tutorial UI panels and video playback for each TutorialStep.
/// Manages video playback, text display, and button interaction.
/// </summary>
public class TutorialUIManager : MonoBehaviour
{
    // ── Singleton ────────────────────────────────────────────────────────────────
    private static TutorialUIManager _instance;
    public static TutorialUIManager Instance
    {
        get
        {
            if (_instance == null)
                _instance = FindFirstObjectByType<TutorialUIManager>();
            return _instance;
        }
    }

    // ── Inspector References: Main Containers ───────────────────────────────────
    [Header("Main Panel Containers")]
    [SerializeField] private Canvas tutorialCanvas;              // Root canvas for all tutorial UI
    [SerializeField] private CanvasGroup mainPanelGroup;         // Main tutorial panel
    [SerializeField] private RawImage demoVideoDisplay;          // RawImage for VideoPlayer output
    [SerializeField] private TextMeshProUGUI titleText;          // Step title
    [SerializeField] private TextMeshProUGUI instructionText;    // Step instructions
    [SerializeField] private TextMeshProUGUI messageText;         // Transient messages ("Click Next", warnings, etc.)
    [SerializeField] private Button nextButton;                  // "Next" button
    [SerializeField] private Button skipButton;                  // Optional skip button (if you want it)

    [Header("Calibration-Specific UI")]
    [SerializeField] private Image buttonDisplay;                // Shows 4 buttons on screen
    [SerializeField] private Text buttonHighlight;               // Highlights which button to press

    [Header("Video Data")]
    [SerializeField] private VideoPlayer videoPlayer;            // VideoPlayer component
    [SerializeField] private VideoClip hardwareGuideVideo;       // Step 0 video
    [SerializeField] private VideoClip placeCourtGuideVideo;     // Step 1 video

    // ── Component References ─────────────────────────────────────────────────────
    private TutorialManager tutorialManager;
    private GameStateManager gameState;

    // ─────────────────────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;
    }

    private void Start()
    {
        tutorialManager = TutorialManager.Instance;
        gameState = GameStateManager.Instance ?? FindFirstObjectByType<GameStateManager>();

        if (tutorialManager == null)
        {
            Debug.LogError("[TutorialUI] TutorialManager not found. Disabling tutorial UI manager.");
            enabled = false;
            return;
        }

        if (tutorialCanvas == null)
            tutorialCanvas = GetComponentInParent<Canvas>();

        // Find VideoPlayer if not assigned
        if (videoPlayer == null)
            videoPlayer = demoVideoDisplay?.GetComponentInParent<VideoPlayer>();

        // Wire up button callbacks
        if (nextButton != null)
            nextButton.onClick.AddListener(OnNextClicked);
        if (skipButton != null)
            skipButton.onClick.AddListener(OnSkipClicked);

        // Subscribe to mode changes
        if (gameState != null)
            gameState.OnModeChanged += OnModeChanged;

        // Only show canvas in Tutorial mode
        UpdateCanvasVisibility();

        if (mainPanelGroup == null)
            Debug.LogError("[TutorialUI] mainPanelGroup is NULL. Tutorial panel cannot be shown.");
    }

    // ── Public API ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Updates canvas visibility based on current game mode.
    /// Only shows tutorial UI when in Tutorial mode.
    /// </summary>
    private void UpdateCanvasVisibility()
    {
        if (tutorialCanvas == null) return;
        tutorialCanvas.enabled = gameState != null && gameState.Mode == GameStateManager.GameMode.Tutorial;
    }

    /// <summary>
    /// Called when game mode changes.
    /// </summary>
    private void OnModeChanged(GameStateManager.GameMode newMode)
    {
        UpdateCanvasVisibility();
        Debug.Log($"[TutorialUI] Mode changed to {newMode}. Canvas enabled: {tutorialCanvas?.enabled}");
    }

    /// <summary>
    /// Displays the UI for a specific tutorial step.
    /// </summary>
    public void ShowStep(TutorialManager.TutorialStep step)
    {
        CancelInvoke(nameof(InvokeNextStep));

        HideAllPanels();
        if (mainPanelGroup == null)
            return;

        mainPanelGroup.alpha = 1f;
        mainPanelGroup.blocksRaycasts = true;

        switch (step)
        {
            case TutorialManager.TutorialStep.HardwareGuide:
                ShowHardwareGuide();
                break;
            case TutorialManager.TutorialStep.PlaceCourtGuide:
                ShowPlaceCourtGuide();
                break;
            case TutorialManager.TutorialStep.PressButtonToCalibrate:
                ShowPressButtonToCalibrate();
                break;
            case TutorialManager.TutorialStep.CalibrationStep1_Paddle:
                ShowCalibrationStep1();
                break;
            case TutorialManager.TutorialStep.CalibrationStep2_Position:
                ShowCalibrationStep2();
                break;
            case TutorialManager.TutorialStep.CalibrationComplete:
                ShowCalibrationComplete();
                break;
            case TutorialManager.TutorialStep.ServingDemo:
                ShowServingDemo();
                break;
            case TutorialManager.TutorialStep.OpponentResponse:
                ShowOpponentResponse();
                break;
            case TutorialManager.TutorialStep.MovementDemo:
                ShowMovementDemo();
                break;
            case TutorialManager.TutorialStep.GameplayExplanation:
                ShowGameplayExplanation();
                break;
            case TutorialManager.TutorialStep.ReadyToPlay:
                ShowReadyToPlay();
                break;
        }
    }

    /// <summary>
    /// Hides all tutorial UI panels.
    /// </summary>
    public void HideAllPanels()
    {
        if (mainPanelGroup != null)
        {
            mainPanelGroup.alpha = 0f;
            mainPanelGroup.blocksRaycasts = false;
        }
        StopVideoPlayback();
    }

    /// <summary>
    /// Shows a transient message (e.g., "Click Next" or error).
    /// Auto-hides after a few seconds.
    /// </summary>
    public void ShowMessage(string message)
    {
        if (messageText != null)
        {
            messageText.text = message;
            messageText.gameObject.SetActive(true);
            // Could add timer to auto-hide here if desired
        }
    }

    // ── Step Display Methods ─────────────────────────────────────────────────────

    private void ShowHardwareGuide()
    {
        titleText.text = "HARDWARE GUIDE";
        instructionText.text = "Your controller has 4 buttons:\n\n" +
                               "🔴 Button 1: Start/Pause/Resume\n" +
                               "🟡 Button 2: Calibrate\n" +
                               "🔵 Button 3: Reset Ball\n" +
                               "⚪ Button 4: Cycle Game Mode\n\n" +
                               "Top-right shows: Connection + Scoreboard + Mode";

        PlayVideo(hardwareGuideVideo);
        ShowNextButton();
    }

    private void ShowPlaceCourtGuide()
    {
        titleText.text = "HOW TO START THE GAME";
        instructionText.text = "Step 1: Scan the floor QR code to spawn the court\n\nStep 2: Press 🔴 Button 1 to begin calibration!";

        PlayVideo(placeCourtGuideVideo);
        HideNextButton();  // Auto-advances on QR detection
    }

    private void ShowPressButtonToCalibrate()
    {
        titleText.text = "PREPARE FOR CALIBRATION";
        instructionText.text = "Stand at the center of the court.\n\nHold the paddle in a neutral position (face perpendicular to ground).\n\nPress 🟡 Button 2 to calibrate!";

        HideNextButton();  // Advances on Button 2 press
    }

    private void ShowCalibrationStep1()
    {
        titleText.text = "CALIBRATE PADDLE (Step 1/2)";
        instructionText.text = "Hold your paddle in a neutral position.\n\nKeep it steady.\n\nPress 🟡 Button 2 to confirm.";

        HighlightButton(2);
        HideNextButton();  // Advances on Button 2 press
    }

    private void ShowCalibrationStep2()
    {
        titleText.text = "CALIBRATE POSITION (Step 2/2)";
        instructionText.text = "Stand in the center of the court.\n\nPositioning confirmed by UWB sensors.\n\nPress 🟡 Button 2 to confirm.";

        HighlightButton(2);
        HideNextButton();  // Advances on Button 2 press
    }

    private void ShowCalibrationComplete()
    {
        titleText.text = "CALIBRATION COMPLETE!";
        instructionText.text = "✓ Paddle calibrated\n✓ Position calibrated\n\nReady to play!";

        if (messageText != null)
            messageText.text = "Calibration successful!";

        ShowNextButton();

        // Auto-advance after 2 seconds
        CancelInvoke(nameof(InvokeNextStep));
        Invoke(nameof(InvokeNextStep), 2f);
    }

    private void ShowServingDemo()
    {
        titleText.text = "SERVING";
        instructionText.text = "Serve the ball to the opponent.\n\nYour swing motion will control the virtual paddle.\n\nPress Button 1 when ready to try.";

        if (messageText != null)
            messageText.text = "(Demo playing...)";
        HideNextButton();  // Advances on Button 1 press or auto-advance after video
    }

    private void ShowOpponentResponse()
    {
        titleText.text = "AI OPPONENT";
        instructionText.text = "The AI opponent responds to your serve.\n\nIt will hit the ball back, starting a rally.\n\nWatch how your movement affects your view.";

        HideNextButton();  // Auto-advances
    }

    private void ShowMovementDemo()
    {
        titleText.text = "COURT MOVEMENT";
        instructionText.text = "As you move around the court, your perspective shifts.\n\nYour position is tracked by UWB sensors in the ground.\n\nWalk around and play — the rally will continue.";

        if (messageText != null)
            messageText.text = "(Play a quick rally — we'll advance when finished)";
        HideNextButton();  // Auto-advances when rally ends
    }

    private void ShowGameplayExplanation()
    {
        titleText.text = "GAME RULES";
        instructionText.text = "SCORING:\nFirst to 11 points wins a set (must win by 2)\n\n" +
                               "MATCH:\nBest-of-3 sets\n\n" +
                               "GAME MODES:\n" +
                               "• Normal: Full match\n" +
                               "• Tutorial: No scoring (practice)\n" +
                               "• God Mode: Scoring, but never lose";

        ShowNextButton();
    }

    private void ShowReadyToPlay()
    {
        titleText.text = "READY TO PLAY!";
        instructionText.text = "You've completed the tutorial.\n\nPress Button 4 to cycle through game modes and select Full Play mode.\n\nThen press Button 1 to start your first match!";

        if (messageText != null)
            messageText.text = "Good luck!";
        HideNextButton();

        // Add a "Start Normal Mode" button option here if desired
    }

    // ── Button Highlighting ──────────────────────────────────────────────────────

    private void HighlightButton(int buttonNumber)
    {
        if (buttonHighlight != null)
        {
            buttonHighlight.text = $"Press Button {buttonNumber}";

            // Match physical button colors
            buttonHighlight.color = buttonNumber switch
            {
                1 => new Color(1f, 0f, 0f, 1f),           // Red
                2 => new Color(1f, 1f, 0f, 1f),           // Yellow
                3 => new Color(0f, 0.5f, 1f, 1f),         // Blue
                4 => Color.white,                          // White
                _ => Color.gray                            // Default gray for unknown
            };
        }
    }

    private void ClearButtonHighlight()
    {
        if (buttonHighlight != null)
        {
            buttonHighlight.text = "";
        }
    }

    // ── Video Playback ──────────────────────────────────────────────────────────

    private void PlayVideo(VideoClip clip)
    {
        if (videoPlayer == null)
        {
            Debug.LogWarning("[TutorialUI] VideoPlayer not assigned. Cannot play video.");
            return;
        }

        if (clip == null)
        {
            Debug.LogWarning("[TutorialUI] No video clip assigned for this step.");
            return;
        }

        videoPlayer.clip = clip;
        videoPlayer.Play();
        Debug.Log($"[TutorialUI] Playing video: {clip.name}");
    }

    private void StopVideoPlayback()
    {
        if (videoPlayer != null)
        {
            videoPlayer.Stop();
        }
    }

    // ── Button Management ────────────────────────────────────────────────────────

    private void ShowNextButton()
    {
        if (nextButton != null)
            nextButton.gameObject.SetActive(true);
    }

    private void HideNextButton()
    {
        if (nextButton != null)
            nextButton.gameObject.SetActive(false);
    }

    private void OnNextClicked()
    {
        CancelInvoke(nameof(InvokeNextStep));
        tutorialManager?.OnNextButtonClicked();
    }

    private void InvokeNextStep()
    {
        if (tutorialManager == null)
            return;

        tutorialManager.TryAdvanceFromStep(TutorialManager.TutorialStep.CalibrationComplete);
    }

    private void OnSkipClicked()
    {
        Debug.Log("[TutorialUI] Skip clicked");
        // Could skip to end, or allow user to select which mode to enter
    }

    private void OnDestroy()
    {
        CancelInvoke(nameof(InvokeNextStep));

        if (nextButton != null)
            nextButton.onClick.RemoveListener(OnNextClicked);
        if (skipButton != null)
            skipButton.onClick.RemoveListener(OnSkipClicked);

        if (gameState != null)
            gameState.OnModeChanged -= OnModeChanged;
    }
}
