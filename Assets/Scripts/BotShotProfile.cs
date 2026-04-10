using System;
using UnityEngine;

public class BotShotProfile : MonoBehaviour
{
    [Serializable]
    public struct ShotConfig
    {
        [Tooltip("Upward force component (m/s).")]
        public float upForce;
        [Tooltip("Forward force toward the target (m/s).")]
        public float hitForce;
        [Tooltip("Local-space offset from bot centre to where the racquet meets the ball.")]
        public Vector3 racquetOffset;
    }

    [Header("Shot Types (matching ShotType enum / ML returnSwingType 0-5)")]
    [Tooltip("Drive (0): fast, low, powerful shot.")]
    public ShotConfig drive = new ShotConfig { upForce = 3f,  hitForce = 10f, racquetOffset = new Vector3(-0.8f, 0.04f, 0.9f) };
    [Tooltip("Drop (1): soft arching shot that lands in kitchen.")]
    public ShotConfig drop  = new ShotConfig { upForce = 5f,  hitForce = 5f,  racquetOffset = new Vector3(-0.9f, 0.16f, 0.7f) };
    [Tooltip("Dink (2): soft, controlled short shot.")]
    public ShotConfig dink  = new ShotConfig { upForce = 4f,  hitForce = 4f,  racquetOffset = new Vector3(-0.9f, 0.2f, 0.6f) };
    [Tooltip("Lob (3): high arching defensive shot.")]
    public ShotConfig lob   = new ShotConfig { upForce = 9f,  hitForce = 6f,  racquetOffset = new Vector3(-0.8f, 0.32f, 0.8f) };
    [Tooltip("SpeedUp (4): fast aggressive attack.")]
    public ShotConfig speedUp = new ShotConfig { upForce = 2f,  hitForce = 12f, racquetOffset = new Vector3(-1.1f, 0.12f, 1.1f) };
    [Tooltip("HandBattle (5): quick reflex exchange.")]
    public ShotConfig handBattle = new ShotConfig { upForce = 3f,  hitForce = 11f, racquetOffset = new Vector3(-1.2f, 0.2f, 1.1f) };

    /// <summary>
    /// Returns the shot config for the given ShotType enum value (0-5).
    /// </summary>
    public ShotConfig GetShotByType(int returnSwingType)
    {
        switch ((ShotType)returnSwingType)
        {
            case ShotType.Drive:      return drive;
            case ShotType.Drop:       return drop;
            case ShotType.Dink:       return dink;
            case ShotType.Lob:        return lob;
            case ShotType.SpeedUp:    return speedUp;
            case ShotType.HandBattle: return handBattle;
            default:                  return drive;
        }
    }

    public ShotConfig GetShotByType(ShotType type)
    {
        return GetShotByType((int)type);
    }
}
