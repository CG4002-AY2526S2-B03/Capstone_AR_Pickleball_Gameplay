/// <summary>
/// Pickleball shot types matching the ML model's returnSwingType (0-5).
/// The AI has 6 classes; SpeedUp and HandBattle play like Drive for bot animation.
/// </summary>
public enum ShotType
{
    Drive      = 0,   // Fast, low, powerful shot
    Drop       = 1,   // Soft arching shot (lands in kitchen)
    Dink       = 2,   // Soft, controlled short shot
    Lob        = 3,   // High arching defensive shot
    SpeedUp    = 4,   // Fast attack — plays like Drive
    HandBattle = 5    // Quick reflex — plays like Drive
}
