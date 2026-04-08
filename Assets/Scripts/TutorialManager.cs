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
        PressButtonToCalibrate = 2,     // Button 1 press intro
        CalibrationStep1_Paddle = 3,    // Align paddle orientation, press Button 2
        CalibrationStep2_Position = 4,  // Stand in court center (UWB), press Button 2
        CalibrationComplete = 5,        // Show checkmark, auto-advance
        ServingDemo = 6,                // Video demo of serve
        OpponentResponse = 7,           // Bot hits back (auto-advance)
        MovementDemo = 8,               // Walk around court, view shifts
        GameplayExplanation = 9,        // Explain points, sets, modes
        ReadyToPlay = 10                // Switch to Normal mode button
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

    private bool _calibrationPaddleDone = false;
    private bool _calibrationPositionDone = false;

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
        if (gameState.Mode != GameStateManager.GameMode.Tutorial)
            return;

        Debug.Log($"[Tutorial] Button {buttonNumber} pressed in step {_currentStep}");

        switch (_currentStep)
        {
            case TutorialStep.HardwareGuide:
                // Button should not advance this step; user must click UI "Next"
                tutorialUI.ShowMessage("Click 'Next' button to continue");
                break;

            case TutorialStep.PlaceCourtGuide:
                // Auto-advances when QR detected, button press does nothing
                tutorialUI.ShowMessage("Point camera at QR code to place court");
                break;

            case TutorialStep.PressButtonToCalibrate:
                if (buttonNumber == 1)
                {
                    // Button 1 confirms calibration start
                    gameState.IsStarted = true;
                    AdvanceStep();
                }
                break;

            case TutorialStep.CalibrationStep1_Paddle:
                if (buttonNumber == 2)
                {
                    _calibrationPaddleDone = true;
                    AdvanceStep();
                }
                break;

            case TutorialStep.CalibrationStep2_Position:
                if (buttonNumber == 2)
                {
                    _calibrationPositionDone = true;
                    AdvanceStep();
                }
                break;

            case TutorialStep.ServingDemo:
            case TutorialStep.OpponentResponse:
            case TutorialStep.MovementDemo:
            case TutorialStep.GameplayExplanation:
            case TutorialStep.ReadyToPlay:
                // These steps handle button input via UI callbacks, not direct button presses
                break;
        }
    }

    /// <summary>
    /// Called by UI when user clicks "Next" button.
    /// </summary>
    public void OnNextButtonClicked()
    {
        AdvanceStep();
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
        if (_currentStep == TutorialStep.MovementDemo)
        {
            Debug.Log("[Tutorial] Rally ended, auto-advancing");
            AdvanceStep();
        }
    }

    /// <summary>
    /// Switches to specified mode and resets tutorial.
    /// </summary>
    public void ExitTutorialToMode(GameStateManager.GameMode targetMode)
    {
        Debug.Log($"[Tutorial] Exiting to mode: {targetMode}");
        gameState.IsStarted = false;
        gameState.Mode = targetMode;
        gameState.ResetGameplay();
        tutorialUI.HideAllPanels();
        _currentStep = TutorialStep.HardwareGuide;
        _calibrationPaddleDone = false;
        _calibrationPositionDone = false;
    }

    // ── Internal State Management ────────────────────────────────────────────────

    public void AdvanceStep()
    {
        if (_currentStep < TutorialStep.ReadyToPlay)
        {
            _currentStep++;
            ShowCurrentStep();
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
