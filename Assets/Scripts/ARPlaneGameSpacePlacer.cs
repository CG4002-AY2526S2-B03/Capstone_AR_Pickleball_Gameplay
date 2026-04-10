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
    [Tooltip("When true, GameSpaceRoot is placed only via PlaceAtAnchor() (QR flow). Plane/tap/fallback placement paths are ignored.")]
    [SerializeField] private bool placeOnlyFromQrAnchor = true;
    [SerializeField] private Vector3 placementOffsetMeters = Vector3.zero;
    [SerializeField] private Vector3 rotationOffsetEuler = Vector3.zero;
    [SerializeField] private bool alignYawToCamera = true;

    /// <summary>When true, only PlaceAtAnchor() can place GameSpaceRoot.</summary>
    public bool PlaceOnlyFromQrAnchor
    {
        get => placeOnlyFromQrAnchor;
        set => placeOnlyFromQrAnchor = value;
    }

    public bool IsPlaced => isPlaced;
    public Transform GameSpaceRoot => gameSpaceRoot;

    [Header("Court Anchor Offset")]
    [Tooltip("Offset applied when placing GameSpaceRoot from the QR anchor. " +
             "If the QR marks the net centre and GameSpaceRoot origin is also the net, " +
             "leave this at zero. If GameSpaceRoot origin is elsewhere, set this so the " +
             "court-local net position lands exactly on the QR position.")]
    [SerializeField] private Vector3 courtAnchorOffset = new Vector3(0f, 0f, 0f);

    /// <summary>The configured court anchor offset used when placing GameSpaceRoot.</summary>
    public Vector3 CourtAnchorOffset => courtAnchorOffset;

    [Header("QR Plane Lock")]
    [Tooltip("When true, the court QR only places the court if a suitable horizontal plane is found under it. The court and gameplay floor are then locked to that plane.")]
    [SerializeField] private bool requireHorizontalPlaneForQrPlacement = true;
    [Tooltip("Maximum vertical gap (meters) allowed between the detected QR pose and the supporting horizontal plane.")]
    [SerializeField] private float qrPlaneMaxVerticalDistance = 0.35f;
    [Tooltip("Maximum horizontal X/Z gap (meters) allowed between the detected QR pose and the supporting horizontal plane center.")]
    [SerializeField] private float qrPlaneMaxPlanarDistance = 3f;

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
    private float _lockedFloorY = float.NaN;
    private bool _hasLockedFloorPlane;
    private TrackableId _lockedFloorPlaneId;

    public bool HasLockedFloorPlane => _hasLockedFloorPlane;
    public float LockedFloorY => _lockedFloorY;

    public bool TryGetLockedFloorY(out float floorY)
    {
        if (_hasLockedFloorPlane)
        {
            floorY = _lockedFloorY;
            return true;
        }

        floorY = float.NaN;
        return false;
    }

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

        if (gameSpaceRoot != null && (hideGameSpaceUntilPlaced || placeOnlyFromQrAnchor))
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
        if (placeOnlyFromQrAnchor)
        {
            return;
        }

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
        if (_hasLockedFloorPlane)
        {
            if (args.updated != null)
            {
                foreach (var plane in args.updated)
                {
                    if (plane != null && plane.trackableId == _lockedFloorPlaneId)
                    {
                        _lockedFloorY = plane.transform.position.y;
                        _floorY = _lockedFloorY;
                        _hasFloorY = true;
                        break;
                    }
                }
            }

            if (args.removed != null)
            {
                foreach (var plane in args.removed)
                {
                    if (plane != null && plane.trackableId == _lockedFloorPlaneId)
                    {
                        Debug.LogWarning($"[GameSpacePlacer] Locked floor plane {_lockedFloorPlaneId} was removed. Keeping last locked Y={_lockedFloorY:F4}.");
                        break;
                    }
                }
            }
        }

        // ── Always store the floor Y from any detected horizontal plane ──
        if (!_hasLockedFloorPlane && args.added != null)
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

        if (placeOnlyFromQrAnchor)
        {
            return;
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

        ARPlane groundPlane = null;
        Vector3 anchorPosition = anchorPose.position;
        if (TryResolveHorizontalPlaneForAnchor(anchorPose.position, out groundPlane, out float lockedFloorY, out float verticalDistance, out float planarDistance))
        {
            anchorPosition.y = lockedFloorY;
            _lockedFloorY = lockedFloorY;
            _hasLockedFloorPlane = true;
            _lockedFloorPlaneId = groundPlane.trackableId;
            _floorY = lockedFloorY;
            _hasFloorY = true;

            Debug.Log($"[GameSpacePlacer] QR snapped to locked floor plane {groundPlane.trackableId}. " +
                      $"qrY={anchorPose.position.y:F4}, planeY={lockedFloorY:F4}, verticalΔ={verticalDistance:F4}, planarΔ={planarDistance:F3}");
        }
        else
        {
            if (requireHorizontalPlaneForQrPlacement)
            {
                Debug.LogWarning("[GameSpacePlacer] Court QR detected but no suitable horizontal plane was found under it. Placement skipped.");
                return;
            }

            if (_hasFloorY)
            {
                anchorPosition.y = _floorY;
                Debug.LogWarning($"[GameSpacePlacer] No suitable plane found under QR. Falling back to stored floor Y={_floorY:F4}.");
            }
            else
            {
                Debug.LogWarning($"[GameSpacePlacer] No suitable plane found under QR. Falling back to raw QR Y={anchorPosition.y:F4}.");
            }

            _hasLockedFloorPlane = false;
        }

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

    private bool TryResolveHorizontalPlaneForAnchor(
        Vector3 qrWorldPosition,
        out ARPlane bestPlane,
        out float lockedFloorY,
        out float bestVerticalDistance,
        out float bestPlanarDistance)
    {
        bestPlane = null;
        lockedFloorY = float.NaN;
        bestVerticalDistance = float.MaxValue;
        bestPlanarDistance = float.MaxValue;

        if (planeManager == null)
            return false;

        float maxVerticalDistance = Mathf.Max(0f, qrPlaneMaxVerticalDistance);
        float maxPlanarDistance = Mathf.Max(0f, qrPlaneMaxPlanarDistance);
        float bestScore = float.MaxValue;
        Vector2 qrXZ = new Vector2(qrWorldPosition.x, qrWorldPosition.z);

        foreach (var plane in planeManager.trackables)
        {
            if (plane == null)
                continue;

            if (plane.alignment != PlaneAlignment.HorizontalUp &&
                plane.alignment != PlaneAlignment.HorizontalDown)
                continue;

            float verticalDistance = Mathf.Abs(plane.transform.position.y - qrWorldPosition.y);
            Vector2 planeXZ = new Vector2(plane.transform.position.x, plane.transform.position.z);
            float planarDistance = Vector2.Distance(planeXZ, qrXZ);

            if (verticalDistance > maxVerticalDistance || planarDistance > maxPlanarDistance)
                continue;

            float score = (verticalDistance * 4f) + planarDistance;
            if (score < bestScore)
            {
                bestScore = score;
                bestPlane = plane;
                lockedFloorY = plane.transform.position.y;
                bestVerticalDistance = verticalDistance;
                bestPlanarDistance = planarDistance;
            }
        }

        return bestPlane != null;
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
        _hasLockedFloorPlane = false;
        _lockedFloorY = float.NaN;
        _lockedFloorPlaneId = default;

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

        if (placeOnlyFromQrAnchor)
        {
            return;
        }

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
