// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// AMD FidelityFX ParallelSort driver. Runtime-safe. Sorts (uint key, uint
// payload) pairs ascending, in place, over 8 four-bit radix passes; the sorted
// result lands back in the input buffers (8 swaps = even). Quest-safe: the
// kernels use only basic wave ops (Vulkan subgroup / Adreno).
//
// Ported from ninjamode/Unity-VR-Gaussian-Splatting (FidelityFxSort),
// based on AMD FidelityFX-ParallelSort v1.1.1.

using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatVR.Runtime
{
    public class GaussianSplatFfxSort
    {
        const uint ELEMENTS_PER_THREAD = 4;
        const uint THREADGROUP_SIZE = 128;
        const int  BITS_PER_PASS = 4;
        const uint BIN_COUNT = 1u << BITS_PER_PASS;         // 16
        const uint BLOCK_SIZE = ELEMENTS_PER_THREAD * THREADGROUP_SIZE;   // 512
        const uint MAX_THREADGROUPS = 800;

        // Working buffers; depend only on element count (no compute needed), so a
        // renderer can own one set and Load() it when its splat count changes.
        public struct SupportResources
        {
            public int count;
            public GraphicsBuffer sortScratch, payloadScratch;   // ping-pong
            public GraphicsBuffer scratch, reducedScratch;       // per-block / reduced sums

            public static SupportResources Load(int count)
            {
                uint numBlocks = DivRoundUp((uint)count, BLOCK_SIZE);
                uint numReduced = DivRoundUp(numBlocks, BLOCK_SIZE);
                var t = GraphicsBuffer.Target.Structured;
                return new SupportResources
                {
                    count = count,
                    sortScratch    = new GraphicsBuffer(t, count, 4) { name = "FfxSortScratch" },
                    payloadScratch = new GraphicsBuffer(t, count, 4) { name = "FfxPayloadScratch" },
                    scratch        = new GraphicsBuffer(t, (int)(BIN_COUNT * numBlocks), 4)  { name = "FfxScratch" },
                    reducedScratch = new GraphicsBuffer(t, (int)(BIN_COUNT * numReduced), 4) { name = "FfxReducedScratch" },
                };
            }

            public void Dispose()
            {
                sortScratch?.Dispose(); payloadScratch?.Dispose(); scratch?.Dispose(); reducedScratch?.Dispose();
                sortScratch = payloadScratch = scratch = reducedScratch = null; count = 0;
            }
        }

        readonly ComputeShader m_CS;
        readonly int m_KCount, m_KReduce, m_KScan, m_KScanAdd, m_KScatter;
        readonly bool m_Valid;

        public ComputeShader Compute => m_CS;
        public bool Valid => m_Valid;

        public GaussianSplatFfxSort(ComputeShader cs)
        {
            m_CS = cs;
            m_KCount   = cs ? cs.FindKernel("FfxParallelSortCount")   : -1;
            m_KReduce  = cs ? cs.FindKernel("FfxParallelSortReduce")  : -1;
            m_KScan    = cs ? cs.FindKernel("FfxParallelSortScan")    : -1;
            m_KScanAdd = cs ? cs.FindKernel("FfxParallelSortScanAdd") : -1;
            m_KScatter = cs ? cs.FindKernel("FfxParallelSortScatter") : -1;
            m_Valid = cs && m_KCount >= 0 && cs.IsSupported(m_KCount) && cs.IsSupported(m_KReduce)
                      && cs.IsSupported(m_KScan) && cs.IsSupported(m_KScanAdd) && cs.IsSupported(m_KScatter);
        }

        static uint DivRoundUp(uint x, uint y) => (x + y - 1) / y;

        public void Dispatch(CommandBuffer cmd, GraphicsBuffer keys, GraphicsBuffer payloads, uint count, SupportResources res)
        {
            if (!m_Valid) return;

            GraphicsBuffer srcKey = keys, srcPay = payloads, dstKey = res.sortScratch, dstPay = res.payloadScratch;
            uint numBlocks = DivRoundUp(count, BLOCK_SIZE);

            uint numTG = MAX_THREADGROUPS, blocksPerTG = numBlocks / MAX_THREADGROUPS, tgExtra = numBlocks % MAX_THREADGROUPS;
            if (numBlocks < MAX_THREADGROUPS) { blocksPerTG = 1; numTG = numBlocks; tgExtra = 0; }
            uint numReducedTG = BIN_COUNT * (BLOCK_SIZE > numTG ? 1 : (numTG + BLOCK_SIZE - 1) / BLOCK_SIZE);

            cmd.SetComputeIntParam(m_CS, "numKeys", (int)count);
            cmd.SetComputeIntParam(m_CS, "numBlocksPerThreadGroup", (int)blocksPerTG);
            cmd.SetComputeIntParam(m_CS, "numThreadGroups", (int)numTG);
            cmd.SetComputeIntParam(m_CS, "numThreadGroupsWithAdditionalBlocks", (int)tgExtra);
            cmd.SetComputeIntParam(m_CS, "numReduceThreadgroupPerBin", (int)(numReducedTG / BIN_COUNT));
            cmd.SetComputeIntParam(m_CS, "numScanValues", (int)numReducedTG);

            for (uint shift = 0; shift < 32; shift += BITS_PER_PASS)
            {
                cmd.SetComputeIntParam(m_CS, "shift", (int)shift);

                cmd.SetComputeBufferParam(m_CS, m_KCount, "rw_source_keys", srcKey);
                cmd.SetComputeBufferParam(m_CS, m_KCount, "rw_sum_table", res.scratch);
                cmd.DispatchCompute(m_CS, m_KCount, (int)numTG, 1, 1);

                cmd.SetComputeBufferParam(m_CS, m_KReduce, "rw_sum_table", res.scratch);
                cmd.SetComputeBufferParam(m_CS, m_KReduce, "rw_reduce_table", res.reducedScratch);
                cmd.DispatchCompute(m_CS, m_KReduce, (int)numReducedTG, 1, 1);

                cmd.SetComputeBufferParam(m_CS, m_KScan, "rw_scan_source", res.reducedScratch);
                cmd.SetComputeBufferParam(m_CS, m_KScan, "rw_scan_dest", res.reducedScratch);
                cmd.DispatchCompute(m_CS, m_KScan, 1, 1, 1);

                cmd.SetComputeBufferParam(m_CS, m_KScanAdd, "rw_scan_source", res.scratch);
                cmd.SetComputeBufferParam(m_CS, m_KScanAdd, "rw_scan_dest", res.scratch);
                cmd.SetComputeBufferParam(m_CS, m_KScanAdd, "rw_scan_scratch", res.reducedScratch);
                cmd.DispatchCompute(m_CS, m_KScanAdd, (int)numReducedTG, 1, 1);

                cmd.SetComputeBufferParam(m_CS, m_KScatter, "rw_source_keys", srcKey);
                cmd.SetComputeBufferParam(m_CS, m_KScatter, "rw_dest_keys", dstKey);
                cmd.SetComputeBufferParam(m_CS, m_KScatter, "rw_sum_table", res.scratch);
                cmd.SetComputeBufferParam(m_CS, m_KScatter, "rw_source_payloads", srcPay);
                cmd.SetComputeBufferParam(m_CS, m_KScatter, "rw_dest_payloads", dstPay);
                cmd.DispatchCompute(m_CS, m_KScatter, (int)numTG, 1, 1);

                (srcKey, dstKey) = (dstKey, srcKey);
                (srcPay, dstPay) = (dstPay, srcPay);
            }
        }
    }
}
