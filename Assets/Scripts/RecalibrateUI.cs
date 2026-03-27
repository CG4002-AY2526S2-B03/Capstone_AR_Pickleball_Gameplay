using UnityEngine;

/// <summary>
/// Formerly the in-game HUD with 4 touchscreen buttons.
/// All button actions are now handled by hardware IMU buttons via
/// MqttController.HandleButtonPacket(), so this script is intentionally
/// a no-op.  Kept alive so existing scene references don't break.
/// </summary>
public class RecalibrateUI : MonoBehaviour
{
    [Header("References (unused — kept for scene compatibility)")]
    [SerializeField] private GameStateManager gameState;
    [SerializeField] private PracticeBallController ballController;
    [SerializeField] private PlaceTrackedImages imageTracker;
}
