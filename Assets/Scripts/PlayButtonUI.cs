using UnityEngine;

/// <summary>
/// Legacy play button overlay — functionality moved to RecalibrateUI (Button 1)
/// and GameStateManager.StartOrTogglePause().
/// This component auto-destroys to prevent interference with the new HUD.
/// </summary>
public class PlayButtonUI : MonoBehaviour
{
    private void Awake()
    {
        Destroy(this);
    }
}
