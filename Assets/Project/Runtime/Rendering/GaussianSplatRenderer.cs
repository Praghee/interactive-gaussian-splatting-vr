// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// Owns a GaussianSplatAsset's static GPU residency: the compact blobs uploaded
// once as ByteAddressBuffers. Runtime/Quest-safe.
//
// Per-frame view/sort buffers live in GaussianSplatRenderSystem's shared global
// buffers so every cloud sorts and draws as one depth-ordered stream; this
// component holds only the read-only decode inputs.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatVR.Runtime
{
    [ExecuteAlways]
    [AddComponentMenu("Gaussian Splat/Gaussian Splat Renderer")]
    public class GaussianSplatRenderer : MonoBehaviour
    {
        [Header("Source")]
        [SerializeField] GaussianSplatAsset m_Asset;

        GraphicsBuffer m_GpuChunk, m_GpuPos, m_GpuOther, m_GpuColor, m_GpuSH;

        int m_SplatCount;
        bool m_Ready, m_Dirty;

        public GaussianSplatAsset Asset { get => m_Asset; set { m_Asset = value; m_Dirty = true; } }
        public bool IsValid => m_Ready;
        public int SplatCount => m_SplatCount;

        public GraphicsBuffer ChunkBuffer => m_GpuChunk;   // null when the asset has no chunks
        public GraphicsBuffer PosBuffer   => m_GpuPos;
        public GraphicsBuffer OtherBuffer => m_GpuOther;
        public GraphicsBuffer ColorBuffer => m_GpuColor;
        public GraphicsBuffer SHBuffer    => m_GpuSH;

        static readonly HashSet<GaussianSplatRenderer> s_All = new();
        public static IReadOnlyCollection<GaussianSplatRenderer> All => s_All;

        void OnEnable()  { s_All.Add(this); CreateResources(); }
        void OnDisable() { s_All.Remove(this); DisposeResources(); }
        void OnValidate() => m_Dirty = true;
        void Update() { if (m_Dirty) { m_Dirty = false; CreateResources(); } }

        [ContextMenu("Reload GPU Buffers")]
        void CreateResources()
        {
            DisposeResources();
            if (m_Asset == null) return;
            if (!Validate(m_Asset, out string err)) { Debug.LogError($"[GaussianSplatVR] {name}: {err}", this); return; }

            m_SplatCount = m_Asset.splatCount;
            m_GpuPos   = CreateRawBuffer(m_Asset.posData.bytes,   "pos");
            m_GpuOther = CreateRawBuffer(m_Asset.otherData.bytes, "other");
            m_GpuColor = CreateRawBuffer(m_Asset.colorData.bytes, "color");
            m_GpuSH    = CreateRawBuffer(m_Asset.shData.bytes,    "sh");
            m_GpuChunk = m_Asset.chunkData != null ? CreateRawBuffer(m_Asset.chunkData.bytes, "chunk") : null;
            m_Ready = true;
        }

        void DisposeResources()
        {
            m_Ready = false; m_SplatCount = 0;
            Release(ref m_GpuChunk); Release(ref m_GpuPos); Release(ref m_GpuOther); Release(ref m_GpuColor); Release(ref m_GpuSH);
        }

        static void Release(ref GraphicsBuffer b) { b?.Dispose(); b = null; }

        // Raw ByteAddressBuffer sized to ceil(bytes/4) uints; tail (<4 B) is zero-padded.
        static GraphicsBuffer CreateRawBuffer(byte[] data, string tag)
        {
            int uintCount = Mathf.Max(1, (data.Length + 3) / 4);
            var buf = new GraphicsBuffer(GraphicsBuffer.Target.Raw, uintCount, 4) { name = $"GS_{tag}" };
            uint[] words = new uint[uintCount];
            Buffer.BlockCopy(data, 0, words, 0, data.Length);
            buf.SetData(words);
            return buf;
        }

        static bool Validate(GaussianSplatAsset a, out string err)
        {
            err = null;
            if (a.splatCount <= 0) { err = "asset has 0 splats"; return false; }
            if (a.posData == null || a.otherData == null || a.colorData == null || a.shData == null)
            { err = "asset is missing one or more data blobs"; return false; }
            if (a.formatVersion != GaussianSplatAsset.kCurrentVersion)
                Debug.LogWarning($"[GaussianSplatVR] asset '{a.name}' format v{a.formatVersion} != current v{GaussianSplatAsset.kCurrentVersion}");
            return true;
        }
    }
}
