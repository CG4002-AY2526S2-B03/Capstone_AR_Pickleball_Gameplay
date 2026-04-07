using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

[RequireComponent(typeof(ARTrackedImageManager))]
public class PlaceTrackedImages : MonoBehaviour
{
    // Reference to AR tracked image manager component
    private ARTrackedImageManager _trackedImagesManager;

    // List of prefabs to instantiate - these should be named the same
    // as their corresponding 2D images in the reference image library
    public GameObject[] ArPrefabs;

    [Header("Court Placement")]
    [Tooltip("The ARPlaneGameSpacePlacer that positions the court. " +
             "Leave null to auto-find at runtime.")]
    public ARPlaneGameSpacePlacer gamePlacer;

    [Tooltip("Name of the reference image that represents the court anchor QR. " +
             "Must match the name in the AR Reference Image Library.")]
    public string courtAnchorImageName = "court_anchor";

    [Header("Paddle QR (Dual-Sided)")]
    [Tooltip("Reference image name for the front paddle QR. " +
             "Must match the name in the AR Reference Image Library and the ArPrefabs entry.")]
    public string paddleFrontImageName = "Racket_PickleBall4";

    [Tooltip("Reference image name for the mirrored back paddle QR. " +
             "Leave empty to disable back-side tracking.")]
    public string paddleBackImageName = "Racket_Pickleball4_back";

    private bool _courtPlaced;

    /// <summary>
    /// Gate flag — when false, image detections are ignored.
    /// Set to true by <see cref="StartGame"/> (called from PlayButtonUI).
    /// </summary>
    private bool _gameStarted;

    // Cached reference to the physics paddle for tracking-state updates
    private PaddleHitController _cachedPaddle;

    // The single visual paddle instance (shared between front/back QR)
    private GameObject _paddleInstance;
    private Quaternion _paddlePrefabRot;

    // 180° flip around roll (Z) axis for back-side QR
    private static readonly Quaternion BackFaceFlip = Quaternion.Euler(0f, 0f, 180f);

    // Keep dictionary of created prefabs for non-paddle tracked images
    private readonly Dictionary<string, GameObject> _instantiatedPrefabs = new Dictionary<string, GameObject>();
    private readonly Dictionary<string, Quaternion> _prefabRotOffsets = new Dictionary<string, Quaternion>();

    void Awake()
    {
        _trackedImagesManager = GetComponent<ARTrackedImageManager>();

        if (gamePlacer == null)
            gamePlacer = FindFirstObjectByType<ARPlaneGameSpacePlacer>();
    }

    void OnEnable()
    {
        _trackedImagesManager.trackedImagesChanged += OnTrackedImagesChanged;
    }

    void OnDisable()
    {
        _trackedImagesManager.trackedImagesChanged -= OnTrackedImagesChanged;
    }

    // ── Public Reset API ────────────────────────────────────────────

    /// <summary>
    /// Destroys the spawned paddle so the next QR detection will re-spawn it.
    /// </summary>
    public void ResetRacket()
    {
        var paddle = FindFirstObjectByType<PaddleHitController>();
        if (paddle != null)
            paddle.qrTrackedRacket = null;

        if (_paddleInstance != null)
        {
            Destroy(_paddleInstance);
            _paddleInstance = null;
        }
        _cachedPaddle = null;

        // Also destroy any non-paddle tracked prefabs
        foreach (var kvp in _instantiatedPrefabs)
        {
            if (kvp.Value != null) Destroy(kvp.Value);
        }
        _instantiatedPrefabs.Clear();
        _prefabRotOffsets.Clear();

        Debug.Log("[PlaceTrackedImages] Racket prefabs cleared — scan QR again.");
    }

    private void OnTrackedImagesChanged(ARTrackedImagesChangedEventArgs eventArgs)
    {
        ProcessTrackedImages(eventArgs.added);
        ProcessTrackedImages(eventArgs.updated);
    }

    /// <summary>
    /// Called by PlayButtonUI when the player taps Start.
    /// Unlocks court / racket spawning from image tracking.
    /// </summary>
    public void StartGame()
    {
        _gameStarted = true;
        Debug.Log("[PlaceTrackedImages] Game started — image tracking unlocked.");
    }

    private bool IsPaddleFront(string imageName)
    {
        return !string.IsNullOrEmpty(paddleFrontImageName)
            && string.Compare(imageName, paddleFrontImageName, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private bool IsPaddleBack(string imageName)
    {
        return !string.IsNullOrEmpty(paddleBackImageName)
            && string.Compare(imageName, paddleBackImageName, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private void ProcessTrackedImages(IReadOnlyList<ARTrackedImage> images)
    {
        if (images == null) return;
        if (!_gameStarted) return;

        foreach (var trackedImage in images)
        {
            var imageName = trackedImage.referenceImage.name;

            // ── Court anchor detection → place GameSpaceRoot ──────────────
            if (!_courtPlaced
                && string.Compare(imageName, courtAnchorImageName, StringComparison.OrdinalIgnoreCase) == 0
                && trackedImage.trackingState == TrackingState.Tracking)
            {
                if (gamePlacer != null)
                {
                    Pose anchorPose = new Pose(trackedImage.transform.position,
                                               trackedImage.transform.rotation);
                    gamePlacer.PlaceAtAnchor(anchorPose);
                    _courtPlaced = true;
                    Debug.Log($"[PlaceTrackedImages] Court anchor '{imageName}' detected — placing court.");
                }
                else
                {
                    Debug.LogWarning("[PlaceTrackedImages] Court anchor detected but no ARPlaneGameSpacePlacer assigned!");
                }
                continue;
            }

            // ── Paddle QR (front or back) ─────────────────────────────────
            bool isFront = IsPaddleFront(imageName);
            bool isBack = IsPaddleBack(imageName);

            if (isFront || isBack)
            {
                bool isTracking = trackedImage.trackingState == TrackingState.Tracking;

                // Spawn paddle if first detection from either side
                if (_paddleInstance == null && isTracking)
                {
                    SpawnPaddle(trackedImage, isBack);
                }

                // Update pose when actively tracked
                if (_paddleInstance != null && isTracking)
                {
                    Quaternion rot = isBack
                        ? trackedImage.transform.rotation * BackFaceFlip * _paddlePrefabRot
                        : trackedImage.transform.rotation * _paddlePrefabRot;

                    _paddleInstance.transform.SetPositionAndRotation(
                        trackedImage.transform.position, rot);
                    _paddleInstance.SetActive(true);
                }

                // Notify PaddleHitController of tracking state
                if (_cachedPaddle != null)
                {
                    _cachedPaddle.qrActivelyTracking = isTracking;
                    _cachedPaddle.lastQrTrackingUpdateTime = Time.time;
                }

                continue;
            }

            // ── Other tracked images (generic prefab spawning) ────────────
            foreach (var curPrefab in ArPrefabs)
            {
                if (string.Compare(curPrefab.name, imageName, StringComparison.OrdinalIgnoreCase) == 0
                    && !_instantiatedPrefabs.ContainsKey(imageName))
                {
                    Quaternion prefabRot = curPrefab.transform.rotation;
                    _prefabRotOffsets[imageName] = prefabRot;
                    var newPrefab = Instantiate(curPrefab, trackedImage.transform.position,
                                                trackedImage.transform.rotation * prefabRot);
                    _instantiatedPrefabs[imageName] = newPrefab;
                }
            }

            if (_instantiatedPrefabs.TryGetValue(imageName, out var existingGo))
            {
                if (trackedImage.trackingState == TrackingState.Tracking)
                {
                    Quaternion rotOff = _prefabRotOffsets.TryGetValue(imageName, out var r)
                        ? r : Quaternion.identity;
                    existingGo.transform.SetPositionAndRotation(
                        trackedImage.transform.position,
                        trackedImage.transform.rotation * rotOff);
                    existingGo.SetActive(true);
                }
            }
        }
    }

    /// <summary>
    /// Spawns the paddle prefab and wires it to PaddleHitController.
    /// Uses the front prefab from ArPrefabs regardless of which side triggered the spawn.
    /// </summary>
    private void SpawnPaddle(ARTrackedImage trackedImage, bool isBack)
    {
        // Find the front paddle prefab from ArPrefabs
        GameObject prefab = null;
        foreach (var p in ArPrefabs)
        {
            if (string.Compare(p.name, paddleFrontImageName, StringComparison.OrdinalIgnoreCase) == 0)
            {
                prefab = p;
                break;
            }
        }

        if (prefab == null)
        {
            Debug.LogWarning($"[PlaceTrackedImages] No prefab named '{paddleFrontImageName}' in ArPrefabs.");
            return;
        }

        _paddlePrefabRot = prefab.transform.rotation;

        Quaternion spawnRot = isBack
            ? trackedImage.transform.rotation * BackFaceFlip * _paddlePrefabRot
            : trackedImage.transform.rotation * _paddlePrefabRot;

        _paddleInstance = Instantiate(prefab, trackedImage.transform.position, spawnRot);

        // Wire to physics paddle
        var paddle = FindFirstObjectByType<PaddleHitController>();
        if (paddle != null)
        {
            paddle.qrTrackedRacket = _paddleInstance.transform;
            paddle.qrPrefabRotOffset = _paddlePrefabRot;
            paddle.ApplyPaddleTransparency(_paddleInstance);
            _cachedPaddle = paddle;

            string side = isBack ? "back (180° roll flip)" : "front";
            Debug.Log($"[PlaceTrackedImages] Wired QR racket ({side}) → PaddleHitController");
        }
        else
        {
            Debug.LogWarning("[PlaceTrackedImages] PaddleHitController not found — QR racket not wired.");
        }
    }

    /// <summary>
    /// Resets the court placement flag so the next court QR scan re-places the court.
    /// </summary>
    public void ResetCourt()
    {
        _courtPlaced = false;
        if (gamePlacer != null)
            gamePlacer.ResetPlacement();
        Debug.Log("[PlaceTrackedImages] Court placement reset — scan court QR again.");
    }
}
