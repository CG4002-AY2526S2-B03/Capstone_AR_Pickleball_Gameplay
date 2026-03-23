/// <summary>
/// The four standard pickleball shot types, matching the ML model's
/// returnSwingType (0-3) defined in the design report Section 5.2.
/// </summary>
public enum ShotType
{
    Drive = 0,   // Fast, low, powerful shot
    Drop  = 1,   // Soft arching shot (lands in kitchen)
    Dink  = 2,   // Soft, controlled short shot
    Lob   = 3    // High arching defensive shot
}
