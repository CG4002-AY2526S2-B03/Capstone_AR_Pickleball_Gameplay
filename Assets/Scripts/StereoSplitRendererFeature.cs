using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;

/// <summary>
/// URP ScriptableRendererFeature that splits the final rendered frame into
/// side-by-side left/right eye views for phone-based VR headsets.
///
/// Runs after all rendering so the AR camera background and all 3D content
/// are already composited. Does NOT touch camera.targetTexture, so AR Foundation
/// keeps working normally.
///
/// Add this feature to your URP Renderer Data asset (e.g. URP-Performant-Renderer).
/// Assign the StereoSplit shader in the Inspector so it survives build stripping.
/// Toggle at runtime via the static <see cref="IsActive"/> property.
/// </summary>
public class StereoSplitRendererFeature : ScriptableRendererFeature
{
    [Tooltip("Assign Assets/Shaders/StereoSplit.shader here. " +
             "This creates a hard reference so the shader is included in builds.")]
    [SerializeField] private Shader stereoSplitShader;

    // ── Static control (set by StereoscopicAR at runtime) ──────────────────────
    public static bool IsActive { get; set; }
    public static float UVShift { get; set; } = 0.01f;
    public static float DividerWidth { get; set; } = 0.003f;

    private StereoSplitPass m_Pass;
    private Material m_Material;
    private bool m_CreateFailed;
    private static bool s_AddPassLoggedOnce;

    public override void Create()
    {
        m_CreateFailed = false;
        s_AddPassLoggedOnce = false;

        Shader shader = stereoSplitShader;
        if (shader == null)
            shader = Shader.Find("Hidden/StereoSplit");

        if (shader == null)
        {
            Debug.LogWarning("[StereoSplit] Shader not found. " +
                             "Assign the StereoSplit shader on the renderer feature in the Inspector.");
            m_CreateFailed = true;
            return;
        }

        m_Material = CoreUtils.CreateEngineMaterial(shader);
        if (m_Material == null)
        {
            Debug.LogError("[StereoSplit] Failed to create material from shader.");
            m_CreateFailed = true;
            return;
        }

        m_Pass = new StereoSplitPass(m_Material);
        Debug.Log("[StereoSplit] Renderer feature created successfully.");
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_CreateFailed || m_Pass == null || m_Material == null)
            return;

        if (!IsActive)
            return;

        if (renderingData.cameraData.cameraType != CameraType.Game)
            return;

        m_Material.SetFloat("_StereoUVShift", UVShift);
        m_Material.SetFloat("_StereoDividerWidth", DividerWidth);

        renderer.EnqueuePass(m_Pass);

        if (!s_AddPassLoggedOnce)
        {
            Debug.Log("[StereoSplit] Pass enqueued for camera: " +
                      renderingData.cameraData.camera.name);
            s_AddPassLoggedOnce = true;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (m_Material != null)
        {
            CoreUtils.Destroy(m_Material);
            m_Material = null;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════

    class StereoSplitPass : ScriptableRenderPass
    {
        private Material m_Material;
        private static bool s_LoggedOnce;

        public StereoSplitPass(Material material)
        {
            m_Material = material;
            renderPassEvent = RenderPassEvent.AfterRendering;
            requiresIntermediateTexture = true;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData = frameData.Get<UniversalResourceData>();

            if (!s_LoggedOnce)
            {
                Debug.Log($"[StereoSplit] RecordRenderGraph called. " +
                          $"cameraColor valid={resourceData.cameraColor.IsValid()}, " +
                          $"activeColor valid={resourceData.activeColorTexture.IsValid()}");
            }

            if (!resourceData.cameraColor.IsValid())
            {
                if (!s_LoggedOnce)
                {
                    Debug.LogWarning("[StereoSplit] cameraColor is not valid — pass skipped.");
                    s_LoggedOnce = true;
                }
                return;
            }

            if (!s_LoggedOnce)
            {
                Debug.Log("[StereoSplit] Render pass executing — stereo split active.");
                s_LoggedOnce = true;
            }

            TextureHandle source = resourceData.activeColorTexture;

            // Create destination texture for stereo split output
            var desc = renderGraph.GetTextureDesc(resourceData.cameraColor);
            desc.name = "_StereoSplitDest";
            desc.clearBuffer = false;

            TextureHandle destination = renderGraph.CreateTexture(desc);

            // Blit source through stereo-split material into destination
            var blitParams = new RenderGraphUtils.BlitMaterialParameters(
                source, destination, m_Material, 0);

            renderGraph.AddBlitPass(blitParams, passName: "StereoSplit Blit");

            // Replace the camera color so URP's FinalBlit uses our output
            resourceData.cameraColor = destination;
        }
    }
}
