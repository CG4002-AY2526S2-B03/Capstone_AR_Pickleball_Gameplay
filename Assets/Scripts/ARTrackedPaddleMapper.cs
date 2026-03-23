using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public class ARTrackedPaddleMapper : MonoBehaviour
{
    [Header("Target Transforms")]
    [Tooltip("Root transform that should represent the physical paddle pose. Use an empty parent pivot.")]
    [SerializeField] private Transform paddlePivot;

    [Tooltip("Optional child mesh root to rotate so the handle points along paddlePivot local +Z.")]
    [SerializeField] private Transform visualRoot;

    [Header("Axis Alignment")]
    [Tooltip("Local euler correction for the visible mesh. Use this to make handle align with +Z of paddlePivot.")]
    [SerializeField] private Vector3 visualLocalEulerCorrection;

    [Header("Paddle Mapping Offsets")]
    [Tooltip("Local position offset from tracked image center to paddle pivot.")]
    [SerializeField] private Vector3 localPositionOffset;

    [Tooltip("Local rotation offset from tracked image rotation to paddle pivot.")]
    [SerializeField] private Vector3 localEulerOffset;

    [Header("Tracking Behavior")]
    [Tooltip("When true, paddle visuals and colliders are hidden when QR loses tracking. " +
             "When false (default), the paddle stays visible at its last known position.")]
    [SerializeField] private bool hideWhenNotTracking = false;

    [SerializeField] private bool saveOffsetsPerImage = true;

    [Header("Calibration (Editor/Desktop)")]
    [SerializeField] private bool enableKeyboardCalibration = true;

    [SerializeField] private float positionStepMeters = 0.0025f;
    [SerializeField] private float rotationStepDegrees = 1.0f;

    private ARTrackedImage trackedImage;
    private bool calibrationEnabled;
    private Renderer[] childRenderers;
    private Collider[] childColliders;
    private bool visualsVisible = true;

    private string SaveKeyPrefix => "ARTrackedPaddleMapper";

    private void Awake()
    {
        trackedImage = GetComponentInParent<ARTrackedImage>();
        childRenderers = GetComponentsInChildren<Renderer>(true);
        childColliders = GetComponentsInChildren<Collider>(true);

        if (paddlePivot == null)
        {
            paddlePivot = transform;
        }

        if (saveOffsetsPerImage)
        {
            LoadOffsets();
        }

        ApplyCurrentOffsets();
        ApplyVisualAxisCorrection();
    }

    private void OnEnable()
    {
        ApplyCurrentOffsets();
        ApplyVisualAxisCorrection();
    }

    private void OnDisable()
    {
        if (saveOffsetsPerImage)
        {
            SaveOffsets();
        }
    }

    private void Update()
    {
        UpdateTrackingVisibility();

        if (enableKeyboardCalibration)
        {
            HandleCalibrationHotkeys();
        }
    }

    private void UpdateTrackingVisibility()
    {
        if (!hideWhenNotTracking || trackedImage == null)
        {
            return;
        }

        bool isTracking = trackedImage.trackingState == TrackingState.Tracking;

        if (visualsVisible == isTracking)
        {
            return;
        }

        for (int index = 0; index < childRenderers.Length; index++)
        {
            if (childRenderers[index] != null)
            {
                childRenderers[index].enabled = isTracking;
            }
        }

        for (int index = 0; index < childColliders.Length; index++)
        {
            if (childColliders[index] != null)
            {
                childColliders[index].enabled = isTracking;
            }
        }

        visualsVisible = isTracking;
    }

    private void HandleCalibrationHotkeys()
    {
        if (Input.GetKeyDown(KeyCode.C))
        {
            ToggleCalibration();
        }

        if (!calibrationEnabled)
        {
            return;
        }

        bool changed = false;

        if (Input.GetKeyDown(KeyCode.R))
        {
            ResetOffsets();
        }

        if (Input.GetKeyDown(KeyCode.P))
        {
            SaveCurrentOffsets();
        }

        changed |= HandlePositionKeys();
        changed |= HandleRotationKeys();

        if (changed)
        {
            ApplyCurrentOffsets();
            Debug.Log($"[ARTrackedPaddleMapper] Offset Pos={localPositionOffset} Rot={localEulerOffset}");
        }
    }

    private bool HandlePositionKeys()
    {
        bool changed = false;

        if (Input.GetKeyDown(KeyCode.A)) { localPositionOffset.x -= positionStepMeters; changed = true; }
        if (Input.GetKeyDown(KeyCode.D)) { localPositionOffset.x += positionStepMeters; changed = true; }
        if (Input.GetKeyDown(KeyCode.Q)) { localPositionOffset.y += positionStepMeters; changed = true; }
        if (Input.GetKeyDown(KeyCode.E)) { localPositionOffset.y -= positionStepMeters; changed = true; }
        if (Input.GetKeyDown(KeyCode.W)) { localPositionOffset.z += positionStepMeters; changed = true; }
        if (Input.GetKeyDown(KeyCode.S)) { localPositionOffset.z -= positionStepMeters; changed = true; }

        return changed;
    }

    private bool HandleRotationKeys()
    {
        bool changed = false;

        if (Input.GetKeyDown(KeyCode.I)) { localEulerOffset.x += rotationStepDegrees; changed = true; }
        if (Input.GetKeyDown(KeyCode.K)) { localEulerOffset.x -= rotationStepDegrees; changed = true; }
        if (Input.GetKeyDown(KeyCode.J)) { localEulerOffset.y -= rotationStepDegrees; changed = true; }
        if (Input.GetKeyDown(KeyCode.L)) { localEulerOffset.y += rotationStepDegrees; changed = true; }
        if (Input.GetKeyDown(KeyCode.U)) { localEulerOffset.z -= rotationStepDegrees; changed = true; }
        if (Input.GetKeyDown(KeyCode.O)) { localEulerOffset.z += rotationStepDegrees; changed = true; }

        return changed;
    }

    private void ApplyCurrentOffsets()
    {
        if (paddlePivot == null)
        {
            return;
        }

        paddlePivot.localPosition = localPositionOffset;
        paddlePivot.localRotation = Quaternion.Euler(localEulerOffset);
    }

    private void ApplyVisualAxisCorrection()
    {
        if (visualRoot == null)
        {
            return;
        }

        visualRoot.localRotation = Quaternion.Euler(visualLocalEulerCorrection);
    }

    private void SaveOffsets()
    {
        string key = GetImageScopedKey();
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        PlayerPrefs.SetFloat($"{key}.posX", localPositionOffset.x);
        PlayerPrefs.SetFloat($"{key}.posY", localPositionOffset.y);
        PlayerPrefs.SetFloat($"{key}.posZ", localPositionOffset.z);

        PlayerPrefs.SetFloat($"{key}.rotX", localEulerOffset.x);
        PlayerPrefs.SetFloat($"{key}.rotY", localEulerOffset.y);
        PlayerPrefs.SetFloat($"{key}.rotZ", localEulerOffset.z);
        PlayerPrefs.Save();
    }

    private void LoadOffsets()
    {
        string key = GetImageScopedKey();
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (!PlayerPrefs.HasKey($"{key}.posX"))
        {
            return;
        }

        localPositionOffset = new Vector3(
            PlayerPrefs.GetFloat($"{key}.posX"),
            PlayerPrefs.GetFloat($"{key}.posY"),
            PlayerPrefs.GetFloat($"{key}.posZ"));

        localEulerOffset = new Vector3(
            PlayerPrefs.GetFloat($"{key}.rotX"),
            PlayerPrefs.GetFloat($"{key}.rotY"),
            PlayerPrefs.GetFloat($"{key}.rotZ"));
    }

    private string GetImageScopedKey()
    {
        string imageName = trackedImage != null
            ? trackedImage.referenceImage.name
            : gameObject.name;

        if (string.IsNullOrWhiteSpace(imageName))
        {
            return null;
        }

        return $"{SaveKeyPrefix}.{imageName}";
    }

    public Vector3 GetPaddleForwardAxis()
    {
        return paddlePivot != null ? paddlePivot.forward : transform.forward;
    }

    public bool CalibrationEnabled => calibrationEnabled;

    public Vector3 LocalPositionOffset => localPositionOffset;

    public Vector3 LocalEulerOffset => localEulerOffset;

    public void ToggleCalibration()
    {
        calibrationEnabled = !calibrationEnabled;
        Debug.Log($"[ARTrackedPaddleMapper] Calibration mode: {(calibrationEnabled ? "ON" : "OFF")}");
    }

    public void SetCalibrationEnabled(bool enabled)
    {
        calibrationEnabled = enabled;
    }

    public void AdjustPosition(Vector3 delta)
    {
        localPositionOffset += delta;
        ApplyCurrentOffsets();
    }

    public void AdjustRotation(Vector3 deltaEuler)
    {
        localEulerOffset += deltaEuler;
        ApplyCurrentOffsets();
    }

    public void ResetOffsets()
    {
        localPositionOffset = Vector3.zero;
        localEulerOffset = Vector3.zero;
        ApplyCurrentOffsets();
    }

    public void SaveCurrentOffsets()
    {
        SaveOffsets();
    }
}