using UnityEngine;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Splits the AR camera view into stereoscopic left/right for goggles.
///
/// Approach: Set the main AR camera to render only the left half,
/// then create a second camera that renders the right half.
/// The second camera copies the AR background from the main camera
/// every frame so both eyes show the real-world camera feed.
///
/// SETUP:
///   1. Add this script to Main Camera (XR Origin > Camera Offset > Main Camera)
///   2. Build and deploy
///   3. Toggle 'stereoEnabled' in Inspector to switch on/off
/// </summary>
public class StereoscopicAR : MonoBehaviour
{
    [Header("Settings")]
    public bool stereoEnabled = true;

    [Tooltip("Eye separation in metres. 0.064 = average human IPD.")]
    public float ipd = 0.064f;

    [Header("HUD Canvas (World Space)")]
    [Tooltip("Distance from camera to place HUD canvases (metres).")]
    public float hudDistance = 1.5f;

    [Tooltip("Scale multiplier for HUD canvases. " +
             "At default 0.001, 1 pixel = 1 mm, so a 1080×600 canvas is 1.08 m × 0.6 m.")]
    public float hudScale = 0.001f;

    private Camera mainCam;
    private Camera rightCam;
    private GameObject rightCamObj;
    private ARCameraBackground mainARBg;
    private bool wasEnabled = false;

    // ── Static helper for stereo-compatible UI ──────────────────────────────

    /// <summary>
    /// Configures a programmatically-created canvas for stereoscopic rendering.
    /// Switches to WorldSpace, parents to the main camera, and positions at
    /// the configured HUD distance.  Both stereo cameras see the canvas naturally.
    ///
    /// Call this AFTER adding the Canvas component but BEFORE adding children.
    /// </summary>
    public static void SetupWorldSpaceCanvas(
        GameObject canvasGO, int sortingOrder,
        float width = 1080f, float height = 600f)
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        // Find instance settings (or use defaults)
        var instance = FindFirstObjectByType<StereoscopicAR>();
        float dist  = instance != null ? instance.hudDistance : 1.5f;
        float scale = instance != null ? instance.hudScale   : 0.001f;

        Canvas canvas = canvasGO.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.overrideSorting = true;
        canvas.sortingOrder = sortingOrder;
        canvas.worldCamera = cam;

        // Size the canvas rect in pixel units
        RectTransform rt = canvasGO.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(width, height);
        rt.pivot = new Vector2(0.5f, 0.5f);

        // Parent to camera and position
        canvasGO.transform.SetParent(cam.transform, false);
        canvasGO.transform.localPosition = new Vector3(0f, 0f, dist);
        canvasGO.transform.localRotation = Quaternion.identity;
        canvasGO.transform.localScale    = Vector3.one * scale;

        // CanvasScaler is not applicable to WorldSpace — remove if present
        var scaler = canvasGO.GetComponent<UnityEngine.UI.CanvasScaler>();
        if (scaler != null) Destroy(scaler);
    }

    void Start()
    {
        mainCam = GetComponent<Camera>();

        if (stereoEnabled)
            EnableStereo();
    }

    void Update()
    {
        // Toggle stereo at runtime
        if (stereoEnabled && !wasEnabled)
            EnableStereo();
        else if (!stereoEnabled && wasEnabled)
            DisableStereo();

        // Keep right camera in sync with main camera
        if (stereoEnabled && rightCam != null)
        {
            SyncCameras();
        }
    }

    void EnableStereo()
    {
        wasEnabled = true;

        // Main camera renders left half
        mainCam.rect = new Rect(0f, 0f, 0.5f, 1f);

        // Create right eye camera
        if (rightCamObj == null)
        {
            rightCamObj = new GameObject("RightEyeCamera");
            rightCamObj.transform.SetParent(transform.parent, false);
            rightCam = rightCamObj.AddComponent<Camera>();
        }

        SyncCameras();

        // Right camera renders right half
        rightCam.rect = new Rect(0.5f, 0f, 0.5f, 1f);

        // Copy AR background component to right camera
        mainARBg = mainCam.GetComponent<ARCameraBackground>();
        if (mainARBg != null)
        {
            ARCameraBackground rightARBg = rightCamObj.GetComponent<ARCameraBackground>();
            if (rightARBg == null)
                rightARBg = rightCamObj.AddComponent<ARCameraBackground>();
        }

        Debug.Log("StereoscopicAR: Stereo enabled. IPD=" + ipd);
    }

    void DisableStereo()
    {
        wasEnabled = false;

        // Restore main camera to full screen
        mainCam.rect = new Rect(0f, 0f, 1f, 1f);

        // Remove right camera
        if (rightCamObj != null)
        {
            Destroy(rightCamObj);
            rightCamObj = null;
            rightCam = null;
        }

        Debug.Log("StereoscopicAR: Stereo disabled.");
    }

    void SyncCameras()
    {
        if (rightCam == null || mainCam == null) return;

        // Copy all camera properties
        rightCam.clearFlags = mainCam.clearFlags;
        rightCam.backgroundColor = mainCam.backgroundColor;
        rightCam.cullingMask = mainCam.cullingMask;
        rightCam.fieldOfView = mainCam.fieldOfView;
        rightCam.nearClipPlane = mainCam.nearClipPlane;
        rightCam.farClipPlane = mainCam.farClipPlane;
        rightCam.depth = mainCam.depth;
        rightCam.allowHDR = mainCam.allowHDR;
        rightCam.allowMSAA = mainCam.allowMSAA;

        // Restore viewport rects (must be set after copying properties)
        mainCam.rect = new Rect(0f, 0f, 0.5f, 1f);
        rightCam.rect = new Rect(0.5f, 0f, 0.5f, 1f);

        // Position right camera with IPD offset from main camera
        float halfIPD = ipd * 0.5f;
        rightCamObj.transform.position = transform.position + transform.right * halfIPD;
        rightCamObj.transform.rotation = transform.rotation;
    }

    void OnDestroy()
    {
        // Restore main camera
        if (mainCam != null)
            mainCam.rect = new Rect(0f, 0f, 1f, 1f);

        if (rightCamObj != null)
            Destroy(rightCamObj);
    }
}
