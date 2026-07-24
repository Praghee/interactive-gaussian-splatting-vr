// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// Encodes activated splats into a GaussianSplatAsset. Editor-only.
// Quantized tiers: Morton reorder -> chunks of 256 -> per-chunk min/max ->
// normalise to [0,1] -> quantize. A fully-Float32 tier skips chunking (raw floats).
//
// Decode invariants the GPU decoder mirrors (SplatDecode.compute):
//   chunked attr  = lerp(chunkMin, chunkMax, unpacked[0,1])
//   scale (chunked)   : pow(lerped, 8)          inverse of pow(1/8)
//   opacity (chunked) : InvSquareCentered01(lerped)
//   rotation Float32 -> raw quaternion; Norm10 -> smallest-3 (10.10.10.2)

using System;
using System.IO;
using GaussianSplatVR.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using VectorFormat = GaussianSplatVR.Runtime.GaussianSplatAsset.VectorFormat;
using RotationFormat = GaussianSplatVR.Runtime.GaussianSplatAsset.RotationFormat;
using ColorFormat = GaussianSplatVR.Runtime.GaussianSplatAsset.ColorFormat;
using SHFormat = GaussianSplatVR.Runtime.GaussianSplatAsset.SHFormat;
using ChunkInfo = GaussianSplatVR.Runtime.GaussianSplatAsset.ChunkInfo;

namespace GaussianSplatVR.Editor
{
    public static class GaussianSplatAssetWriter
    {
        const int S = 62;   // floats per InputSplatData
        const int OPos = 0, ODc = 6, OSh = 9, OOpacity = 54, OScale = 55, ORot = 58;

        public struct WriteResult
        {
            public GaussianSplatAsset asset;
            public int chunkCount;    // 0 for a fully-Float32 (chunkless) asset
            public bool verifyOk;
            public float maxError;    // exact for Float32, else world-space position error
            public bool exact;        // true => bit-exact check ; false => position-decode check
        }

        public static unsafe WriteResult WriteAsset(NativeArray<InputSplatData> splats, Vector3 bMinV, Vector3 bMaxV,
            VectorFormat fPos, VectorFormat fScale, RotationFormat fRot, ColorFormat fCol, SHFormat fSH, string assetsFolder, string name)
        {
            int n = splats.Length;
            float3 bMin = bMinV, bMax = bMaxV;
            bool useChunks = fPos != VectorFormat.Float32 || fScale != VectorFormat.Float32
                          || fCol != ColorFormat.Float32x4 || fSH != SHFormat.Float32;

            ReorderMorton(splats, bMin, bMax);   // spatially-near splats adjacent => tight chunks

            // Snapshot reordered positions (managed => no native leak) for the lossy decode check.
            float3[] origPos = null;
            if (useChunks)
            {
                origPos = new float3[n];
                float* sp = (float*)splats.GetUnsafeReadOnlyPtr();
                for (int i = 0; i < n; i++) origPos[i] = new float3(sp[i*S+OPos], sp[i*S+OPos+1], sp[i*S+OPos+2]);
            }

            byte[] chunkB = null;
            int chunkCount = 0;
            if (useChunks)
            {
                chunkCount = (n + GaussianSplatAsset.kChunkSize - 1) / GaussianSplatAsset.kChunkSize;
                var chunks = new NativeArray<ChunkInfo>(chunkCount, Allocator.TempJob);
                new CalcChunkDataJob { splats = splats, chunks = chunks }.Schedule(chunkCount, 8).Complete();
                chunkB = new byte[chunkCount * UnsafeUtility.SizeOf<ChunkInfo>()];
                fixed (byte* d = chunkB) UnsafeUtility.MemCpy(d, chunks.GetUnsafeReadOnlyPtr(), chunkB.Length);
                chunks.Dispose();
            }

            byte[] posB   = EncodePositions(splats, fPos);
            byte[] otherB = EncodeOther(splats, fRot, fScale);
            byte[] colB   = EncodeColor(splats, fCol);
            byte[] shB    = EncodeSH(splats, fSH);

            string relDir = $"{assetsFolder}/{name}";
            Directory.CreateDirectory(ToAbsolute(relDir));
            TextAsset chunkTA = chunkB != null ? WriteBlob(relDir, $"{name}_chunk", chunkB) : null;
            TextAsset posTA   = WriteBlob(relDir, $"{name}_pos",   posB);
            TextAsset otherTA = WriteBlob(relDir, $"{name}_other", otherB);
            TextAsset colTA   = WriteBlob(relDir, $"{name}_col",   colB);
            TextAsset shTA    = WriteBlob(relDir, $"{name}_sh",    shB);

            string relAsset = $"{relDir}/{name}.asset";
            if (File.Exists(ToAbsolute(relAsset))) AssetDatabase.DeleteAsset(relAsset);
            var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
            asset.Initialize(n, bMinV, bMaxV, fPos, fScale, fRot, fCol, fSH);
            asset.SetAssetFiles(chunkTA, posTA, otherTA, colTA, shTA);
            asset.SetDataHash(Hash128.Compute($"{n}|{bMinV}|{bMaxV}|{fPos}{fScale}{fRot}{fCol}{fSH}|{name}"));
            AssetDatabase.CreateAsset(asset, relAsset);
            AssetDatabase.SaveAssets();

            var res = new WriteResult { asset = asset, chunkCount = chunkCount };
            if (!useChunks) { res.exact = true;  res.verifyOk = VerifyFloat32RoundTrip(asset, splats, out res.maxError); }
            else            { res.exact = false; res.verifyOk = VerifyPositionDecode(asset, origPos, out res.maxError); }
            return res;
        }

        // ---------- Morton reorder ----------
        [BurstCompile]
        struct MortonCodeJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<InputSplatData> splats;
            public float3 bMin, invSize;
            [WriteOnly] public NativeArray<ulong> codes;
            public void Execute(int i)
            {
                float3 p = ((float3)splats[i].pos - bMin) * invSize * ((1 << 21) - 1);
                codes[i] = GaussianUtils.MortonEncode3((uint3)math.clamp(p, 0, (1 << 21) - 1));
            }
        }
        static void ReorderMorton(NativeArray<InputSplatData> splats, float3 bMin, float3 bMax)
        {
            int n = splats.Length;
            var codes = new NativeArray<ulong>(n, Allocator.TempJob);
            new MortonCodeJob { splats = splats, bMin = bMin, invSize = 1.0f / math.max(bMax - bMin, 1e-9f), codes = codes }
                .Schedule(n, 4096).Complete();

            ulong[] codesM = new ulong[n];
            codes.CopyTo(codesM); codes.Dispose();
            int[] order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) => codesM[a].CompareTo(codesM[b]));

            var copy = new NativeArray<InputSplatData>(splats, Allocator.TempJob);
            for (int i = 0; i < n; i++) splats[i] = copy[order[i]];
            copy.Dispose();
        }

        // ---------- chunk bounds + in-place normalize ----------
        [BurstCompile]
        struct CalcChunkDataJob : IJobParallelFor
        {
            [NativeDisableParallelForRestriction] public NativeArray<InputSplatData> splats;
            [WriteOnly] public NativeArray<ChunkInfo> chunks;

            public void Execute(int chunkIdx)
            {
                float3 minP = float.PositiveInfinity, minS = float.PositiveInfinity, minH = float.PositiveInfinity;
                float4 minC = float.PositiveInfinity;
                float3 maxP = float.NegativeInfinity, maxS = float.NegativeInfinity, maxH = float.NegativeInfinity;
                float4 maxC = float.NegativeInfinity;

                int begin = math.min(chunkIdx * GaussianSplatAsset.kChunkSize, splats.Length);
                int end   = math.min((chunkIdx + 1) * GaussianSplatAsset.kChunkSize, splats.Length);

                for (int i = begin; i < end; ++i)
                {
                    InputSplatData s = splats[i];
                    s.scale = math.pow((float3)s.scale, 1.0f / 8.0f);      // perceptual pre-transforms
                    s.opacity = GaussianUtils.SquareCentered01(s.opacity);
                    splats[i] = s;

                    float4 col = new float4(s.dc0, s.opacity);
                    minP = math.min(minP, s.pos);   maxP = math.max(maxP, s.pos);
                    minS = math.min(minS, s.scale); maxS = math.max(maxS, s.scale);
                    minC = math.min(minC, col);     maxC = math.max(maxC, col);
                    minH = MinSH(minH, s);          maxH = MaxSH(maxH, s);
                }

                maxP = math.max(maxP, minP + 1e-5f);   // avoid zero-size bounds
                maxS = math.max(maxS, minS + 1e-5f);
                maxC = math.max(maxC, minC + 1e-5f);
                maxH = math.max(maxH, minH + 1e-5f);

                ChunkInfo ci;
                ci.posMin = minP; ci.posMax = maxP; ci.sclMin = minS; ci.sclMax = maxS;
                ci.colMin = minC; ci.colMax = maxC; ci.shMin = minH;  ci.shMax = maxH;
                chunks[chunkIdx] = ci;

                for (int i = begin; i < end; ++i)   // normalize in place to [0,1]
                {
                    InputSplatData s = splats[i];
                    s.pos     = ((float3)s.pos - minP) / (maxP - minP);
                    s.scale   = ((float3)s.scale - minS) / (maxS - minS);
                    s.dc0     = ((float3)s.dc0 - minC.xyz) / (maxC.xyz - minC.xyz);
                    s.opacity = (s.opacity - minC.w) / (maxC.w - minC.w);
                    NormalizeSH(ref s, minH, maxH);
                    splats[i] = s;
                }
            }

            static float3 MinSH(float3 m, in InputSplatData s)
            {
                m = math.min(m,(float3)s.sh1); m = math.min(m,(float3)s.sh2); m = math.min(m,(float3)s.sh3); m = math.min(m,(float3)s.sh4); m = math.min(m,(float3)s.sh5);
                m = math.min(m,(float3)s.sh6); m = math.min(m,(float3)s.sh7); m = math.min(m,(float3)s.sh8); m = math.min(m,(float3)s.sh9); m = math.min(m,(float3)s.shA);
                m = math.min(m,(float3)s.shB); m = math.min(m,(float3)s.shC); m = math.min(m,(float3)s.shD); m = math.min(m,(float3)s.shE); m = math.min(m,(float3)s.shF);
                return m;
            }
            static float3 MaxSH(float3 m, in InputSplatData s)
            {
                m = math.max(m,(float3)s.sh1); m = math.max(m,(float3)s.sh2); m = math.max(m,(float3)s.sh3); m = math.max(m,(float3)s.sh4); m = math.max(m,(float3)s.sh5);
                m = math.max(m,(float3)s.sh6); m = math.max(m,(float3)s.sh7); m = math.max(m,(float3)s.sh8); m = math.max(m,(float3)s.sh9); m = math.max(m,(float3)s.shA);
                m = math.max(m,(float3)s.shB); m = math.max(m,(float3)s.shC); m = math.max(m,(float3)s.shD); m = math.max(m,(float3)s.shE); m = math.max(m,(float3)s.shF);
                return m;
            }
            static void NormalizeSH(ref InputSplatData s, float3 mn, float3 mx)
            {
                float3 d = mx - mn;
                s.sh1=((float3)s.sh1-mn)/d; s.sh2=((float3)s.sh2-mn)/d; s.sh3=((float3)s.sh3-mn)/d; s.sh4=((float3)s.sh4-mn)/d; s.sh5=((float3)s.sh5-mn)/d;
                s.sh6=((float3)s.sh6-mn)/d; s.sh7=((float3)s.sh7-mn)/d; s.sh8=((float3)s.sh8-mn)/d; s.sh9=((float3)s.sh9-mn)/d; s.shA=((float3)s.shA-mn)/d;
                s.shB=((float3)s.shB-mn)/d; s.shC=((float3)s.shC-mn)/d; s.shD=((float3)s.shD-mn)/d; s.shE=((float3)s.shE-mn)/d; s.shF=((float3)s.shF-mn)/d;
            }
        }

        // ---------- bit-pack encoders ----------
        static ulong  EncNorm16(float3 v) => (ulong)(v.x*65535.5f) | ((ulong)(v.y*65535.5f)<<16) | ((ulong)(v.z*65535.5f)<<32);
        static uint   EncNorm11(float3 v) => (uint)(v.x*2047.5f) | ((uint)(v.y*1023.5f)<<11) | ((uint)(v.z*2047.5f)<<21);
        static ushort EncNorm655(float3 v) => (ushort)((uint)(v.x*63.5f) | ((uint)(v.y*31.5f)<<6) | ((uint)(v.z*31.5f)<<11));
        static ushort EncNorm565(float3 v) => (ushort)((uint)(v.x*31.5f) | ((uint)(v.y*63.5f)<<5) | ((uint)(v.z*31.5f)<<11));
        static uint   EncQuatNorm10(float4 v) => (uint)(v.x*1023.5f) | ((uint)(v.y*1023.5f)<<10) | ((uint)(v.z*1023.5f)<<20) | ((uint)(v.w*3.5f)<<30);

        static unsafe void EmitVector(float3 v, byte* p, VectorFormat fmt)
        {
            switch (fmt)
            {
                case VectorFormat.Float32: *(float*)p=v.x; *(float*)(p+4)=v.y; *(float*)(p+8)=v.z; break;
                case VectorFormat.Norm16: { ulong e = EncNorm16(math.saturate(v)); *(uint*)p=(uint)e; *(ushort*)(p+4)=(ushort)(e>>32); } break;
                case VectorFormat.Norm11: *(uint*)p = EncNorm11(math.saturate(v)); break;
                case VectorFormat.Norm6:  *(ushort*)p = EncNorm655(math.saturate(v)); break;
            }
        }

        static unsafe byte[] EncodePositions(NativeArray<InputSplatData> s, VectorFormat fmt)
        {
            int sz = GaussianSplatAsset.GetVectorSize(fmt), n = s.Length;
            byte[] o = new byte[n * sz];
            fixed (byte* d = o) for (int i = 0; i < n; i++) EmitVector(s[i].pos, d + i*sz, fmt);
            return o;
        }
        static unsafe byte[] EncodeOther(NativeArray<InputSplatData> s, RotationFormat fRot, VectorFormat fScale)
        {
            int rotSz = GaussianSplatAsset.GetRotationSize(fRot);
            int stride = GaussianSplatAsset.GetOtherSize(fRot, fScale), n = s.Length;
            byte[] o = new byte[n * stride];
            fixed (byte* d = o)
                for (int i = 0; i < n; i++)
                {
                    byte* p = d + i*stride;
                    Quaternion q = s[i].rot;
                    if (fRot == RotationFormat.Float32) { *(float*)p=q.x; *(float*)(p+4)=q.y; *(float*)(p+8)=q.z; *(float*)(p+12)=q.w; }
                    else *(uint*)p = EncQuatNorm10(GaussianUtils.PackSmallest3Rotation(new float4(q.x, q.y, q.z, q.w)));
                    EmitVector(s[i].scale, p + rotSz, fScale);
                }
            return o;
        }
        static unsafe byte[] EncodeColor(NativeArray<InputSplatData> s, ColorFormat fmt)
        {
            int sz = GaussianSplatAsset.GetColorSize(fmt), n = s.Length;
            byte[] o = new byte[n * sz];
            fixed (byte* d = o)
                for (int i = 0; i < n; i++)
                {
                    byte* p = d + i*sz;
                    float4 c = new float4(s[i].dc0, s[i].opacity);
                    switch (fmt)
                    {
                        case ColorFormat.Float32x4: *(float4*)p = c; break;
                        case ColorFormat.Float16x4: *(half4*)p = new half4(c); break;
                        case ColorFormat.Norm8x4:
                            c = math.saturate(c);
                            *(uint*)p = (uint)(c.x*255.5f) | ((uint)(c.y*255.5f)<<8) | ((uint)(c.z*255.5f)<<16) | ((uint)(c.w*255.5f)<<24);
                            break;
                    }
                }
            return o;
        }
        static unsafe byte[] EncodeSH(NativeArray<InputSplatData> s, SHFormat fmt)
        {
            int sz = GaussianSplatAsset.GetSHSize(fmt), n = s.Length;
            byte[] o = new byte[n * sz];
            float3* c = stackalloc float3[15];   // allocate once, reuse per splat
            fixed (byte* d = o)
                for (int i = 0; i < n; i++)
                {
                    byte* p = d + i*sz;
                    InputSplatData v = s[i];
                    c[0]=v.sh1; c[1]=v.sh2; c[2]=v.sh3; c[3]=v.sh4; c[4]=v.sh5; c[5]=v.sh6; c[6]=v.sh7; c[7]=v.sh8;
                    c[8]=v.sh9; c[9]=v.shA; c[10]=v.shB; c[11]=v.shC; c[12]=v.shD; c[13]=v.shE; c[14]=v.shF;
                    switch (fmt)
                    {
                        case SHFormat.Float32: { float3* q=(float3*)p; for (int k=0;k<15;k++) q[k]=c[k]; q[15]=default; } break;
                        case SHFormat.Float16: { half3*  q=(half3*)p;  for (int k=0;k<15;k++) q[k]=new half3(c[k]); q[15]=default; } break;
                        case SHFormat.Norm11:  { uint*   q=(uint*)p;    for (int k=0;k<15;k++) q[k]=EncNorm11(math.saturate(c[k])); } break;
                        case SHFormat.Norm6:   { ushort* q=(ushort*)p;  for (int k=0;k<15;k++) q[k]=EncNorm565(math.saturate(c[k])); q[15]=0; } break;
                    }
                }
            return o;
        }

        // ---------- write-time verification ----------
        static unsafe bool VerifyFloat32RoundTrip(GaussianSplatAsset a, NativeArray<InputSplatData> splats, out float maxError)
        {
            float* src = (float*)splats.GetUnsafeReadOnlyPtr();
            byte[] pos = a.posData.bytes, oth = a.otherData.bytes, col = a.colorData.bytes, sh = a.shData.bytes;
            int n = splats.Length; maxError = 0f;
            foreach (int i in new[] { 0, n/2, n-1 })
            {
                int b = i * S;
                maxError = Mathf.Max(maxError, Diff(pos, i*3,     src, b+OPos, 3));
                maxError = Mathf.Max(maxError, Diff(oth, i*7,     src, b+ORot, 4));
                maxError = Mathf.Max(maxError, Diff(oth, i*7 + 4, src, b+OScale, 3));
                maxError = Mathf.Max(maxError, Diff(col, i*4,     src, b+ODc, 3));
                maxError = Mathf.Max(maxError, Diff(col, i*4 + 3, src, b+OOpacity, 1));
                maxError = Mathf.Max(maxError, Diff(sh,  i*48,    src, b+OSh, 45));
            }
            return maxError == 0f;
        }

        // Decode a few positions through chunk-lerp and compare to the pre-quantized values.
        static unsafe bool VerifyPositionDecode(GaussianSplatAsset a, float3[] origPos, out float maxError)
        {
            int n = origPos.Length, posSz = GaussianSplatAsset.GetVectorSize(a.posFormat);
            byte[] posBlob = a.posData.bytes, chunkBlob = a.chunkData.bytes;
            maxError = 0f;
            fixed (byte* pp = posBlob) fixed (byte* cp = chunkBlob)
            {
                ChunkInfo* chunks = (ChunkInfo*)cp;
                foreach (int i in new[] { 0, n/2, n-1 })
                {
                    ChunkInfo ci = chunks[i / GaussianSplatAsset.kChunkSize];
                    float3 dec = math.lerp(ci.posMin, ci.posMax, DecodeVector(pp + i*posSz, a.posFormat));
                    maxError = Mathf.Max(maxError, math.length(dec - origPos[i]));
                }
            }
            return maxError <= math.length((float3)a.boundsMax - (float3)a.boundsMin) * 0.01f;
        }

        static unsafe float3 DecodeVector(byte* p, VectorFormat fmt)
        {
            switch (fmt)
            {
                case VectorFormat.Float32: return new float3(*(float*)p, *(float*)(p+4), *(float*)(p+8));
                case VectorFormat.Norm16: { ulong e = *(uint*)p | ((ulong)(*(ushort*)(p+4)) << 32);
                    return new float3((e&65535)/65535f, ((e>>16)&65535)/65535f, ((e>>32)&65535)/65535f); }
                case VectorFormat.Norm11: { uint e = *(uint*)p; return new float3((e&2047)/2047f, ((e>>11)&1023)/1023f, ((e>>21)&2047)/2047f); }
                case VectorFormat.Norm6:  { ushort e = *(ushort*)p; return new float3((e&63)/63f, ((e>>6)&31)/31f, ((e>>11)&31)/31f); }
                default: return 0;
            }
        }
        static unsafe float Diff(byte[] blob, int blobFloatIdx, float* src, int srcFloatIdx, int count)
        {
            float m = 0f;
            for (int k = 0; k < count; k++)
                m = Mathf.Max(m, Mathf.Abs(BitConverter.ToSingle(blob, (blobFloatIdx + k) * 4) - src[srcFloatIdx + k]));
            return m;
        }

        static TextAsset WriteBlob(string relDir, string fileName, byte[] data)
        {
            string rel = $"{relDir}/{fileName}.bytes";
            File.WriteAllBytes(ToAbsolute(rel), data);
            AssetDatabase.ImportAsset(rel, ImportAssetOptions.ForceSynchronousImport);
            return AssetDatabase.LoadAssetAtPath<TextAsset>(rel);
        }
        static string ToAbsolute(string assetsRelPath) => Path.Combine(Path.GetDirectoryName(Application.dataPath)!, assetsRelPath);
    }
}
