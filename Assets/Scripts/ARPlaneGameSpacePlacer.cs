using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class ARPlaneGameSpacePlacer : MonoBehaviour
{
    [Header("Game Space")]
    [Tooltip("Root object that contains the whole game world (court, bots, ball spawners, etc.).")]
    [SerializeField] private Transform gameSpaceRoot;

    [SerializeField] private bool hideGameSpaceUntilPlaced = true;

    [Header("AR References")]
    [SerializeField] private ARPlaneManager planeManager;
    [SerializeField] private ARRaycastManager raycastManager;
    [SerializeField] private Camera arCamera;

    [Header("Placement")]
    [SerializeField] private bool autoPlaceOnFirstDetectedPlane = true;
    [Tooltip("When true, placement is deferred until AllowPlacement() is called " +
             "(e.g. by the PlaceTrackedImages.onFirstImageDetected event). " +
             "Planes are still detected in the background so a surface is ready. " +
             "Keep this TRUE when using QR-code court placement so a detected floor " +
             "plane does not spawn the court before the QR is scanned.")]
    [SerializeField] private bool waitForExternalTrigger = true;
    [SerializeField] private bool requireTapToPlace = false;
    [SerializeField] private bool allowRepositionAfterPlacement = false;
    [SerializeField] private Vector3 placementOffsetMeters = Vector3.zero;
    [SerializeField] private Vector3 rotationOffsetEuler = Vector3.zero;
    [SerializeField] private bool alignYawToCamera = true;

    [Header("Court Anchor Offset")]
    [Tooltip("The QR anchor marks the net centre, but GameSpaceRoot origin is at the " +
             "player-side baseline. This offset shifts the root so that the net " +
             "(at netZ in court-local space) lands exactly on the QR position. " +
             "Set Z = −netLocalPosition.z (e.g. −5.4 for a net at z=5.4).")]
    [SerializeField] private Vector3 courtAnchorOffset = new Vector3(0f, 0f, -5.4f);

    /// <summary>The configured court anchor offset used when placing GameSpaceRoot.</summary>
    public Vector3 CourtAnchorOffset => courtAnchorOffset;

    [Header("Camera Height")]
    [Tooltip("Assumed player eye-height in metres. Used by the fallback " +
             "(no-plane) path to place the court this far below the camera.")]
    [SerializeField] private float playerHeight = 1.7f;

    [Header("After Placement")]
    [SerializeField] private bool disablePlaneDetectionAfterPlacement = true;
    [SerializeField] private bool disablePlaneVisualsAfterPlacement = true;

    private static readonly List<ARRaycastHit> RaycastHits = new List<ARRaycastHit>();

    private bool isPlaced;
    private bool isAllowed;        // external trigger received
    private Pose? pendingPlanePose; // best plane pose stored while waiting

    // ARAnchor used to pin the court to physical space (prevents drift)
    private ARAnchorManager _anchorManager;
    private GameObject _anchorGO;

    // ── Floor Y from the first detected plane ──
    private float _floorY = float.NaN;
    private bool _hasFloorY;

    private void Awake()
    {
        ResolveReferences();

        // Ensure an ARAnchorManager exists on the XR Origin so we can create
        // world-locked anchors that won't drift with ARCore coordinate shifts.
        _anchorManager = GetComponentInParent<ARAnchorManager>();
        if (_anchorManager == null)
            _anchorManager = FindFirstObjectByType<ARAnchorManager>();
        if (_anchorManager == null)
        {
            // Add to same GO as XROrigin (which is typically our parent or self)
            var xrOrigin = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (xrOrigin != null)
                _anchorManager = xrOrigin.gameObject.AddComponent<ARAnchorManager>();
            else
                _anchorManager = gameObject.AddComponent<ARAnchorManager>();
            Debug.Log("[ARPlaneGameSpacePlacer] Added ARAnchorManager at runtime.");
        }

        if (gameSpaceRoot != null && hideGameSpaceUntilPlaced)
        {
            gameSpaceRoot.gameObject.SetActive(false);
        }
    }

    private void OnEnable()
    {
        if (planeManager != null)
        {
            planeManager.planesChanged += OnPlanesChanged;
        }
    }

    private void OnDisable()
    {
        if (planeManager != null)
        {
            planeManager.planesChanged -= OnPlanesChanged;
        }
    }

    private void Update()
    {
        if (!requireTapToPlace)
        {
            return;
        }

        if (isPlaced && !allowRepositionAfterPlacement)
        {
            return;
        }

        if (Input.touchCount == 0)
        {
            return;
        }

        Touch touch = Input.GetTouch(0);
        if (touch.phase != TouchPhase.Began)
        {
            return;
        }

        if (raycastManager == null)
        {
            return;
        }

        if (raycastManager.Raycast(touch.position, RaycastHits, TrackableType.PlaneWithinPolygon))
        {
            Pose hitPose = RaycastHits[0].pose;
            PlaceGameSpace(hitPose.position, hitPose.rotation);
        }
    }

    private void OnPlanesChanged(ARPlanesChangedEventArgs args)
    {
        // ── Always store the floor Y from any detected horizontal plane ──
        if (args.added != null)
        {
            foreach (var plane in args.added)
            {
                if (plane.alignment == PlaneAlignment.HorizontalUp ||
                    plane.alignment == PlaneAlignment.HorizontalDown)
                {
                    _floorY = plane.transform.position.y;
                    _hasFloorY = true;
                    Debug.Log($"[GameSpacePlacer] Floor Y stored: {_floorY:F4} from plane {plane.trackableId}");
                    break;
                }
            }
        }

        if (!autoPlaceOnFirstDetectedPlane || requireTapToPlace)
        {
            return;
        }

        if (isPlaced && !allowRepositionAfterPlacement)
        {
            return;
        }

        if (args.added == null || args.added.Count == 0)
        {
            return;
        }

        ARPlane firstPlane = args.added[0];
        Pose planePose = new Pose(firstPlane.transform.position, firstPlane.transform.rotation);

        // If we must wait for an external trigger (e.g. image detection), store the pose
        if (waitForExternalTrigger && !isAllowed)
        {
            pendingPlanePose = planePose;
            return;
        }

        PlaceGameSpace(planePose.position, planePose.rotation);
    }

    // ────────────────────────────────────────────────────────────
    //  Raycast hit list (reused to avoid GC)
    // ────────────────────────────────────────────────────────────
    private static readonly List<ARRaycastHit> s_PlaneHits = new List<ARRaycastHit>();

    /// <summary>
    /// Called by PlaceTrackedImages when the court anchor QR code is detected.
    ///
    /// Simple logic:
    ///   1. Plane A detected → we stored its Y (floor level)
    ///   2. QR detected → use QR's X/Z, force Y = floor level
    ///   3. Attach anchor to plane at that position
    ///   4. GameSpaceRoot is placed directly on the anchor, no Y offset
    ///
    /// The court sits exactly on the ground. Period.
    /// </summary>
    public void PlaceAtAnchor(Pose anchorPose)
    {
        // QR-based placement always wins — tear down any prior plane-based placement
        // so a floor plane detected before the QR scan does not block this path.
        if (isPlaced)
        {
            DestroyAnchor();
            isPlaced = false;
        }

        // ── 1. Get the floor Y from the detected plane ──
        float groundY;
        ARPlane groundPlane = null;

        if (_hasFloorY)
        {
            groundY = _floorY;
            Debug.Log($"[GameSpacePlacer] Using stored floor Y: {groundY:F4}");
        }
        else
        {
            // If somehow no plane Y stored yet, try to get it from planeManager
            groundPlane = GetClosestHorizontalPlane(anchorPose.position);
            if (groundPlane != null)
            {
                groundY = groundPlane.transform.position.y;
                Debug.Log($"[GameSpacePlacer] Using plane Y: {groundY:F4}");
            }
            else
            {
                // Last resort: use QR Y (may float)
                groundY = anchorPose.position.y;
                Debug.LogWarning($"[GameSpacePlacer] No plane found! Using QR Y: {groundY:F4} — court may float.");
            }
        }

        // ── 2. QR gives X/Z, plane gives Y ──
        Vector3 anchorPosition = new Vector3(
            anchorPose.position.x,
            groundY,                 // FORCE to floor level
            anchorPose.position.z
        );

        // ── 3. Derive court yaw from camera → QR direction ──────────────────────
        // The player always scans from the player's side, so the vector from the
        // camera to the QR anchor = from player baseline toward the net = court +Z.
        // This is robust regardless of how ARFoundation orients the QR image axes
        // (which varies between ARKit/ARCore and QR mounting angle).
        Vector3 fwd;
        if (arCamera != null)
        {
            fwd = anchorPosition - arCamera.transform.position;
        }
        else
        {
            // Fallback: use QR forward if no camera reference is available.
            fwd = anchorPose.rotation * Vector3.forward;
        }
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
        Quaternion yaw = Quaternion.LookRotation(fwd.normalized, Vector3.up);

        Pose floorPose = new Pose(anchorPosition, yaw);

        // ── 4. Create/attach anchor ──
        DestroyAnchor();

        // Try to find the plane under the anchor for a plane-attached anchor
        if (groundPlane == null)
            groundPlane = GetClosestHorizontalPlane(anchorPosition);

        if (groundPlane != null && _anchorManager != null)
        {
            ARAnchor a = _anchorManager.AttachAnchor(groundPlane, floorPose);
            if (a != null)
            {
                _anchorGO = a.gameObject;
                Debug.Log($"[GameSpacePlacer] PLANE-ATTACHED anchor at {anchorPosition} on plane {groundPlane.trackableId}");
            }
            else
            {
                CreateFreeAnchor(floorPose);
            }
        }
        else
        {
            CreateFreeAnchor(floorPose);
        }

        // ── 5. Position GameSpaceRoot relative to anchor ──
        // The QR anchor marks the net centre, but GameSpaceRoot origin is at the
        // player-side baseline. Apply the configured offset exactly as authored.
        //
        // IMPORTANT: We do NOT parent GameSpaceRoot under the anchor, because
        // ARFoundation may destroy the anchor GameObject at any time (tracking
        // loss, plane merges, etc.) — which would destroy the entire game world.
        // Instead we follow the anchor's pose each frame via AnchorFollower.
        Vector3 effectiveCourtOffset = courtAnchorOffset;

        // Un-parent from any previous anchor first
        if (gameSpaceRoot.parent != null)
            gameSpaceRoot.SetParent(null, true);

        // Position the court relative to the anchor
        Vector3 anchorWorldPos = _anchorGO.transform.position;
        Quaternion anchorWorldRot = _anchorGO.transform.rotation;
        gameSpaceRoot.SetPositionAndRotation(
            anchorWorldPos + anchorWorldRot * effectiveCourtOffset,
            anchorWorldRot);

        // Attach a follower that syncs the court to the anchor each frame
        // (provides drift correction without the risk of destruction).
        var follower = gameSpaceRoot.GetComponent<AnchorFollower>();
        if (follower == null)
            follower = gameSpaceRoot.gameObject.AddComponent<AnchorFollower>();
        follower.SetAnchor(_anchorGO.transform, effectiveCourtOffset);

        if (!gameSpaceRoot.gameObject.activeSelf)
            gameSpaceRoot.gameObject.SetActive(true);

        isPlaced = true;

        if (disablePlaneVisualsAfterPlacement)
            SetPlaneVisualsEnabled(false);

        if (disablePlaneDetectionAfterPlacement && planeManager != null)
            planeManager.enabled = false;

        Debug.Log($"[GameSpacePlacer] Court placed. Anchor pos={anchorPosition}, " +
                  $"courtOffset={effectiveCourtOffset}, " +
                  $"GameSpaceRoot world={gameSpaceRoot.position}");
    }

    /// <summary>
    /// Finds the closest horizontal AR plane to the given position.
    /// </summary>
    private ARPlane GetClosestHorizontalPlane(Vector3 worldPos)
    {
        if (planeManager == null) return null;

        ARPlane best = null;
        float bestDist = float.MaxValue;

        foreach (var plane in planeManager.trackables)
        {
            if (plane.alignment != PlaneAlignment.HorizontalUp &&
                plane.alignment != PlaneAlignment.HorizontalDown)
                continue;

            float dist = Mathf.Abs(plane.transform.position.y - worldPos.y);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = plane;
            }
        }
        return best;
    }

    /// <summary>
    /// Fallback: creates a free-floating ARAnchor (not attached to any plane).
    /// </summary>
    private void CreateFreeAnchor(Pose pose)
    {
        _anchorGO = new GameObject("CourtWorldAnchor_Free");
        _anchorGO.transform.SetPositionAndRotation(pose.position, pose.rotation);
        _anchorGO.AddComponent<ARAnchor>();
        Debug.LogWarning($"[GameSpacePlacer] Free-floating anchor at {pose.position} (no plane)");
    }

    /// <summary>
    /// Destroys the current ARAnchor (used during recalibration).
    /// </summary>
    private void DestroyAnchor()
    {
        if (_anchorGO != null)
        {
            // Clear the follower's anchor reference so it stops tracking
            if (gameSpaceRoot != null)
            {
                var follower = gameSpaceRoot.GetComponent<AnchorFollower>();
                if (follower != null)
                    follower.SetAnchor(null, Vector3.zero);
            }
            Destroy(_anchorGO);
            _anchorGO = null;
        }
    }

    /// <summary>
    /// Public reset that allows the court to be re-placed on next QR detection.
    /// Called by RecalibrateUI.
    /// </summary>
    public void ResetPlacement()
    {
        DestroyAnchor();
        isPlaced = false;
        isAllowed = false;
        pendingPlanePose = null;

        if (gameSpaceRoot != null && hideGameSpaceUntilPlaced)
            gameSpaceRoot.gameObject.SetActive(false);

        // Re-enable plane detection so the anchor subsystem is active
        if (planeManager != null)
            planeManager.enabled = true;

        Debug.Log("[ARPlaneGameSpacePlacer] Placement reset — ready for re-anchor.");
    }

    /// <summary>
    /// Call this from PlaceTrackedImages.onFirstImageDetected (or any other trigger)
    /// to allow the game space to be placed. If a plane was already detected,
    /// placement happens immediately; otherwise it happens on the next plane detection.
    /// </summary>
    public void AllowPlacement()
    {
        isAllowed = true;

        // If a plane was already found while we were waiting, place now
        if (pendingPlanePose.HasValue && !isPlaced)
        {
            PlaceGameSpace(pendingPlanePose.Value.position, pendingPlanePose.Value.rotation);
        }
        // If no plane yet but we want instant feedback, fall back to camera-forward on the floor
        else if (!isPlaced && arCamera != null)
        {
            // Place GameSpaceRoot directly beneath the camera (offset handles court positioning)
            Vector3 fallbackPos = arCamera.transform.position;
            fallbackPos.y = arCamera.transform.position.y - playerHeight; // floor level
            PlaceGameSpace(fallbackPos, Quaternion.identity);
        }
    }

    /// <summary>
    /// Plane-based placement: computes final rotation (camera-aligned or plane-based)
    /// and applies offsets, then calls FinalizePlace.
    /// </summary>
    private void PlaceGameSpace(Vector3 planePosition, Quaternion planeRotation)
    {
        if (gameSpaceRoot == null)
        {
            return;
        }

        Quaternion targetRotation;
        if (alignYawToCamera && arCamera != null)
        {
            Vector3 cameraForward = arCamera.transform.forward;
            cameraForward.y = 0f;
            if (cameraForward.sqrMagnitude < 0.0001f)
            {
                cameraForward = Vector3.forward;
            }

            targetRotation = Quaternion.LookRotation(cameraForward.normalized, Vector3.up);
        }
        else
        {
            targetRotation = planeRotation;
        }

        targetRotation *= Quaternion.Euler(rotationOffsetEuler);

        Vector3 targetPosition = planePosition + targetRotation * placementOffsetMeters;
        FinalizePlace(targetPosition, targetRotation);
    }

    /// <summary>
    /// Shared final step: actually moves GameSpaceRoot, activates it, and disables
    /// plane detection / visuals if configured.
    /// </summary>
    private void FinalizePlace(Vector3 finalPosition, Quaternion finalRotation)
    {
        if (gameSpaceRoot == null) return;

        gameSpaceRoot.SetPositionAndRotation(finalPosition, finalRotation);

        if (!gameSpaceRoot.gameObject.activeSelf)
        {
            gameSpaceRoot.gameObject.SetActive(true);
        }

        isPlaced = true;

        if (disablePlaneVisualsAfterPlacement)
        {
            SetPlaneVisualsEnabled(false);
        }

        if (disablePlaneDetectionAfterPlacement && planeManager != null)
        {
            planeManager.enabled = false;
        }
    }

    private void ResolveReferences()
    {
        if (planeManager == null)
        {
            planeManager = FindFirstObjectByType<ARPlaneManager>();
        }

        if (raycastManager == null)
        {
            raycastManager = FindFirstObjectByType<ARRaycastManager>();
        }

        if (arCamera == null)
        {
            arCamera = Camera.main;
        }
    }

    private void SetPlaneVisualsEnabled(bool isEnabled)
    {
        if (planeManager == null)
        {
            return;
        }

        foreach (ARPlane plane in planeManager.trackables)
        {
            MeshRenderer meshRenderer = plane.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                meshRenderer.enabled = isEnabled;
            }

            LineRenderer lineRenderer = plane.GetComponent<LineRenderer>();
            if (lineRenderer != null)
            {
                lineRenderer.enabled = isEnabled;
            }
        }
    }
}
