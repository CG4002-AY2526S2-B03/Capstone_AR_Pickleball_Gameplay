using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
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

    [Header("Court Anchor")]
    [Tooltip("Name of the reference image used as the stationary court anchor " +
             "(must match the entry in the Reference Image Library).")]
    [SerializeField] private string courtAnchorImageName = "CourtAnchor";

    [Tooltip("The ARPlaneGameSpacePlacer that will receive the anchor pose.")]
    [SerializeField] private ARPlaneGameSpacePlacer gamePlacer;

    [Header("Game Flow")]
    [Tooltip("Fired once when the court anchor QR is first detected.")]
    public UnityEvent onCourtAnchorDetected;

    // Keep dictionary array of created prefabs
    private readonly Dictionary<string, GameObject> _instantiatedPrefabs = new Dictionary<string, GameObject>();

    private bool _courtAnchorPlaced;

    void Awake()
    {
        // Cache a reference to the Tracked Image Manager component
        _trackedImagesManager = GetComponent<ARTrackedImageManager>();
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
    /// Resets court anchor state so the next QR detection will re-place the court.
    /// Also tells ARPlaneGameSpacePlacer to destroy its ARAnchor.
    /// </summary>
    public void ResetCourtAnchor()
    {
        _courtAnchorPlaced = false;

        if (gamePlacer != null)
            gamePlacer.ResetPlacement();

        Debug.Log("[PlaceTrackedImages] Court anchor reset — scan QR again.");
    }

    /// <summary>
    /// Destroys all spawned racket prefabs so the next QR detection will re-spawn them.
    /// </summary>
    public void ResetRacket()
    {
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

            // Never destroy things spawned by the court anchor
            if (string.Compare(imageName, courtAnchorImageName, StringComparison.OrdinalIgnoreCase) == 0)
                continue;

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

            // ── Court Anchor handling ──────────────────────────────
            if (string.Compare(imageName, courtAnchorImageName, StringComparison.OrdinalIgnoreCase) == 0)
            {
                if (trackedImage.trackingState != TrackingState.Tracking)
                    continue;

                if (!_courtAnchorPlaced)
                {
                    _courtAnchorPlaced = true;

                    // Resolve gamePlacer if not assigned in Inspector
                    if (gamePlacer == null)
                        gamePlacer = FindFirstObjectByType<ARPlaneGameSpacePlacer>();

                    if (gamePlacer != null)
                    {
                        var anchorPose = new Pose(
                            trackedImage.transform.position,
                            trackedImage.transform.rotation);
                        gamePlacer.PlaceAtAnchor(anchorPose);
                    }
                    else
                    {
                        Debug.LogError("[PlaceTrackedImages] No ARPlaneGameSpacePlacer found!");
                    }

                    onCourtAnchorDetected?.Invoke();
                }
                continue; // don't try to match a prefab for the anchor image
            }

            // ── Normal prefab-spawning for other images (e.g. racket) ──
            foreach (var curPrefab in ArPrefabs)
            {
                if (string.Compare(curPrefab.name, imageName, StringComparison.OrdinalIgnoreCase) == 0
                    && !_instantiatedPrefabs.ContainsKey(imageName))
                {
                    var newPrefab = Instantiate(curPrefab, trackedImage.transform);
                    _instantiatedPrefabs[imageName] = newPrefab;
                }
            }

            // Update visibility of already-instantiated prefabs
            if (_instantiatedPrefabs.TryGetValue(imageName, out var existingGo))
            {
                existingGo.SetActive(trackedImage.trackingState == TrackingState.Tracking);
            }
        }
    }
}