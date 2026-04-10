using UnityEngine;

/// <summary>
/// Automatically configures court boundaries, net, and kitchen zone at runtime.
/// Runs in Awake() so CourtBoundary components are ready before other scripts' Start().
///
/// Setup: Add this component to GameFlowManager (or any persistent GameObject).
///        Set gameSpaceRoot in Inspector, or leave null to auto-find.
///        Adjust net/kitchen positions in Inspector to match your court layout.
/// </summary>
public class CourtBoundarySetup : MonoBehaviour
{
    [Header("Court Reference")]
    [Tooltip("The GameSpaceRoot transform. Auto-found by name if null.")]
    public Transform gameSpaceRoot;

    [Header("Net (solid collider — ball bounces off)")]
    [Tooltip("Local position relative to GameSpaceRoot.")]
    public Vector3 netLocalPosition = new Vector3(0f, 0.3f, 5.4f);
    [Tooltip("BoxCollider size for the net.")]
    public Vector3 netSize = new Vector3(8f, 0.6f, 0.05f);

    [Header("Kitchen / Non-Volley Zone (trigger — detects paddle entry)")]
    [Tooltip("Local position relative to GameSpaceRoot.")]
    public Vector3 kitchenLocalPosition = new Vector3(0f, 0.5f, 4.3f);
    [Tooltip("BoxCollider size for the kitchen zone.")]
    public Vector3 kitchenSize = new Vector3(8f, 1.5f, 2.2f);

    private void Awake()
    {
        if (gameSpaceRoot == null)
        {
            var go = GameObject.Find("GameSpaceRoot");
            if (go != null) gameSpaceRoot = go.transform;
        }

        if (gameSpaceRoot == null)
        {
            Debug.LogWarning("[CourtBoundarySetup] GameSpaceRoot not found — skipping setup.");
            return;
        }

        TagExistingWalls();
        CreateNet();
        CreateKitchenZone();

        Debug.Log("[CourtBoundarySetup] Court boundaries configured.");
    }

    // ── Tag the four existing walls ──────────────────────────────────────────

    private void TagExistingWalls()
    {
        Transform walls = gameSpaceRoot.Find("walls");
        if (walls == null)
        {
            Debug.LogWarning("[CourtBoundarySetup] 'walls' container not found.");
            return;
        }

        // wall (3) at Z = -17  → behind the player
        AddBoundary(walls, "wall (3)", CourtBoundary.BoundaryType.PlayerBackWall);

        // wall (2) at Z = 12.2 → behind the bot
        AddBoundary(walls, "wall (2)", CourtBoundary.BoundaryType.BotBackWall);

        // wall    at X =  2.1  → right side wall
        AddBoundary(walls, "wall",     CourtBoundary.BoundaryType.SideWall);

        // wall (1) at X = -10.8 → left side wall
        AddBoundary(walls, "wall (1)", CourtBoundary.BoundaryType.SideWall);
    }

    private void AddBoundary(Transform parent, string childName, CourtBoundary.BoundaryType type)
    {
        Transform child = parent.Find(childName);
        if (child == null)
        {
            Debug.LogWarning($"[CourtBoundarySetup] '{childName}' not found under '{parent.name}'.");
            return;
        }

        var boundary = child.GetComponent<CourtBoundary>();
        if (boundary == null)
            boundary = child.gameObject.AddComponent<CourtBoundary>();
        boundary.boundaryType = type;
    }

    // ── Create Net ───────────────────────────────────────────────────────────

    private void CreateNet()
    {
        if (gameSpaceRoot.Find("Net") != null) return;

        var netGO = new GameObject("Net");
        netGO.transform.SetParent(gameSpaceRoot, false);
        netGO.transform.localPosition = netLocalPosition;
        netGO.transform.localRotation = Quaternion.identity;

        var col = netGO.AddComponent<BoxCollider>();
        col.size = netSize;
        col.isTrigger = false;

        var boundary = netGO.AddComponent<CourtBoundary>();
        boundary.boundaryType = CourtBoundary.BoundaryType.Net;
    }

    // ── Create Kitchen (Non-Volley Zone) ─────────────────────────────────────

    private void CreateKitchenZone()
    {
        if (gameSpaceRoot.Find("KitchenZone") != null) return;

        var kitchenGO = new GameObject("KitchenZone");
        kitchenGO.transform.SetParent(gameSpaceRoot, false);
        kitchenGO.transform.localPosition = kitchenLocalPosition;
        kitchenGO.transform.localRotation = Quaternion.identity;

        var col = kitchenGO.AddComponent<BoxCollider>();
        col.size = kitchenSize;
        col.isTrigger = true;

        var boundary = kitchenGO.AddComponent<CourtBoundary>();
        boundary.boundaryType = CourtBoundary.BoundaryType.Kitchen;
    }
}
