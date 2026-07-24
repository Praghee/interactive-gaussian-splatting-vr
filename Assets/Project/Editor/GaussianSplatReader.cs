// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// Reads a 3DGS PLY into activated, in-memory splats (InputSplatData). Editor-only.
// Pipeline: read body -> validate core properties -> scatter each field (absent
// fields zero-fill) -> reorder SH channel-major -> coeff-major -> activate.

using System;
using System.Collections.Generic;
using System.IO;
using GaussianSplatVR.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;

namespace GaussianSplatVR.Editor
{
    // Canonical splat, field order matching the INRIA PLY property order (62 floats).
    // Fields absent from a file (e.g. normals) stay zero.
    public struct InputSplatData
    {
        public Vector3 pos, nor, dc0;
        public Vector3 sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
        public float opacity;
        public Vector3 scale;
        public Quaternion rot;
    }

    public static class GaussianSplatReader
    {
        static readonly string[] kRequired =
        { "x","y","z", "f_dc_0","f_dc_1","f_dc_2", "opacity", "scale_0","scale_1","scale_2", "rot_0","rot_1","rot_2","rot_3" };

        // 62 target fields in InputSplatData memory order, keyed by PLY property name.
        static readonly string[] kFields = BuildFields();
        static string[] BuildFields()
        {
            var l = new List<string> { "x","y","z", "nx","ny","nz", "f_dc_0","f_dc_1","f_dc_2" };
            for (int i = 0; i < 45; i++) l.Add($"f_rest_{i}");
            l.AddRange(new[] { "opacity", "scale_0","scale_1","scale_2", "rot_0","rot_1","rot_2","rot_3" });
            return l.ToArray();
        }

        /// <summary>Reads, validates and activates a 3DGS PLY. Caller owns and disposes <paramref name="splats"/>.</summary>
        public static unsafe void ReadPly(string path, out NativeArray<InputSplatData> splats)
        {
            PLYFileReader.ReadFile(path, out int count, out int stride, out var attrs, out var raw);
            try
            {
                var floatNames = new HashSet<string>();
                var fileOffset = new Dictionary<string, int>();
                int off = 0;
                foreach (var (name, type) in attrs)
                {
                    if (type == PLYFileReader.ElementType.Float) floatNames.Add(name);
                    fileOffset[name] = off;
                    off += PLYFileReader.TypeToSize(type);
                }

                var missing = new List<string>();
                foreach (var r in kRequired) if (!floatNames.Contains(r)) missing.Add(r);
                if (missing.Count > 0)
                    throw new IOException("Not a valid 3DGS PLY. Missing float properties: " + string.Join(", ", missing));

                var srcOffsets = new NativeArray<int>(kFields.Length, Allocator.Temp);
                for (int i = 0; i < kFields.Length; i++)
                    srcOffsets[i] = fileOffset.TryGetValue(kFields[i], out var o) ? o : -1;   // -1 => zero-fill
                Assert.AreEqual(UnsafeUtility.SizeOf<InputSplatData>() / 4, kFields.Length);

                splats = new NativeArray<InputSplatData>(count, Allocator.Persistent);
                ScatterFields(count, (byte*)raw.GetUnsafeReadOnlyPtr(), stride,
                    (byte*)splats.GetUnsafePtr(), UnsafeUtility.SizeOf<InputSplatData>(), (int*)srcOffsets.GetUnsafeReadOnlyPtr());
                ReorderSHs(count, (float*)splats.GetUnsafePtr());
                new ActivateJob { data = splats }.Schedule(count, 4096).Complete();

                srcOffsets.Dispose();
            }
            finally { raw.Dispose(); }
        }

        // Copy present fields (4 bytes each, float) from file layout into InputSplatData.
        static unsafe void ScatterFields(int count, byte* src, int srcStride, byte* dst, int dstStride, int* srcOffsets)
        {
            int fields = dstStride / 4;
            for (int i = 0; i < count; i++)
            {
                for (int f = 0; f < fields; f++)
                    if (srcOffsets[f] >= 0) *(int*)(dst + f * 4) = *(int*)(src + srcOffsets[f]);
                src += srcStride; dst += dstStride;
            }
        }

        // SH rest arrives channel-major [R0..14, G0..14, B0..14]; repack to coeff-major.
        static unsafe void ReorderSHs(int count, float* data)
        {
            int stride = UnsafeUtility.SizeOf<InputSplatData>() / 4;
            const int shStart = 9, shCount = 15;   // f_rest begins at float 9 (pos3+nor3+dc3)
            float* tmp = stackalloc float[shCount * 3];
            int idx = shStart;
            for (int i = 0; i < count; i++)
            {
                for (int j = 0; j < shCount; j++)
                {
                    tmp[j * 3 + 0] = data[idx + j];
                    tmp[j * 3 + 1] = data[idx + j + shCount];
                    tmp[j * 3 + 2] = data[idx + j + shCount * 2];
                }
                for (int j = 0; j < shCount * 3; j++) data[idx + j] = tmp[j];
                idx += stride;
            }
        }

        [BurstCompile]
        struct ActivateJob : IJobParallelFor
        {
            public NativeArray<InputSplatData> data;
            public void Execute(int i)
            {
                InputSplatData s = data[i];
                float4 q = GaussianUtils.NormalizeSwizzleRotation(new float4(s.rot.x, s.rot.y, s.rot.z, s.rot.w));
                s.rot = new Quaternion(q.x, q.y, q.z, q.w);
                s.scale = GaussianUtils.LinearScale(s.scale);
                s.dc0 = GaussianUtils.SH0ToColor(s.dc0);
                s.opacity = GaussianUtils.Sigmoid(s.opacity);
                data[i] = s;
            }
        }
    }
}
