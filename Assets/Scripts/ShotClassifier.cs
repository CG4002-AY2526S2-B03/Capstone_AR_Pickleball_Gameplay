using UnityEngine;

/// <summary>
/// Classifies a player's hit into one of the four standard pickleball shot types
/// based on observable physics at the moment of impact.
///
/// Classification uses two inputs:
///   1. Paddle speed (magnitude of paddle velocity at contact point)
///   2. Launch angle (angle of the resulting ball velocity above the horizontal)
///
/// Thresholds are exposed in the Inspector so they can be tuned during hardware testing.
/// </summary>
public static class ShotClassifier
{
    // ── Speed thresholds (m/s) ──────────────────────────────────────────────
    // These match realistic pickleball swing speeds:
    //   Dink/Drop:  gentle push   < 4 m/s
    //   Drive:      fast flat hit  > 8 m/s
    //   Lob:        medium speed but steep upward angle

    private const float DinkSpeedCeiling  = 4f;   // below this = soft shot
    private const float DriveSpeedFloor   = 8f;    // above this = power shot

    // ── Angle thresholds (degrees above horizontal) ─────────────────────────
    private const float LobAngleFloor     = 45f;   // steep upward = lob
    private const float DropAngleLow      = 20f;   // moderate arc = drop
    private const float DriveAngleCeiling = 20f;    // flat trajectory = drive

    /// <summary>
    /// Classify a player hit into a ShotType.
    /// </summary>
    /// <param name="paddleSpeed">Paddle contact-point speed in m/s.</param>
    /// <param name="ballVelocity">Ball velocity immediately after the hit.</param>
    public static ShotType Classify(float paddleSpeed, Vector3 ballVelocity)
    {
        // Launch angle: angle between the ball's velocity and the horizontal plane.
        float horizSpeed = new Vector2(ballVelocity.x, ballVelocity.z).magnitude;
        float launchAngle = Mathf.Atan2(ballVelocity.y, horizSpeed) * Mathf.Rad2Deg;

        // ── Decision tree ───────────────────────────────────────────────────
        //
        //  High angle (>45°)  → Lob   (regardless of speed)
        //  Fast + flat        → Drive
        //  Slow + angled up   → Dink  (soft, short)
        //  Medium + angled up → Drop  (soft arc into kitchen)

        if (launchAngle >= LobAngleFloor)
            return ShotType.Lob;

        if (paddleSpeed >= DriveSpeedFloor && launchAngle < DriveAngleCeiling)
            return ShotType.Drive;

        if (paddleSpeed < DinkSpeedCeiling)
            return ShotType.Dink;

        // Medium speed, moderate upward angle → Drop
        if (launchAngle >= DropAngleLow)
            return ShotType.Drop;

        // Default: anything else is a drive
        return ShotType.Drive;
    }
}
