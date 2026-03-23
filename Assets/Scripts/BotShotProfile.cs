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
    }

    [Header("Shot Types (matching ShotType enum / ML returnSwingType 0-3)")]
    [Tooltip("Drive (0): fast, low, powerful shot.")]
    public ShotConfig drive = new ShotConfig { upForce = 3f,  hitForce = 14f };
    [Tooltip("Drop (1): soft arching shot that lands in kitchen.")]
    public ShotConfig drop  = new ShotConfig { upForce = 5f,  hitForce = 7f };
    [Tooltip("Dink (2): soft, controlled short shot.")]
    public ShotConfig dink  = new ShotConfig { upForce = 4f,  hitForce = 5f };
    [Tooltip("Lob (3): high arching defensive shot.")]
    public ShotConfig lob   = new ShotConfig { upForce = 9f,  hitForce = 8f };

    /// <summary>
    /// Returns the shot config for the given ShotType enum value (0-3).
    /// </summary>
    public ShotConfig GetShotByType(int returnSwingType)
    {
        switch ((ShotType)returnSwingType)
        {
            case ShotType.Drive: return drive;
            case ShotType.Drop:  return drop;
            case ShotType.Dink:  return dink;
            case ShotType.Lob:   return lob;
            default:             return drive;
        }
    }

    public ShotConfig GetShotByType(ShotType type)
    {
        return GetShotByType((int)type);
    }
}
