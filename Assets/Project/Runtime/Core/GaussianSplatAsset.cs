// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// The runtime splat asset: compact binary blobs (as TextAssets) plus the
// per-attribute formats needed to decode them. Loaded on every platform,
// including the Quest player, so it holds no editor references.
//
// Blob layout:
//   _pos   positions
//   _other rotation + scale (interleaved: rotation, then scale)
//   _col   colour + opacity   (linear blob -> GraphicsBuffer, no BC7)
//   _sh    per-splat SH bands 1-3
//   _chunk per-chunk min/max bounds; present only for quantized tiers
//          (absent for a fully-Float32 asset, which needs no de-normalisation)

using System;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatVR.Runtime
{
    public class GaussianSplatAsset : ScriptableObject
    {
        public const int kCurrentVersion = 2026_01_01;
        public const int kChunkSize = 256;              // Morton reorder -> chunks of 256

        public enum VectorFormat   { Float32, Norm16, Norm11, Norm6 }   // positions, scale
        public enum RotationFormat { Float32, Norm10 }                  // quaternion / smallest-3
        public enum ColorFormat    { Float32x4, Float16x4, Norm8x4 }    // rgba (no BC7)
        public enum SHFormat       { Float32, Float16, Norm11, Norm6 }  // per-splat SH rest

        // Byte sizes per splat, per format.
        public static int GetVectorSize(VectorFormat f) => f switch
        { VectorFormat.Float32 => 12, VectorFormat.Norm16 => 6, VectorFormat.Norm11 => 4, VectorFormat.Norm6 => 2, _ => throw Bad(f) };

        public static int GetRotationSize(RotationFormat f) => f switch
        { RotationFormat.Float32 => 16, RotationFormat.Norm10 => 4, _ => throw Bad(f) };

        public static int GetColorSize(ColorFormat f) => f switch
        { ColorFormat.Float32x4 => 16, ColorFormat.Float16x4 => 8, ColorFormat.Norm8x4 => 4, _ => throw Bad(f) };

        // 15 SH coeffs x 3 channels, padded for GPU alignment.
        public static int GetSHSize(SHFormat f) => f switch
        {
            SHFormat.Float32 => 192,   // 16 x float3
            SHFormat.Float16 => 96,    // 16 x half3
            SHFormat.Norm11  => 60,    // 15 x uint
            SHFormat.Norm6   => 32,    // 16 x ushort
            _ => throw Bad(f)
        };

        // _other = rotation followed by scale.
        public static int  GetOtherSize(RotationFormat rot, VectorFormat scl) => GetRotationSize(rot) + GetVectorSize(scl);
        public static long CalcPosDataSize  (int n, VectorFormat pos)                     => (long)n * GetVectorSize(pos);
        public static long CalcOtherDataSize(int n, RotationFormat rot, VectorFormat scl) => (long)n * GetOtherSize(rot, scl);
        public static long CalcColorDataSize(int n, ColorFormat col)                      => (long)n * GetColorSize(col);
        public static long CalcSHDataSize   (int n, SHFormat sh)                          => (long)n * GetSHSize(sh);
        public static long CalcChunkDataSize(int n) => (long)((n + kChunkSize - 1) / kChunkSize) * UnsafeUtility.SizeOf<ChunkInfo>();

        // Per-chunk min/max bounds used to de-normalise quantized attributes (104 B).
        // Full-float layout; the writer and the GPU decoder must agree on it.
        public struct ChunkInfo
        {
            public float3 posMin, posMax;
            public float3 sclMin, sclMax;
            public float4 colMin, colMax;
            public float3 shMin,  shMax;
        }

        [SerializeField] int m_FormatVersion;
        [SerializeField] int m_SplatCount;
        [SerializeField] Vector3 m_BoundsMin, m_BoundsMax;
        [SerializeField] Hash128 m_DataHash;
        [SerializeField] VectorFormat   m_PosFormat   = VectorFormat.Norm11;
        [SerializeField] VectorFormat   m_ScaleFormat = VectorFormat.Norm6;
        [SerializeField] RotationFormat m_RotFormat   = RotationFormat.Norm10;
        [SerializeField] ColorFormat    m_ColorFormat = ColorFormat.Norm8x4;
        [SerializeField] SHFormat       m_SHFormat    = SHFormat.Norm6;
        [SerializeField] TextAsset m_PosData, m_OtherData, m_ColorData, m_SHData, m_ChunkData;

        public int formatVersion => m_FormatVersion;
        public int splatCount => m_SplatCount;
        public Vector3 boundsMin => m_BoundsMin;
        public Vector3 boundsMax => m_BoundsMax;
        public Hash128 dataHash => m_DataHash;
        public VectorFormat   posFormat   => m_PosFormat;
        public VectorFormat   scaleFormat => m_ScaleFormat;
        public RotationFormat rotFormat   => m_RotFormat;
        public ColorFormat    colorFormat => m_ColorFormat;
        public SHFormat       shFormat    => m_SHFormat;
        public TextAsset posData   => m_PosData;
        public TextAsset otherData => m_OtherData;
        public TextAsset colorData => m_ColorData;
        public TextAsset shData    => m_SHData;
        public TextAsset chunkData => m_ChunkData;   // null when the asset has no chunks

        public void Initialize(int splats, Vector3 bMin, Vector3 bMax,
            VectorFormat pos, VectorFormat scale, RotationFormat rot, ColorFormat col, SHFormat sh)
        {
            m_FormatVersion = kCurrentVersion;
            m_SplatCount = splats;
            m_BoundsMin = bMin; m_BoundsMax = bMax;
            m_PosFormat = pos; m_ScaleFormat = scale; m_RotFormat = rot; m_ColorFormat = col; m_SHFormat = sh;
        }

        public void SetDataHash(Hash128 hash) => m_DataHash = hash;

        public void SetAssetFiles(TextAsset chunk, TextAsset pos, TextAsset other, TextAsset color, TextAsset sh)
        {
            m_ChunkData = chunk; m_PosData = pos; m_OtherData = other; m_ColorData = color; m_SHData = sh;
        }

        static ArgumentOutOfRangeException Bad(object f) => new("format", f, null);
    }
}
