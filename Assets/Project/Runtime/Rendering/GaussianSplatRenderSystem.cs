// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// Per-frame GPU render logic for ALL active splat renderers, as ONE global
// depth-sorted stream. Runtime-safe.
//
// Owns shared GLOBAL buffers sized to the total splat count across every cloud.
// Each frame: assign each cloud a contiguous slice [offset, offset+count) of the
// global buffers -> dispatch its decode/project compute into that slice (writing
// its GLOBAL index as the sort payload) -> ONE sort (selectable backend) ->
// ONE procedural draw over all splats in global back-to-front order. Splats from
// different clouds therefore interleave correctly by depth (interpenetration).
//
// Buffers grow to the peak total and are reused (no per-frame allocation).

using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatVR.Runtime
{
    public static class GaussianSplatRenderSystem
    {
        public enum SortBackend { FFX, DeviceRadix }

        // Matches struct SplatViewData in SplatDecode.compute and RenderSplats.shader (40 B).
        [StructLayout(LayoutKind.Sequential)]
        public struct SplatViewData
        {
            public Vector4 pos;            // world centre (xyz) + valid flag (w)
            public Vector2 axis1, axis2;   // 2D ellipse semi-axes (screen px), centre eye
            public uint colorX, colorY;    // rgba packed as 4x fp16
        }
        public static readonly int SizeViewData = Marshal.SizeOf<SplatViewData>();

        const int kFfxBlock = 512;

        static readonly int k_Pos    = Shader.PropertyToID("_SplatPos");
        static readonly int k_Other  = Shader.PropertyToID("_SplatOther");
        static readonly int k_Color  = Shader.PropertyToID("_SplatColor");
        static readonly int k_SH     = Shader.PropertyToID("_SplatSH");
        static readonly int k_Chunks = Shader.PropertyToID("_SplatChunks");
        static readonly int k_View   = Shader.PropertyToID("_SplatViewData");
        static readonly int k_Keys   = Shader.PropertyToID("_SplatSortKeys");
        static readonly int k_Dist   = Shader.PropertyToID("_SplatSortDistances");
        static readonly int k_Order  = Shader.PropertyToID("_OrderBuffer");
        static readonly int k_Screen = Shader.PropertyToID("_SplatScreenParams");
        static readonly int k_Offset = Shader.PropertyToID("_SplatOffset");
        // chunk frustum culling
        static readonly int k_ChunkVisible = Shader.PropertyToID("_ChunkVisible");
        static readonly int k_CullEnabled  = Shader.PropertyToID("_CullEnabled");
        static readonly int k_ChunkVisBase = Shader.PropertyToID("_ChunkVisBase");

        static readonly MaterialPropertyBlock s_Mpb = new();
        static readonly List<GaussianSplatRenderer> s_Active = new();   // valid clouds this frame (cached, no GC)
        static GaussianSplatFfxSort s_Ffx;
        static GaussianSplatRadixSort s_Radix;

        // shared global buffers
        static GraphicsBuffer s_View, s_Keys, s_Dist;
        static GaussianSplatFfxSort.SupportResources s_FfxRes;
        static GaussianSplatRadixSort.SupportResources s_RadixRes;
        static int s_ActiveBackend = -1;   // which backend's scratch is allocated
        static int s_Capacity;   // total the global buffers are currently sized for

        // --- chunk frustum culling (simple + safe: a per-chunk visible flag) ---
        static GraphicsBuffer s_ChunkVisible;
        // Bound to _ChunkVisible when culling is OFF. It must NOT be one of the buffers
        // already bound as a UAV in the same dispatch: _ChunkVisible is an SRV and
        // _SplatSortKeys is a UAV, and binding one buffer as both in a single dispatch is
        // undefined (D3D12/Vulkan can only put a resource in one state at a time).
        static GraphicsBuffer s_ChunkVisibleDummy;
        static int s_ChunkCapacity;
        static readonly Plane[] s_Planes = new Plane[6];
        static readonly Vector4[] s_PlaneVecs = new Vector4[6];

        public static bool HasAnySplats()
        {
            foreach (var r in GaussianSplatRenderer.All)
                if (r.IsValid && r.SplatCount > 0) return true;
            return false;
        }

        public static void RenderAll(CommandBuffer cmd, Camera cam, ComputeShader decodeCS, ComputeShader sortCS,
            SortBackend backend, Material mat, int shOrder, ComputeShader cullCS, bool useCulling, float cullMargin, bool flipProj)
        {
            if (decodeCS == null || mat == null) return;

            // 1) Snapshot the valid clouds + total ONCE, so the offsets, the sort
            //    count and the draw count are all taken from the same set (robust to
            //    a cloud enabling/disabling between calls).
            s_Active.Clear();
            int total = 0;
            foreach (var r in GaussianSplatRenderer.All)
                if (r.IsValid && r.SplatCount > 0) { s_Active.Add(r); total += r.SplatCount; }
            if (total == 0) return;

            bool canSort;
            if (backend == SortBackend.FFX)
            {
                if (sortCS != null && (s_Ffx == null || s_Ffx.Compute != sortCS)) s_Ffx = new GaussianSplatFfxSort(sortCS);
                canSort = s_Ffx != null && s_Ffx.Valid;
            }
            else
            {
                if (sortCS != null && (s_Radix == null || s_Radix.Compute != sortCS)) s_Radix = new GaussianSplatRadixSort(sortCS);
                canSort = s_Radix != null && s_Radix.Valid;
            }
            WarnIfCannotSort(canSort, backend, sortCS);

            EnsureCapacity(total, backend);

            // Centre-eye matrices, computed once.
            Matrix4x4 view = cam.worldToCameraMatrix;
            Matrix4x4 proj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, flipProj);
            var screen = new Vector4(cam.pixelWidth, cam.pixelHeight, 0, 0);

            // Culling: needs the cull shader + EVERY cloud chunked (a Float32/Very High
            // asset has no chunks). Otherwise it safely does nothing.
            bool cull = useCulling && cullCS != null;
            if (cull)
                foreach (var r in s_Active)
                    if (r.ChunkBuffer == null) { cull = false; break; }

            if (cull)
            {
                EnsureCullBuffer(s_Active);
                RecordChunkCull(cmd, cam, cullCS, cullMargin);   // one thread per chunk -> visible flag
            }

            // 2) decode + project each cloud into its slice of the global buffers.
            //    Culled splats early-out inside the kernel (no decode, no SH, no fragments).
            int offset = 0, visBase = 0;
            foreach (var r in s_Active)
            {
                RecordCalcView(cmd, r, decodeCS, cam, view, proj, shOrder, offset, cull, visBase);
                offset  += r.SplatCount;
                visBase += (r.SplatCount + 255) / 256;
            }

            // 3) ONE global sort (back-to-front). Culled slots carry key 0xFFFFFFFF
            //    and sort harmlessly to the back.
            if (canSort)
            {
                if (backend == SortBackend.FFX) s_Ffx.Dispatch(cmd, s_Dist, s_Keys, (uint)total, s_FfxRes);
                else                            s_Radix.Dispatch(cmd, s_Dist, s_Keys, (uint)total, s_RadixRes);
            }

            // 4) ONE draw over the sorted stream. Culled splats have pos.w = 0, so the
            //    vertex shader discards them and they cost no fragments.
            s_Mpb.Clear();
            s_Mpb.SetBuffer(k_View, s_View);
            s_Mpb.SetBuffer(k_Order, s_Keys);
            s_Mpb.SetVector(k_Screen, screen);
            cmd.DrawProcedural(Matrix4x4.identity, mat, 0, MeshTopology.Triangles, 6, total, s_Mpb);
        }

        // Without a sort the draw still runs, but in asset (Morton) order rather than
        // depth order -- transparency blends wrong and it looks like a rendering bug
        // rather than a missing feature. That silence cost a lot of debugging, so say it
        // once, loudly, with the actual reason.
        //
        // BOTH backends need Shader Model 6.0 wave intrinsics (`#pragma require
        // wavebasic` / `waveballot`). Direct3D11 tops out at SM 5.0, so every kernel
        // reports unsupported and the whole sort silently vanishes. Windows must run on
        // Direct3D12 (or Vulkan); Meta lists both as supported for Link in OVRManager.
        static bool s_WarnedNoSort;
        static void WarnIfCannotSort(bool canSort, SortBackend backend, ComputeShader sortCS)
        {
            if (canSort) { s_WarnedNoSort = false; return; }
            if (s_WarnedNoSort) return;
            s_WarnedNoSort = true;

            string why = sortCS == null
                ? $"no sort compute shader is assigned for the {backend} backend on the URP renderer feature"
                : $"'{sortCS.name}' reports unsupported on {SystemInfo.graphicsDeviceType}. Both sort backends " +
                  "need Shader Model 6.0 wave intrinsics; Direct3D11 only reaches SM 5.0. Set Player Settings > " +
                  "Graphics APIs for Windows to Direct3D12 and restart the Editor.";

            Debug.LogError($"[GaussianSplatVR] Splats are rendering UNSORTED -- {why}");
        }

        // One thread per chunk: dilated AABB vs the centre-eye frustum -> 0/1 flag.
        static void RecordChunkCull(CommandBuffer cmd, Camera cam, ComputeShader cullCS, float cullMargin)
        {
            // The eyes are ~65 mm apart -- far less than the margin -- so the centre
            // frustum plus the margin conservatively covers both eyes.
            GeometryUtility.CalculateFrustumPlanes(cam, s_Planes);
            for (int i = 0; i < 6; i++)
                s_PlaneVecs[i] = new Vector4(s_Planes[i].normal.x, s_Planes[i].normal.y, s_Planes[i].normal.z, s_Planes[i].distance);

            int k = cullCS.FindKernel("CSCullChunks");
            cmd.SetComputeVectorArrayParam(cullCS, "_FrustumPlanes", s_PlaneVecs);
            cmd.SetComputeFloatParam(cullCS, "_CullMargin", cullMargin);
            cmd.SetComputeBufferParam(cullCS, k, k_ChunkVisible, s_ChunkVisible);

            int visBase = 0;
            foreach (var r in s_Active)
            {
                int chunks = (r.SplatCount + 255) / 256;
                cmd.SetComputeBufferParam(cullCS, k, k_Chunks, r.ChunkBuffer);
                cmd.SetComputeIntParam(cullCS, "_ChunkCount", chunks);
                cmd.SetComputeIntParam(cullCS, k_ChunkVisBase, visBase);
                cmd.SetComputeMatrixParam(cullCS, "_CullObjectToWorld", r.transform.localToWorldMatrix);
                cmd.DispatchCompute(cullCS, k, (chunks + 63) / 64, 1, 1);
                visBase += chunks;
            }
        }

        static void EnsureCullBuffer(List<GaussianSplatRenderer> active)
        {
            int chunks = 0;
            foreach (var r in active) chunks += (r.SplatCount + 255) / 256;
            if (s_ChunkVisible != null && chunks <= s_ChunkCapacity) return;
            s_ChunkVisible?.Dispose();
            s_ChunkVisible = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, chunks), 4) { name = "GS_chunkVisible" };
            s_ChunkCapacity = chunks;
        }

        // (Re)allocate the global buffers; grow-only, so toggling clouds doesn't churn.
        static void EnsureCapacity(int total, SortBackend backend)
        {
            if (s_View != null && total <= s_Capacity && (int)backend == s_ActiveBackend) return;
            DisposeGlobal();
            s_Capacity = total;
            s_ActiveBackend = (int)backend;
            int sortLen = (total + kFfxBlock - 1) / kFfxBlock * kFfxBlock;
            var t = GraphicsBuffer.Target.Structured;
            s_View = new GraphicsBuffer(t, total, SizeViewData) { name = "GS_globalView" };
            s_Keys = new GraphicsBuffer(t, sortLen, 4) { name = "GS_globalKeys" };
            s_Dist = new GraphicsBuffer(t, sortLen, 4) { name = "GS_globalDist" };
            if (backend == SortBackend.FFX) s_FfxRes = GaussianSplatFfxSort.SupportResources.Load(total);
            else                            s_RadixRes = GaussianSplatRadixSort.SupportResources.Load(total);
        }

        static void DisposeGlobal()
        {
            s_ChunkVisible?.Dispose(); s_ChunkVisible = null; s_ChunkCapacity = 0;
            s_ChunkVisibleDummy?.Dispose(); s_ChunkVisibleDummy = null;
            s_View?.Dispose(); s_Keys?.Dispose(); s_Dist?.Dispose();
            s_View = s_Keys = s_Dist = null;
            s_FfxRes.Dispose();
            s_RadixRes.Dispose();
            s_Capacity = 0;
            s_ActiveBackend = -1;
        }

        /// <summary>Free the shared buffers (call from the feature's Dispose).</summary>
        public static void Dispose() { DisposeGlobal(); s_Ffx = null; s_Radix = null; }

        // Records CSCalcView for one cloud, writing into the global buffers at [offset, offset+count).
        static void RecordCalcView(CommandBuffer cmd, GaussianSplatRenderer r, ComputeShader cs, Camera cam, Matrix4x4 view, Matrix4x4 proj, int shOrder, int offset, bool cull, int visBase)
        {
            int kk = cs.FindKernel("CSCalcView");
            SetupCalcViewParams(cmd, r, cs, kk, cam, view, proj, shOrder, offset);

            // culling flags (bind a valid buffer even when off -- some drivers dislike null)
            if (!cull && s_ChunkVisibleDummy == null)
                s_ChunkVisibleDummy = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4) { name = "GS_chunkVisibleDummy" };
            cmd.SetComputeBufferParam(cs, kk, k_ChunkVisible, cull ? s_ChunkVisible : s_ChunkVisibleDummy);
            cmd.SetComputeIntParam(cs, k_CullEnabled, cull ? 1 : 0);
            cmd.SetComputeIntParam(cs, k_ChunkVisBase, visBase);

            cmd.DispatchCompute(cs, kk, (r.SplatCount + 63) / 64, 1, 1);
        }

        // Shared buffer/uniform setup for BOTH CSCalcView and CSCalcViewCulled.
        static void SetupCalcViewParams(CommandBuffer cmd, GaussianSplatRenderer r, ComputeShader cs, int k, Camera cam, Matrix4x4 view, Matrix4x4 proj, int shOrder, int offset)
        {
            var a = r.Asset;
            bool uc = r.ChunkBuffer != null;

            cmd.SetComputeBufferParam(cs, k, k_Pos, r.PosBuffer);
            cmd.SetComputeBufferParam(cs, k, k_Other, r.OtherBuffer);
            cmd.SetComputeBufferParam(cs, k, k_Color, r.ColorBuffer);
            cmd.SetComputeBufferParam(cs, k, k_SH, r.SHBuffer);
            cmd.SetComputeBufferParam(cs, k, k_Chunks, uc ? r.ChunkBuffer : r.PosBuffer);   // placeholder when unused
            cmd.SetComputeBufferParam(cs, k, k_View, s_View);
            cmd.SetComputeBufferParam(cs, k, k_Keys, s_Keys);
            cmd.SetComputeBufferParam(cs, k, k_Dist, s_Dist);

            cmd.SetComputeIntParam(cs, "_SplatCount", a.splatCount);
            cmd.SetComputeIntParam(cs, k_Offset, offset);
            cmd.SetComputeIntParam(cs, "_UseChunks", uc ? 1 : 0);
            cmd.SetComputeIntParam(cs, "_PosFmt", (int)a.posFormat);
            cmd.SetComputeIntParam(cs, "_ScaleFmt", (int)a.scaleFormat);
            cmd.SetComputeIntParam(cs, "_RotFmt", (int)a.rotFormat);
            cmd.SetComputeIntParam(cs, "_ColorFmt", (int)a.colorFormat);
            cmd.SetComputeIntParam(cs, "_SHFmt", (int)a.shFormat);
            cmd.SetComputeIntParam(cs, "_PosStride", GaussianSplatAsset.GetVectorSize(a.posFormat));
            cmd.SetComputeIntParam(cs, "_OtherStride", GaussianSplatAsset.GetOtherSize(a.rotFormat, a.scaleFormat));
            cmd.SetComputeIntParam(cs, "_ColorStride", GaussianSplatAsset.GetColorSize(a.colorFormat));
            cmd.SetComputeIntParam(cs, "_SHStride", GaussianSplatAsset.GetSHSize(a.shFormat));
            cmd.SetComputeIntParam(cs, "_RotSize", GaussianSplatAsset.GetRotationSize(a.rotFormat));

            Matrix4x4 o2w = r.transform.localToWorldMatrix, w2o = r.transform.worldToLocalMatrix;
            cmd.SetComputeMatrixParam(cs, "_MatrixObjectToWorld", o2w);
            cmd.SetComputeMatrixParam(cs, "_MatrixWorldToObject", w2o);
            cmd.SetComputeMatrixParam(cs, "_MatrixMV", view * o2w);
            cmd.SetComputeMatrixParam(cs, "_MatrixP", proj);
            cmd.SetComputeVectorParam(cs, "_VecScreenParams", new Vector4(cam.pixelWidth, cam.pixelHeight, 1f + 1f/cam.pixelWidth, 1f + 1f/cam.pixelHeight));
            cmd.SetComputeVectorParam(cs, "_VecWorldSpaceCameraPos", cam.transform.position);
            cmd.SetComputeIntParam(cs, "_SHOrder", Mathf.Clamp(shOrder, 0, 3));
            cmd.SetComputeFloatParam(cs, "_SplatScale", 1f);
            cmd.SetComputeFloatParam(cs, "_SplatOpacityScale", 1f);
        }
    }
}
