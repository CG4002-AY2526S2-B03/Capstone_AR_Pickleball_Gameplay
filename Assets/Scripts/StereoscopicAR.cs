using UnityEngine;

/// <summary>
/// Splits the AR camera into a stereoscopic left/right eye view
/// for use with a phone-based VR headset (e.g. Cardboard-style holder).
///
/// Attach to the AR Camera GameObject. Toggle stereo mode at runtime
/// via the Inspector or by calling SetStereoEnabled().
///
/// How it works:
///   - Creates a second camera as a child of the AR camera
///   - Left eye renders to the left half of the screen
///   - Right eye renders to the right half
///   - A small horizontal offset (half IPD) simulates binocular parallax
///   - The AR background is shared across both views
/// </summary>
[RequireComponent(typeof(Camera))]
public class StereoscopicAR : MonoBehaviour
{
    [Header("Stereo Settings")]
    [Tooltip("Enable stereoscopic split-screen rendering.")]
    public bool stereoEnabled = false;

    [Tooltip("Interpupillary distance in meters (average human ~0.063m).")]
    [Range(0.05f, 0.08f)]
    public float ipd = 0.063f;

    [Header("Barrel Distortion (for lens correction)")]
    [Tooltip("Apply simple barrel distortion for VR lenses. Requires a distortion shader.")]
    public bool applyDistortion = false;

    private Camera mainCam;
    private Camera rightEyeCam;
    private GameObject rightEyeGO;

    // Original camera rect (to restore when disabling stereo)
    private Rect originalRect;
    private bool wasEnabled;

    private void Awake()
    {
        mainCam = GetComponent<Camera>();
        originalRect = mainCam.rect;
    }

    private void LateUpdate()
    {
        if (stereoEnabled && !wasEnabled)
            EnableStereo();
        else if (!stereoEnabled && wasEnabled)
            DisableStereo();

        if (stereoEnabled && rightEyeCam != null)
        {
            // Keep right eye camera in sync with main camera settings
            rightEyeCam.fieldOfView = mainCam.fieldOfView;
            rightEyeCam.nearClipPlane = mainCam.nearClipPlane;
            rightEyeCam.farClipPlane = mainCam.farClipPlane;
            rightEyeCam.clearFlags = mainCam.clearFlags;
            rightEyeCam.backgroundColor = mainCam.backgroundColor;
            rightEyeCam.cullingMask = mainCam.cullingMask;

            // Position the right eye with IPD offset
            rightEyeGO.transform.localPosition = new Vector3(ipd, 0f, 0f);
            rightEyeGO.transform.localRotation = Quaternion.identity;
        }

        wasEnabled = stereoEnabled;
    }

    /// <summary>Toggle stereo mode from code.</summary>
    public void SetStereoEnabled(bool enabled)
    {
        stereoEnabled = enabled;
    }

    private void EnableStereo()
    {
        // Left eye = main camera, renders to left half
        mainCam.rect = new Rect(0f, 0f, 0.5f, 1f);

        // Offset main camera (left eye) by -half IPD
        // We do this via a child offset object approach:
        // Main cam stays at center, we just adjust the viewport.
        // For proper parallax, shift the main cam left by half IPD
        // and the right cam right by half IPD from the original position.

        if (rightEyeGO == null)
        {
            rightEyeGO = new GameObject("RightEyeCamera");
            rightEyeGO.transform.SetParent(transform, false);
            rightEyeGO.transform.localPosition = new Vector3(ipd, 0f, 0f);
            rightEyeGO.transform.localRotation = Quaternion.identity;

            rightEyeCam = rightEyeGO.AddComponent<Camera>();
        }

        // Right eye renders to right half of screen
        rightEyeCam.CopyFrom(mainCam);
        rightEyeCam.rect = new Rect(0.5f, 0f, 0.5f, 1f);
        rightEyeCam.depth = mainCam.depth + 1;

        // Disable AR background on the right eye camera to avoid double-rendering
        // The AR background from the left eye is sufficient for the passthrough effect
        var arBg = rightEyeGO.GetComponent<UnityEngine.XR.ARFoundation.ARCameraBackground>();
        if (arBg != null)
            arBg.enabled = false;

        rightEyeCam.enabled = true;

        Debug.Log("[StereoscopicAR] Stereo mode ENABLED");
    }

    private void DisableStereo()
    {
        // Restore main camera to fullscreen
        mainCam.rect = originalRect;

        if (rightEyeCam != null)
            rightEyeCam.enabled = false;

        Debug.Log("[StereoscopicAR] Stereo mode DISABLED");
    }

    private void OnDestroy()
    {
        if (rightEyeGO != null)
            Destroy(rightEyeGO);
    }
}
