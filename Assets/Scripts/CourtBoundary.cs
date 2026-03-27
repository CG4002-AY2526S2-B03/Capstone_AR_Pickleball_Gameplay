using UnityEngine;

/// <summary>
/// Identifies a court boundary object for the scoring system.
/// Attach to each wall, the net collider, and the kitchen trigger zone.
///
/// Setup (Inspector):
///   - Walls:   solid collider, set boundaryType to PlayerBackWall / BotBackWall / SideWall
///   - Net:     solid collider, set boundaryType to Net
///   - Kitchen: trigger collider (isTrigger = true), set boundaryType to Kitchen
/// </summary>
public class CourtBoundary : MonoBehaviour
{
    public enum BoundaryType
    {
        PlayerBackWall,
        BotBackWall,
        SideWall,
        Net,
        Kitchen
    }

    [Tooltip("What kind of boundary this object represents.")]
    public BoundaryType boundaryType;
}
