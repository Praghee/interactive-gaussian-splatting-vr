// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// GPUSorting DeviceRadixSort driver (Thomas Smith, b0nes164/GPUSorting, MIT;
// adapted from gsplat-unity's GsplatSortPass). Sorts (uint key, uint payload)
// pairs ascending, in place, over 4 eight-bit radix passes (Upsweep/Scan/
// Downsweep + one histogram clear = 13 dispatches; the sorted result lands
// back in the input buffers, 4 swaps = even). Replaces the FFX ParallelSort
// (40 dispatches) for a large win on tiled mobile GPUs (Quest/Adreno).

using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatVR.Runtime
{
    public class GaussianSplatRadixSort
    {
        const uint kPartitionSize = 3840;   // keys per threadblock partition
        const uint kRadix = 256;            // 8-bit digits
        const uint kRadixPasses = 4;        // 32-bit keys / 8 bits

        static readonly int k_eNumKeys      = Shader.PropertyToID("e_numKeys");
        static readonly int k_eThreadBlocks = Shader.PropertyToID("e_threadBlocks");
        static readonly int k_eRadixShift   = Shader.PropertyToID("e_radixShift");
        static readonly int k_bSort         = Shader.PropertyToID("b_sort");
        static readonly int k_bSortPayload  = Shader.PropertyToID("b_sortPayload");
        static readonly int k_bAlt          = Shader.PropertyToID("b_alt");
        static readonly int k_bAltPayload   = Shader.PropertyToID("b_altPayload");
        static readonly int k_bPassHist     = Shader.PropertyToID("b_passHist");
        static readonly int k_bGlobalHist   = Shader.PropertyToID("b_globalHist");

        // Working buffers; depend only on element count.
        public struct SupportResources
        {
            public int count;
            public GraphicsBuffer altKeys, altPayload;      // ping-pong
            public GraphicsBuffer passHist, globalHist;     // histograms

            public static SupportResources Load(int count)
            {
                uint blocks = DivRoundUp((uint)count, kPartitionSize);
                var t = GraphicsBuffer.Target.Structured;
                return new SupportResources
                {
                    count = count,
                    altKeys    = new GraphicsBuffer(t, count, 4) { name = "RadixAltKeys" },
                    altPayload = new GraphicsBuffer(t, count, 4) { name = "RadixAltPayload" },
                    passHist   = new GraphicsBuffer(t, (int)(blocks * kRadix), 4) { name = "RadixPassHist" },
                    globalHist = new GraphicsBuffer(t, (int)(kRadix * kRadixPasses), 4) { name = "RadixGlobalHist" },
                };
            }

            public void Dispose()
            {
                altKeys?.Dispose(); altPayload?.Dispose(); passHist?.Dispose(); globalHist?.Dispose();
                altKeys = altPayload = passHist = globalHist = null; count = 0;
            }
        }

        readonly ComputeShader m_CS;
        readonly int m_KInit, m_KUpsweep, m_KScan, m_KDownsweep;
        readonly bool m_Valid;

        public ComputeShader Compute => m_CS;
        public bool Valid => m_Valid;

        public GaussianSplatRadixSort(ComputeShader cs)
        {
            m_CS = cs;
            m_KInit      = cs ? cs.FindKernel("InitDeviceRadixSort") : -1;
            m_KUpsweep   = cs ? cs.FindKernel("Upsweep")   : -1;
            m_KScan      = cs ? cs.FindKernel("Scan")      : -1;
            m_KDownsweep = cs ? cs.FindKernel("Downsweep") : -1;
            m_Valid = cs && m_KInit >= 0 && m_KUpsweep >= 0 && m_KScan >= 0 && m_KDownsweep >= 0
                      && cs.IsSupported(m_KInit) && cs.IsSupported(m_KUpsweep)
                      && cs.IsSupported(m_KScan) && cs.IsSupported(m_KDownsweep);

            // SPIRV<1.6 WaveGetLaneCount bug workaround: fixed wave size path on Vulkan.
            if (cs)
            {
                var vulkan = new LocalKeyword(cs, "VULKAN");
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan) cs.EnableKeyword(vulkan);
                else cs.DisableKeyword(vulkan);
            }
        }

        static uint DivRoundUp(uint x, uint y) => (x + y - 1) / y;

        /// <summary>Sorts (keys, payloads) ascending, in place, over count elements.</summary>
        public void Dispatch(CommandBuffer cmd, GraphicsBuffer keys, GraphicsBuffer payloads, uint count, SupportResources res)
        {
            if (!m_Valid || count == 0) return;

            GraphicsBuffer srcKey = keys, srcPay = payloads;
            GraphicsBuffer dstKey = res.altKeys, dstPay = res.altPayload;
            uint blocks = DivRoundUp(count, kPartitionSize);

            cmd.SetComputeIntParam(m_CS, k_eNumKeys, (int)count);
            cmd.SetComputeIntParam(m_CS, k_eThreadBlocks, (int)blocks);

            // statically located buffers
            cmd.SetComputeBufferParam(m_CS, m_KUpsweep, k_bPassHist, res.passHist);
            cmd.SetComputeBufferParam(m_CS, m_KUpsweep, k_bGlobalHist, res.globalHist);
            cmd.SetComputeBufferParam(m_CS, m_KScan, k_bPassHist, res.passHist);
            cmd.SetComputeBufferParam(m_CS, m_KDownsweep, k_bPassHist, res.passHist);
            cmd.SetComputeBufferParam(m_CS, m_KDownsweep, k_bGlobalHist, res.globalHist);

            // clear the global histogram once
            cmd.SetComputeBufferParam(m_CS, m_KInit, k_bGlobalHist, res.globalHist);
            cmd.DispatchCompute(m_CS, m_KInit, 1, 1, 1);

            for (uint shift = 0; shift < 32; shift += 8)
            {
                cmd.SetComputeIntParam(m_CS, k_eRadixShift, (int)shift);

                cmd.SetComputeBufferParam(m_CS, m_KUpsweep, k_bSort, srcKey);
                cmd.DispatchCompute(m_CS, m_KUpsweep, (int)blocks, 1, 1);

                cmd.DispatchCompute(m_CS, m_KScan, (int)kRadix, 1, 1);

                cmd.SetComputeBufferParam(m_CS, m_KDownsweep, k_bSort, srcKey);
                cmd.SetComputeBufferParam(m_CS, m_KDownsweep, k_bSortPayload, srcPay);
                cmd.SetComputeBufferParam(m_CS, m_KDownsweep, k_bAlt, dstKey);
                cmd.SetComputeBufferParam(m_CS, m_KDownsweep, k_bAltPayload, dstPay);
                cmd.DispatchCompute(m_CS, m_KDownsweep, (int)blocks, 1, 1);

                (srcKey, dstKey) = (dstKey, srcKey);
                (srcPay, dstPay) = (dstPay, srcPay);
            }
        }
    }
}
