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

    private bool _courtPlaced;

    /// <summary>
    /// Gate flag — when false, image detections are ignored.
    /// Set to true by <see cref="StartGame"/> (called from PlayButtonUI).
    /// </summary>
    private bool _gameStarted;

    // Cached reference to the physics paddle for tracking-state updates
    private PaddleHitController _cachedPaddle;
    // The image name that was wired to the paddle (to identify racket images)
    private string _racketImageName;

    // Keep dictionary array of created prefabs
    private readonly Dictionary<string, GameObject> _instantiatedPrefabs = new Dictionary<string, GameObject>();
    // Store each prefab's baked-in rotation so we can apply it as offset after un-parenting
    private readonly Dictionary<string, Quaternion> _prefabRotOffsets = new Dictionary<string, Quaternion>();

    void Awake()
    {
        // Cache a reference to the Tracked Image Manager component
        _trackedImagesManager = GetComponent<ARTrackedImageManager>();

        if (gamePlacer == null)
            gamePlacer = FindFirstObjectByType<ARPlaneGameSpacePlacer>();
    }

    void OnEnable()
    {
        // Attach event handler when tracked images change
        _trackedImagesManager.trackedImagesChanged += OnTrackedImagesChanged;
    }

    void OnDisable()
    {
        // Remove event handler 
        _trackedImagesManager.trackedImagesChanged -= OnTrackedImagesChanged;
    }

    // ── Public Reset API (called by RecalibrateUI) ────────────────

    /// <summary>
    /// Destroys all spawned racket prefabs so the next QR detection will re-spawn them.
    /// </summary>
    public void ResetRacket()
    {
        // Disconnect QR tracking from the physics paddle
        var paddle = FindFirstObjectByType<PaddleHitController>();
        if (paddle != null)
        {
            paddle.qrTrackedRacket = null;
        }

        var toRemove = new List<string>();
        foreach (var kvp in _instantiatedPrefabs)
        {
            if (kvp.Value != null) Destroy(kvp.Value);
            toRemove.Add(kvp.Key);
        }
        foreach (var key in toRemove)
        {
            _instantiatedPrefabs.Remove(key);
            _prefabRotOffsets.Remove(key);
        }

        Debug.Log("[PlaceTrackedImages] Racket prefabs cleared — scan QR again.");
    }

    // Event Handler
    private void OnTrackedImagesChanged(ARTrackedImagesChangedEventArgs eventArgs)
    {
        // Process both newly added AND updated images
        ProcessTrackedImages(eventArgs.added);
        ProcessTrackedImages(eventArgs.updated);

        // If the AR subsystem has given up looking for a tracked image.
        // Do NOT destroy spawned prefabs — they persist at their last
        // known pose so the paddle stays visible when QR is out of view.
        // Only ResetRacket() (button 3) explicitly destroys them.
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

    private void ProcessTrackedImages(IReadOnlyList<ARTrackedImage> images)
    {
        if (images == null) return;
        if (!_gameStarted) return; // wait until player presses Start

        foreach (var trackedImage in images)
        {
            var imageName = trackedImage.referenceImage.name;

            // ── Court anchor detection → place GameSpaceRoot ──
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
            }

            // ── Normal prefab-spawning for tracked images (e.g. racket) ──
            foreach (var curPrefab in ArPrefabs)
            {
                if (string.Compare(curPrefab.name, imageName, StringComparison.OrdinalIgnoreCase) == 0
                    && !_instantiatedPrefabs.ContainsKey(imageName))
                {
                    // Spawn at the tracked image pose but UN-PARENT immediately
                    // so ARFoundation can't deactivate it when tracking is lost.
                    // Apply prefab's baked-in rotation as offset (was automatic when parented).
                    Quaternion prefabRot = curPrefab.transform.rotation;
                    _prefabRotOffsets[imageName] = prefabRot;
                    var newPrefab = Instantiate(curPrefab, trackedImage.transform.position,
                                                trackedImage.transform.rotation * prefabRot);
                    _instantiatedPrefabs[imageName] = newPrefab;

                    // ── Wire the QR racket to the physics paddle ──────────────────
                    var paddle = FindFirstObjectByType<PaddleHitController>();
                    if (paddle != null)
                    {
                        paddle.qrTrackedRacket = newPrefab.transform;
                        _cachedPaddle = paddle;
                        _racketImageName = imageName;
                        Debug.Log($"[PlaceTrackedImages] Wired QR racket '{imageName}' → PaddleHitController.qrTrackedRacket");
                    }
                    else
                    {
                        Debug.LogWarning("[PlaceTrackedImages] PaddleHitController not found — QR racket tracking not wired.");
                    }
                }
            }

            // Update pose of already-instantiated prefabs ONLY when actively tracking.
            // When tracking is lost the prefab stays at its last known pose.
            if (_instantiatedPrefabs.TryGetValue(imageName, out var existingGo))
            {
                bool isTracking = trackedImage.trackingState == TrackingState.Tracking;

                if (isTracking)
                {
                    Quaternion rotOff = _prefabRotOffsets.TryGetValue(imageName, out var r)
                        ? r : Quaternion.identity;
                    existingGo.transform.SetPositionAndRotation(
                        trackedImage.transform.position,
                        trackedImage.transform.rotation * rotOff);
                    existingGo.SetActive(true);
                }

                // Notify PaddleHitController whether QR is actively tracked this frame
                if (_cachedPaddle != null
                    && string.Compare(imageName, _racketImageName, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    _cachedPaddle.qrActivelyTracking = isTracking;
                }
            }
        }
    }

    /// <summary>
    /// Resets the court placement flag so the next court QR scan re-places the court.
    /// Call this from RecalibrateUI if you want a full court reset.
    /// </summary>
    public void ResetCourt()
    {
        _courtPlaced = false;
        if (gamePlacer != null)
            gamePlacer.ResetPlacement();
        Debug.Log("[PlaceTrackedImages] Court placement reset — scan court QR again.");
    }
}