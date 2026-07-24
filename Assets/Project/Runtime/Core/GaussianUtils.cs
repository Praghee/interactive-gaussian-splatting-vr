// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// Conversion-time math (activations + quantization packing). Burst-friendly;
// uses only Unity.Mathematics. The runtime decode counterpart lives in HLSL
// (SplatDecode.compute), so only the encode side is needed here.

using Unity.Mathematics;

namespace GaussianSplatVR.Runtime
{
    public static class GaussianUtils
    {
        // ---- activations (applied in float, before quantization) ----

        public static float Sigmoid(float v) => math.rcp(1.0f + math.exp(-v));   // opacity -> alpha

        // SH band-0 DC term -> base RGB. Constant = 1 / (2*sqrt(pi)).
        public static float3 SH0ToColor(float3 dc0) => dc0 * 0.28209479177387814f + 0.5f;

        public static float3 LinearScale(float3 logScale) => math.abs(math.exp(logScale));   // log-scale -> linear

        // Normalise + reorder PLY (w,x,y,z) -> Unity (x,y,z,w).
        public static float4 NormalizeSwizzleRotation(float4 wxyz) => math.normalize(wxyz).yzwx;

        // ---- perceptual remap for opacity quantization ----
        public static float SquareCentered01(float x)
        {
            x -= 0.5f;
            x *= x * math.sign(x);
            return x * 2.0f + 0.5f;
        }

        // ---- smallest-three quaternion packing (for the Norm10 rotation format) ----
        // Returns the three smallest components (remapped to 0..1) in xyz and the
        // index of the largest component (as index/3) in w.
        public static float4 PackSmallest3Rotation(float4 q)
        {
            float4 a = math.abs(q);
            int index = 0; float maxV = a.x;
            if (a.y > maxV) { index = 1; maxV = a.y; }
            if (a.z > maxV) { index = 2; maxV = a.z; }
            if (a.w > maxV) { index = 3; }

            if (index == 0) q = q.yzwx;
            if (index == 1) q = q.xzwy;
            if (index == 2) q = q.xywz;

            float3 three = q.xyz * (q.w >= 0 ? 1 : -1);        // -1/sqrt2 .. +1/sqrt2
            three = three * math.SQRT2 * 0.5f + 0.5f;          // 0 .. 1
            return new float4(three, index / 3.0f);
        }

        // ---- 3D Morton (Z-order) code, for spatial chunk reordering ----
        static ulong Part1By2(ulong x)
        {
            x &= 0x1fffff;
            x = (x ^ (x << 32)) & 0x1f00000000ffffUL;
            x = (x ^ (x << 16)) & 0x1f0000ff0000ffUL;
            x = (x ^ (x << 8))  & 0x100f00f00f00f00fUL;
            x = (x ^ (x << 4))  & 0x10c30c30c30c30c3UL;
            x = (x ^ (x << 2))  & 0x1249249249249249UL;
            return x;
        }
        public static ulong MortonEncode3(uint3 v) => (Part1By2(v.z) << 2) | (Part1By2(v.y) << 1) | Part1By2(v.x);
    }
}
