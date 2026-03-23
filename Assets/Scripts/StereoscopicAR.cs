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

    private Camera mainCam;
    private Camera rightCam;
    private GameObject rightCamObj;
    private ARCameraBackground mainARBg;
    private bool wasEnabled = false;

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
