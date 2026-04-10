using UnityEngine;
using System;

/// <summary>
/// Tutorial mode state machine.
/// Manages progression through tutorial steps and validates task completion.
/// Coordinates with GameStateManager for game state and MqttController for button input.
/// </summary>
public class TutorialManager : MonoBehaviour
{
    public enum TutorialStep
    {
        HardwareGuide = 0,              // Learn buttons & UI layout
        PlaceCourtGuide = 1,            // Scan QR code
        PressButtonToCalibrate = 2,     // Stand in center, press Button 2 to calibrate
        CalibrationComplete = 3,        // Show checkmark, auto-advance
        GameplayDemo = 4,               // Combined serving/opponent/movement video demo
        GameplayExplanation = 5,        // Explain points, sets, modes
        ReadyToPlay = 6                 // Switch to Normal mode button
    }

    // ── Singleton ────────────────────────────────────────────────────────────────
    private static TutorialManager _instance;
    public static TutorialManager Instance
    {
        get
        {
            if (_instance == null)
                _instance = FindFirstObjectByType<TutorialManager>();
            return _instance;
        }
    }

    // ── State ────────────────────────────────────────────────────────────────────
    private TutorialStep _currentStep = TutorialStep.HardwareGuide;
    public TutorialStep CurrentStep => _currentStep;
    private bool _isAdvancingStep;


    // ── Component References ─────────────────────────────────────────────────────
    [SerializeField] private GameStateManager gameState;
    [SerializeField] private TutorialUIManager tutorialUI;

    // ── Events ───────────────────────────────────────────────────────────────────
    public event Action<TutorialStep> OnStepChanged;

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
        if (gameState == null)
            gameState = FindFirstObjectByType<GameStateManager>();
        if (tutorialUI == null)
            tutorialUI = FindFirstObjectByType<TutorialUIManager>();

        if (gameState == null)
        {
            Debug.LogError("[Tutorial] GameStateManager not found. Disabling tutorial manager.");
            enabled = false;
            return;
        }

        // Only initialize if in Tutorial mode
        if (gameState.Mode == GameStateManager.GameMode.Tutorial)
        {
            _currentStep = TutorialStep.HardwareGuide;
            ShowCurrentStep();
        }
    }

    // ── Public API ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Called when a hardware button is pressed during tutorial.
    /// Routes the button press to appropriate step handler.
    /// </summary>
    public void ProcessButtonPress(int buttonNumber)
    {
        if (gameState == null || gameState.Mode != GameStateManager.GameMode.Tutorial)
            return;

        Debug.Log($"[Tutorial] Button {buttonNumber} pressed in step {_currentStep}");

        switch (_currentStep)
        {
        case TutorialStep.HardwareGuide:
            if (buttonNumber == 1) {
                EnsureTutorialStarted();
                AdvanceStep();
            }
            else
            {
                tutorialUI?.ShowMessage("Press Button 1 to continue");
            }
            break;

        case TutorialStep.PlaceCourtGuide:
            if (buttonNumber == 1)
            {
                EnsureTutorialStarted();
                tutorialUI?.ShowMessage("Now scan the court QR to place court and racket.");
            }
            else
            {
                tutorialUI?.ShowMessage("Point camera at QR code to place court");
            }
            break;

        case TutorialStep.PressButtonToCalibrate:
            if (buttonNumber == 2)
            {
                EnsureTutorialStarted();
                AdvanceStep();
            }
            else
            {
                tutorialUI?.ShowMessage("Press Button 2 to calibrate");
            }
            break;

        case TutorialStep.GameplayDemo:
            if (buttonNumber == 1) {
                EnsureTutorialStarted();
                AdvanceStep();
            }
            else
            {
                tutorialUI?.ShowMessage("Press Button 1 to continue");
            }
            break;
        case TutorialStep.GameplayExplanation:
            if (buttonNumber == 1) {
                EnsureTutorialStarted();
                AdvanceStep();
            }
            else
            {
                tutorialUI?.ShowMessage("Press Button 1 to continue");
            }
            break;
        case TutorialStep.ReadyToPlay:
            if (buttonNumber == 1)
                RestartTutorial();
            break;
        }
    }

    /// <summary>
    /// Called by UI when user clicks "Next" button.
    /// </summary>
    public void OnNextButtonClicked()
    {
        TryAdvanceFromStep(_currentStep);
    }

    /// <summary>
    /// Advances only if the current step matches <paramref name="expectedStep"/>.
    /// This prevents duplicate progression from overlapping timer and button events.
    /// </summary>
    public bool TryAdvanceFromStep(TutorialStep expectedStep)
    {
        if (_currentStep != expectedStep)
            return false;

        AdvanceStep();
        return true;
    }

    /// <summary>
    /// Called by QR detection when court is placed.
    /// </summary>
    public void OnCourtPlaced()
    {
        if (_currentStep == TutorialStep.PlaceCourtGuide)
        {
            Debug.Log("[Tutorial] Court placed, auto-advancing");
            AdvanceStep();
        }
    }

    /// <summary>
    /// Called when a rally ends (e.g., in MovementDemo).
    /// Auto-advances to next step.
    /// </summary>
    public void OnRallyEnded()
    {
        if (_currentStep == TutorialStep.GameplayDemo)
        {
            Debug.Log("[Tutorial] Rally ended, auto-advancing");
            AdvanceStep();
        }
    }

    public void RestartTutorial()
    {
        Debug.Log("[Tutorial] Restarting tutorial from step 0");
        _currentStep = TutorialStep.HardwareGuide;
        ShowCurrentStep();
    }

    /// <summary>
    /// Switches to specified mode and resets tutorial.
    /// </summary>
    public void ExitTutorialToMode(GameStateManager.GameMode targetMode)
    {
        if (gameState == null)
        {
            Debug.LogWarning("[Tutorial] ExitTutorialToMode ignored: GameStateManager missing.");
            return;
        }

        Debug.Log($"[Tutorial] Exiting to mode: {targetMode}");
        gameState.IsStarted = false;
        gameState.Mode = targetMode;
        gameState.ResetGameplay();
        tutorialUI?.HideAllPanels();
        _currentStep = TutorialStep.HardwareGuide;
    }

    // ── Internal State Management ────────────────────────────────────────────────

    private void EnsureTutorialStarted()
    {
        if (gameState == null || gameState.IsStarted)
            return;

        // Triggers imageTracker.StartGame(), which unlocks QR-based court/racket spawning.
        gameState.StartOrTogglePause();
    }

    public void AdvanceStep()
    {
        if (_isAdvancingStep || _currentStep >= TutorialStep.ReadyToPlay)
            return;

        _isAdvancingStep = true;
        try
        {
            _currentStep++;
            ShowCurrentStep();
        }
        finally
        {
            _isAdvancingStep = false;
        }
    }

    private void ShowCurrentStep()
    {
        Debug.Log($"[Tutorial] Showing step: {_currentStep}");
        OnStepChanged?.Invoke(_currentStep);

        if (tutorialUI != null)
            tutorialUI.ShowStep(_currentStep);
    }
}
