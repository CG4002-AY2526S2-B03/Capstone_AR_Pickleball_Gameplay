using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Toggles stereoscopic side-by-side rendering for phone-based VR headsets
/// while keeping AR Foundation (ARKit) fully active.
///
/// Works by controlling <see cref="StereoSplitRendererFeature"/>, a URP
/// ScriptableRendererFeature that runs after all rendering and splits the
/// final composited frame (AR background + 3D objects) into two eye views.
///
/// Setup:
///   1. Add StereoSplitRendererFeature to your URP Renderer Data asset.
///   2. Assign the StereoSplit shader on the feature in the Inspector.
///   3. Attach this script to any GameObject.
///   4. Toggle stereoEnabled to switch between normal and VR view.
/// </summary>
public class StereoscopicAR : MonoBehaviour
{
    [Header("Stereo Settings")]
    [Tooltip("Enable stereoscopic split-screen rendering.")]
    public bool stereoEnabled = false;

    [Tooltip("Interpupillary distance in meters (average human ~0.063m).")]
    [Range(0.0f, 0.08f)]
    public float ipd = 0.063f;

    [Tooltip("Convergence distance in meters. Objects at this distance " +
             "appear at screen depth.")]
    [Range(0.5f, 20f)]
    public float convergenceDistance = 5f;

    [Header("Display")]
    [Tooltip("Vertical black divider width as screen fraction (0.003 ≈ 2-3 px).")]
    [Range(0f, 0.02f)]
    public float dividerWidth = 0.003f;

    private Camera arCamera;
    private bool _loggedActivation;
    private bool _loggedRendererInfo;
    private static int s_instanceCount;
    private static StereoscopicAR s_activeInstance;

    private void Awake()
    {
        arCamera = Camera.main;
    }

    private void OnEnable()
    {
        s_instanceCount++;
        if (s_instanceCount > 1)
        {
            Debug.LogWarning($"[StereoscopicAR] DUPLICATE INSTANCE detected! " +
                             $"({s_instanceCount} instances active). " +
                             $"Only one StereoscopicAR should exist. Disabling this one.");
            enabled = false;
            return;
        }
        s_activeInstance = this;

        if (arCamera == null)
            arCamera = Camera.main;
    }

    private void LateUpdate()
    {
        StereoSplitRendererFeature.IsActive = stereoEnabled;

        // Log renderer info once to verify the camera uses the correct renderer
        if (!_loggedRendererInfo && arCamera != null)
        {
            var camData = arCamera.GetUniversalAdditionalCameraData();
            if (camData != null)
            {
                var renderer = camData.scriptableRenderer;
                Debug.Log($"[StereoscopicAR] Camera renderer: " +
                          $"type={renderer?.GetType().Name ?? "NULL"}, " +
                          $"toString={renderer?.ToString() ?? "NULL"}");
            }
            else
            {
                Debug.LogWarning("[StereoscopicAR] No UniversalAdditionalCameraData on main camera!");
            }
            _loggedRendererInfo = true;
        }

        if (stereoEnabled && !_loggedActivation)
        {
            Debug.Log($"[StereoscopicAR] Stereo ENABLED — IsActive={StereoSplitRendererFeature.IsActive}, camera={arCamera != null}");
            _loggedActivation = true;
        }
        else if (!stereoEnabled && _loggedActivation)
        {
            Debug.Log("[StereoscopicAR] Stereo DISABLED");
            _loggedActivation = false;
        }

        if (stereoEnabled && arCamera != null)
        {
            float fovRad = arCamera.fieldOfView * Mathf.Deg2Rad;
            float viewWidth = 2f * convergenceDistance
                              * Mathf.Tan(fovRad * 0.5f)
                              * arCamera.aspect;
            float uvShift = (ipd * 0.5f) / viewWidth;

            StereoSplitRendererFeature.UVShift = uvShift;
            StereoSplitRendererFeature.DividerWidth = dividerWidth;

            Screen.orientation = ScreenOrientation.LandscapeLeft;
        }
    }

    private void OnDisable()
    {
        s_instanceCount--;
        if (s_activeInstance == this)
        {
            StereoSplitRendererFeature.IsActive = false;
            s_activeInstance = null;
        }
    }

    private void OnDestroy()
    {
        if (s_activeInstance == this)
        {
            StereoSplitRendererFeature.IsActive = false;
            s_activeInstance = null;
        }
    }

    public void SetStereoEnabled(bool enabled)
    {
        stereoEnabled = enabled;
    }
}
