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

    [Tooltip("Extra local-space offset applied to the QR-spawned paddle visual relative to the tracked QR pose.")]
    public Vector3 paddleVisualLocalOffset = new Vector3(0f, 0f, 0.3f);

    [Header("Paddle QR Tracking")]
    [Tooltip("Requested moving-image tracking budget for ARTrackedImageManager. Higher values can help retain fast-moving paddle QR images.")]
    public int requestedMaxNumberOfMovingImages = 3;

    [Tooltip("When true, paddle QR remains usable while AR tracking is Limited instead of dropping immediately.")]
    public bool treatLimitedTrackingAsActiveForPaddle = true;

    [Header("Paddle QR Smoothing")]
    [Tooltip("When true, applies an adaptive Kalman filter to paddle QR position updates.")]
    public bool enablePaddleQrKalmanFilter = true;

    [Tooltip("Process noise for paddle QR Kalman position filter (higher follows measurement more quickly).")]
    public float paddleQrProcessNoise = 0.03f;

    [Tooltip("Measurement noise used when QR is near the camera (lower = less smoothing).")]
    public float paddleQrMeasurementNoiseNear = 0.0002f;

    [Tooltip("Measurement noise used when QR is far from the camera (higher = more smoothing).")]
    public float paddleQrMeasurementNoiseFar = 0.002f;

    [Tooltip("Camera distance where far-noise weighting is fully applied (meters).")]
    public float paddleQrKalmanFarDistance = 2.2f;

    [Tooltip("If measured QR position jumps farther than this in one update (meters), reset filter to avoid lagging behind hard relocalization.")]
    public float paddleQrKalmanSnapDistance = 0.45f;

    [Tooltip("Smoothing rate for paddle QR rotation updates (1/seconds).")]
    public float paddleQrRotationSmoothing = 45f;

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
    private Transform _arCameraTransform;

    // Paddle QR smoothing filter state
    private bool _hasPaddlePoseFilter;
    private PositionKalman1D _paddleKalmanX;
    private PositionKalman1D _paddleKalmanY;
    private PositionKalman1D _paddleKalmanZ;
    private Vector3 _filteredPaddlePosition;
    private Quaternion _filteredPaddleRotation = Quaternion.identity;
    private float _lastPaddleFilterTime;

    // 180° flip around roll (Z) axis for back-side QR
    private static readonly Quaternion BackFaceFlip = Quaternion.Euler(0f, 0f, 180f);

    // Keep dictionary of created prefabs for non-paddle tracked images
    private readonly Dictionary<string, GameObject> _instantiatedPrefabs = new Dictionary<string, GameObject>();
    private readonly Dictionary<string, Quaternion> _prefabRotOffsets = new Dictionary<string, Quaternion>();

    void Awake()
    {
        _trackedImagesManager = GetComponent<ARTrackedImageManager>();
        ConfigureTrackedImageManager();

        if (Camera.main != null)
            _arCameraTransform = Camera.main.transform;

        if (gamePlacer == null)
            gamePlacer = FindFirstObjectByType<ARPlaneGameSpacePlacer>();

        if (gamePlacer != null)
            gamePlacer.PlaceOnlyFromQrAnchor = true;
    }

    void OnEnable()
    {
        ConfigureTrackedImageManager();
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
        ResetPaddlePoseFilterState();
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

        foreach (var trackedImage in images)
        {
            var imageName = trackedImage.referenceImage.name;

            // Keep all tracked-image-driven gameplay objects (including court QR)
            // locked until the game is explicitly started by Button 1.
            if (!_gameStarted)
            {
                continue;
            }

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
                    TutorialManager tutorialManager = TutorialManager.Instance;
                    if (tutorialManager != null)
                        tutorialManager.OnCourtPlaced();
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
                bool isLimited = trackedImage.trackingState == TrackingState.Limited;
                bool isUsableTracking = isTracking || (treatLimitedTrackingAsActiveForPaddle && isLimited);

                // Spawn paddle if first detection from either side
                if (_paddleInstance == null && isUsableTracking)
                {
                    SpawnPaddle(trackedImage, isBack);
                }

                // Update pose when actively tracked
                if (_paddleInstance != null && isUsableTracking)
                {
                    Quaternion rot = isBack
                        ? trackedImage.transform.rotation * BackFaceFlip * _paddlePrefabRot
                        : trackedImage.transform.rotation * _paddlePrefabRot;

                    GetFilteredPaddlePose(
                        trackedImage.transform.position,
                        rot,
                        out Vector3 filteredPosition,
                        out Quaternion filteredRotation);

                    Vector3 visualPosition = GetPaddleVisualWorldPosition(filteredPosition, filteredRotation);

                    _paddleInstance.transform.SetPositionAndRotation(
                        visualPosition, filteredRotation);
                    _paddleInstance.SetActive(true);
                }

                // Notify PaddleHitController of tracking state
                if (_cachedPaddle != null)
                {
                    if (isUsableTracking)
                    {
                        _cachedPaddle.qrActivelyTracking = true;
                        _cachedPaddle.lastQrTrackingUpdateTime = Time.time;
                    }
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

    private void ConfigureTrackedImageManager()
    {
        if (_trackedImagesManager == null)
            return;

        _trackedImagesManager.requestedMaxNumberOfMovingImages = Mathf.Max(1, requestedMaxNumberOfMovingImages);
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

        Vector3 spawnPosition = GetPaddleVisualWorldPosition(trackedImage.transform.position, spawnRot);
        _paddleInstance = Instantiate(prefab, spawnPosition, spawnRot);
        InitializePaddlePoseFilter(trackedImage.transform.position, spawnRot);

        // Wire to physics paddle
        var paddle = FindFirstObjectByType<PaddleHitController>();
        if (paddle != null)
        {
            paddle.qrTrackedRacket = _paddleInstance.transform;
            paddle.qrPrefabRotOffset = _paddlePrefabRot;
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
        _gameStarted = false;
        if (gamePlacer != null)
            gamePlacer.ResetPlacement();
        Debug.Log("[PlaceTrackedImages] Court placement reset — press Start then scan court QR again.");
    }

    private void ResetPaddlePoseFilterState()
    {
        _hasPaddlePoseFilter = false;
        _paddleKalmanX = default;
        _paddleKalmanY = default;
        _paddleKalmanZ = default;
        _filteredPaddlePosition = Vector3.zero;
        _filteredPaddleRotation = Quaternion.identity;
        _lastPaddleFilterTime = 0f;
    }

    private Vector3 GetPaddleVisualWorldPosition(Vector3 qrWorldPosition, Quaternion visualRotation)
    {
        return qrWorldPosition + visualRotation * paddleVisualLocalOffset;
    }

    private void InitializePaddlePoseFilter(Vector3 worldPosition, Quaternion worldRotation)
    {
        _paddleKalmanX.Reset(worldPosition.x);
        _paddleKalmanY.Reset(worldPosition.y);
        _paddleKalmanZ.Reset(worldPosition.z);
        _filteredPaddlePosition = worldPosition;
        _filteredPaddleRotation = worldRotation;
        _lastPaddleFilterTime = Time.time;
        _hasPaddlePoseFilter = true;
    }

    private void GetFilteredPaddlePose(
        Vector3 measuredPosition,
        Quaternion measuredRotation,
        out Vector3 filteredPosition,
        out Quaternion filteredRotation)
    {
        if (!enablePaddleQrKalmanFilter)
        {
            filteredPosition = measuredPosition;
            filteredRotation = measuredRotation;
            return;
        }

        if (_arCameraTransform == null && Camera.main != null)
            _arCameraTransform = Camera.main.transform;

        if (!_hasPaddlePoseFilter)
        {
            InitializePaddlePoseFilter(measuredPosition, measuredRotation);
            filteredPosition = measuredPosition;
            filteredRotation = measuredRotation;
            return;
        }

        float now = Time.time;
        float dt = _lastPaddleFilterTime > 0f ? now - _lastPaddleFilterTime : Time.deltaTime;
        _lastPaddleFilterTime = now;
        dt = Mathf.Clamp(dt, 0.001f, 0.2f);

        float snapDistance = Mathf.Max(0f, paddleQrKalmanSnapDistance);
        if (snapDistance > 0f)
        {
            float jumpDistance = Vector3.Distance(_filteredPaddlePosition, measuredPosition);
            if (jumpDistance > snapDistance)
            {
                InitializePaddlePoseFilter(measuredPosition, measuredRotation);
                filteredPosition = measuredPosition;
                filteredRotation = measuredRotation;
                return;
            }
        }

        float distanceToCamera = 0f;
        if (_arCameraTransform != null)
            distanceToCamera = Vector3.Distance(_arCameraTransform.position, measuredPosition);

        float farDistance = Mathf.Max(0.01f, paddleQrKalmanFarDistance);
        float distanceFactor = Mathf.Clamp01(distanceToCamera / farDistance);

        float measurementNoise = Mathf.Lerp(
            Mathf.Max(1e-6f, paddleQrMeasurementNoiseNear),
            Mathf.Max(1e-6f, paddleQrMeasurementNoiseFar),
            distanceFactor);
        float processNoise = Mathf.Max(1e-6f, paddleQrProcessNoise);

        _filteredPaddlePosition = new Vector3(
            _paddleKalmanX.Update(measuredPosition.x, dt, processNoise, measurementNoise),
            _paddleKalmanY.Update(measuredPosition.y, dt, processNoise, measurementNoise),
            _paddleKalmanZ.Update(measuredPosition.z, dt, processNoise, measurementNoise));

        float rotationRate = Mathf.Max(0f, paddleQrRotationSmoothing);
        float rotationLerp = 1f - Mathf.Exp(-rotationRate * dt);
        _filteredPaddleRotation = Quaternion.Slerp(_filteredPaddleRotation, measuredRotation, rotationLerp);

        filteredPosition = _filteredPaddlePosition;
        filteredRotation = _filteredPaddleRotation;
    }

    private struct PositionKalman1D
    {
        private bool _initialized;
        private float _position;
        private float _velocity;
        private float _p00;
        private float _p01;
        private float _p10;
        private float _p11;

        public void Reset(float position)
        {
            _initialized = true;
            _position = position;
            _velocity = 0f;
            _p00 = 1f;
            _p01 = 0f;
            _p10 = 0f;
            _p11 = 1f;
        }

        public float Update(float measurement, float dt, float processNoise, float measurementNoise)
        {
            if (!_initialized)
            {
                Reset(measurement);
                return _position;
            }

            dt = Mathf.Max(0.0001f, dt);
            processNoise = Mathf.Max(1e-7f, processNoise);
            measurementNoise = Mathf.Max(1e-7f, measurementNoise);

            // Predict with a constant-velocity state model.
            _position += _velocity * dt;

            float dt2 = dt * dt;
            float dt3 = dt2 * dt;
            float dt4 = dt2 * dt2;

            float q00 = 0.25f * dt4 * processNoise;
            float q01 = 0.5f * dt3 * processNoise;
            float q11 = dt2 * processNoise;

            float predP00 = _p00 + dt * (_p10 + _p01) + dt2 * _p11 + q00;
            float predP01 = _p01 + dt * _p11 + q01;
            float predP10 = _p10 + dt * _p11 + q01;
            float predP11 = _p11 + q11;

            float innovation = measurement - _position;
            float s = predP00 + measurementNoise;
            float invS = 1f / s;
            float k0 = predP00 * invS;
            float k1 = predP10 * invS;

            _position += k0 * innovation;
            _velocity += k1 * innovation;

            _p00 = (1f - k0) * predP00;
            _p01 = (1f - k0) * predP01;
            _p10 = predP10 - k1 * predP00;
            _p11 = predP11 - k1 * predP01;

            // Keep covariance numerically symmetric.
            float offDiag = 0.5f * (_p01 + _p10);
            _p01 = offDiag;
            _p10 = offDiag;

            return _position;
        }
    }
}
