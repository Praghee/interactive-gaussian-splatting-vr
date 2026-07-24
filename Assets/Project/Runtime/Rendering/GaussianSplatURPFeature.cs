// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// URP ScriptableRendererFeature (RenderGraph) that draws all active splat
// renderers. AddUnsafePass lets one pass dispatch the compute + sort and issue
// the procedural draw. Add to PC_Renderer + Mobile_Renderer and assign:
//   Compute           -> SplatDecode.compute
//   Sort Compute Radix-> SplatSortRadix.compute   (the default backend)
//   Sort Compute FFX  -> SplatSortFFX.compute     (alternative backend)
//   Splat Shader      -> RenderSplats.shader
//   Cull Compute      -> SplatCull.compute        (only used when Use Culling is on)
//
// BOTH sort backends need Shader Model 6.0 wave intrinsics, so the graphics API
// must be Direct3D12 or Vulkan -- on Direct3D11 every sort kernel reports
// unsupported and splats render unsorted. GaussianSplatRenderSystem logs an error
// naming the device if that happens.

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace GaussianSplatVR.Runtime
{
    public class GaussianSplatURPFeature : ScriptableRendererFeature
    {
        [SerializeField] ComputeShader m_Compute;      // SplatDecode.compute
        [Header("Sorting")]
        [Tooltip("DeviceRadix is much faster on Quest. FFX is the alternative backend.")]
        [SerializeField] GaussianSplatRenderSystem.SortBackend m_SortBackend = GaussianSplatRenderSystem.SortBackend.DeviceRadix;
        [SerializeField] ComputeShader m_SortComputeRadix;  // SplatSortRadix.compute
        [SerializeField] ComputeShader m_SortComputeFFX;    // SplatSortFFX.compute (alternative backend)
        [SerializeField] Shader m_SplatShader;         // RenderSplats.shader
        [SerializeField, Range(0, 3)] int m_SHOrder = 3;
        [SerializeField, Range(0f, 1f)] float m_SoftDepthFade = 0.15f;   // metres; 0 = hard occlusion
        [SerializeField] bool m_ColorToLinear = true;   // splats are gamma-trained; convert in a Linear project

        [Header("Chunk frustum culling")]
        [Tooltip("Skip 256-splat chunks outside the view. Big win in room-scale scenes (kitchen); no gain when a cloud fills the view (apple). Needs a CHUNKED asset -- i.e. any preset EXCEPT Very High -- and the cull compute assigned; otherwise it safely does nothing.")]
        [SerializeField] bool m_UseCulling = false;
        [SerializeField] ComputeShader m_CullCompute;   // SplatCull.compute
        [Tooltip("Metres to dilate each chunk's bounds before the frustum test. Chunk bounds cover splat CENTRES, so a margin prevents big splats popping at the view edge. Raise if you see popping; lower to cull more.")]
        [SerializeField, Range(0f, 1f)] float m_CullMargin = 0.25f;

        [SerializeField] RenderPassEvent m_Event = RenderPassEvent.BeforeRenderingTransparents;

        Material m_Material;
        GSPass m_Pass;

        public override void Create()
        {
            m_Pass = new GSPass();
            if (m_SplatShader != null) m_Material = CoreUtils.CreateEngineMaterial(m_SplatShader);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_Compute == null || m_Material == null || !GaussianSplatRenderSystem.HasAnySplats()) return;
            m_Material.SetFloat("_SplatSoftFade", m_SoftDepthFade);
            m_Material.SetFloat("_SplatColorToLinear", m_ColorToLinear ? 1f : 0f);
            m_Pass.renderPassEvent = m_Event;
            ComputeShader sortCS = m_SortBackend == GaussianSplatRenderSystem.SortBackend.FFX
                ? m_SortComputeFFX : m_SortComputeRadix;
            m_Pass.Setup(m_Compute, sortCS, m_SortBackend, m_Material, m_SHOrder, m_CullCompute, m_UseCulling, m_CullMargin);
            renderer.EnqueuePass(m_Pass);
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(m_Material); m_Material = null; m_Pass = null;
            GaussianSplatRenderSystem.Dispose();   // free the shared global buffers
        }

        class GSPass : ScriptableRenderPass
        {
            ComputeShader m_CS, m_SortCS, m_CullCS;
            GaussianSplatRenderSystem.SortBackend m_Backend;
            Material m_Mat;
            int m_SHOrder;
            bool m_UseCulling;
            float m_CullMargin;

            public void Setup(ComputeShader cs, ComputeShader sortCS, GaussianSplatRenderSystem.SortBackend backend, Material mat, int shOrder,
                ComputeShader cullCS, bool useCulling, float cullMargin)
            { m_CS = cs; m_SortCS = sortCS; m_Backend = backend; m_Mat = mat; m_SHOrder = shOrder;
              m_CullCS = cullCS; m_UseCulling = useCulling; m_CullMargin = cullMargin; }

            class PassData
            {
                public ComputeShader cs, sortCS, cullCS;
                public GaussianSplatRenderSystem.SortBackend backend;
                public Material mat; public int shOrder; public Camera cam; public TextureHandle color, depth;
                public bool useCulling; public float cullMargin;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var camData = frameData.Get<UniversalCameraData>();
                if (camData.camera.cameraType == CameraType.Preview) return;
                var resData = frameData.Get<UniversalResourceData>();

                using var builder = renderGraph.AddUnsafePass<PassData>("GaussianSplats", out var pd);
                bool hasDepth = resData.activeDepthTexture.IsValid();
                pd.cs = m_CS; pd.sortCS = m_SortCS; pd.cullCS = m_CullCS; pd.backend = m_Backend; pd.mat = m_Mat; pd.shOrder = m_SHOrder;
                pd.useCulling = m_UseCulling; pd.cullMargin = m_CullMargin;
                pd.cam = camData.camera; pd.color = resData.activeColorTexture;
                pd.depth = hasDepth ? resData.activeDepthTexture : TextureHandle.nullHandle;

                builder.UseTexture(resData.activeColorTexture, AccessFlags.ReadWrite);
                if (hasDepth)
                    builder.UseTexture(resData.activeDepthTexture, AccessFlags.ReadWrite);   // write cloud front-surface depth
                if (resData.cameraDepthTexture.IsValid())
                    builder.UseTexture(resData.cameraDepthTexture, AccessFlags.Read);   // scene depth for splat occlusion
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext ctx) =>
                {
                    var cmd = CommandBufferHelpers.GetNativeCommandBuffer(ctx.cmd);
                    if (data.depth.IsValid()) CoreUtils.SetRenderTarget(cmd, data.color, data.depth);
                    else                      CoreUtils.SetRenderTarget(cmd, data.color);
                    GaussianSplatRenderSystem.RenderAll(cmd, data.cam, data.cs, data.sortCS, data.backend, data.mat, data.shOrder,
                        data.cullCS, data.useCulling, data.cullMargin, flipProj: true);
                });
            }
        }
    }
}
