using System;
using UnityEngine;

public class BotShotProfile : MonoBehaviour
{
    [Serializable]
    public struct ShotConfig
    {
        public float upForce;
        public float hitForce;
    }

    [Header("ML Shot Types (returnSwingType 0-3)")]
    public ShotConfig drive  = new ShotConfig { upForce = 3f, hitForce = 14f };  // type 0
    public ShotConfig attack = new ShotConfig { upForce = 2f, hitForce = 18f };  // type 1
    public ShotConfig dink   = new ShotConfig { upForce = 5f, hitForce = 6f };   // type 2
    public ShotConfig lob    = new ShotConfig { upForce = 8f, hitForce = 10f };  // type 3

    [Header("Legacy (random bot fallback)")]
    public ShotConfig topSpin;
    public ShotConfig flat;

    public ShotConfig GetShotByType(int returnSwingType)
    {
        switch (returnSwingType)
        {
            case 0: return drive;
            case 1: return attack;
            case 2: return dink;
            case 3: return lob;
            default: return drive;
        }
    }
}
