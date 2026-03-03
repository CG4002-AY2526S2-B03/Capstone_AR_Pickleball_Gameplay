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

    // Keep dictionary array of created prefabs
    private readonly Dictionary<string, GameObject> _instantiatedPrefabs = new Dictionary<string, GameObject>();

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
            _instantiatedPrefabs.Remove(key);

        Debug.Log("[PlaceTrackedImages] Racket prefabs cleared — scan QR again.");
    }

    // Event Handler
    private void OnTrackedImagesChanged(ARTrackedImagesChangedEventArgs eventArgs)
    {
        // Process both newly added AND updated images
        ProcessTrackedImages(eventArgs.added);
        ProcessTrackedImages(eventArgs.updated);

        // If the AR subsystem has given up looking for a tracked image
        foreach (var trackedImage in eventArgs.removed)
        {
            var imageName = trackedImage.referenceImage.name;

            if (_instantiatedPrefabs.TryGetValue(imageName, out var go))
            {
                Destroy(go);
                _instantiatedPrefabs.Remove(imageName);
            }
        }
    }

    private void ProcessTrackedImages(IReadOnlyList<ARTrackedImage> images)
    {
        if (images == null) return;

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
                    var newPrefab = Instantiate(curPrefab, trackedImage.transform);
                    _instantiatedPrefabs[imageName] = newPrefab;

                    // ── Wire the QR racket to the physics paddle ──────────────────
                    // The physics paddle (PlayerPaddle with PaddleHitController) must
                    // follow the QR-tracked racket so collisions happen where the
                    // player sees the visual racket, not at a fixed camera offset.
                    var paddle = FindFirstObjectByType<PaddleHitController>();
                    if (paddle != null)
                    {
                        paddle.qrTrackedRacket = newPrefab.transform;
                        Debug.Log($"[PlaceTrackedImages] Wired QR racket '{imageName}' → PaddleHitController.qrTrackedRacket");
                    }
                    else
                    {
                        Debug.LogWarning("[PlaceTrackedImages] PaddleHitController not found — QR racket tracking not wired.");
                    }
                }
            }

            // Update visibility of already-instantiated prefabs
            if (_instantiatedPrefabs.TryGetValue(imageName, out var existingGo))
            {
                existingGo.SetActive(trackedImage.trackingState == TrackingState.Tracking);
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