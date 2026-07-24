// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// In-editor converter UI (also owns its menu item). Five quality presets
// (Very Low .. Very High) plus a Custom preset that unlocks the per-attribute
// overrides. Named presets show the overrides greyed out. Saved asset name:
//   <plyName>_<preset>_<sizeMB>MB
//   Menu: Tools > Gaussian Splat > Convert PLY to Asset

using System;
using System.IO;
using GaussianSplatVR.Runtime;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using VectorFormat = GaussianSplatVR.Runtime.GaussianSplatAsset.VectorFormat;
using RotationFormat = GaussianSplatVR.Runtime.GaussianSplatAsset.RotationFormat;
using ColorFormat = GaussianSplatVR.Runtime.GaussianSplatAsset.ColorFormat;
using SHFormat = GaussianSplatVR.Runtime.GaussianSplatAsset.SHFormat;

namespace GaussianSplatVR.Editor
{
    public class GaussianSplatConverterWindow : EditorWindow
    {
        enum PresetChoice { VeryLow, Low, Balanced, High, VeryHigh, Custom }   // shown nicified in the popup

        const string kOutputDefault = "Assets/Project/Samples";

        string m_PlyPath = "";
        int m_SplatCount = -1, m_ShRestCount = -1;
        string m_HeaderError = "";
        float m_EstimateMB;

        PresetChoice m_Preset = PresetChoice.Balanced;
        VectorFormat m_Pos, m_Scale;
        RotationFormat m_Rot;
        ColorFormat m_Color;
        SHFormat m_SH;
        string m_OutputFolder = kOutputDefault;

        [MenuItem("Tools/Gaussian Splat/Convert PLY to Asset")]
        public static void Open()
        {
            var w = GetWindow<GaussianSplatConverterWindow>(true, "Gaussian Splat Converter", true);
            w.minSize = new Vector2(360, 300);
            w.maxSize = new Vector2(2000, 2000);
            const float ww = 430f, wh = 372f;
            Rect main = EditorGUIUtility.GetMainWindowPosition();
            w.position = new Rect(main.x + (main.width - ww) * 0.5f, main.y + (main.height - wh) * 0.5f, ww, wh);
            w.ApplyPreset(w.m_Preset);
            w.Show();
        }

        // Window-local preset -> formats. Custom keeps whatever is currently set.
        void ApplyPreset(PresetChoice p)
        {
            switch (p)
            {
                case PresetChoice.VeryLow:  m_Pos=VectorFormat.Norm11;  m_Scale=VectorFormat.Norm6;   m_Rot=RotationFormat.Norm10;  m_Color=ColorFormat.Norm8x4;   m_SH=SHFormat.Norm6;   break;
                case PresetChoice.Low:      m_Pos=VectorFormat.Norm11;  m_Scale=VectorFormat.Norm11;  m_Rot=RotationFormat.Norm10;  m_Color=ColorFormat.Norm8x4;   m_SH=SHFormat.Norm11;  break;
                case PresetChoice.Balanced: m_Pos=VectorFormat.Norm16;  m_Scale=VectorFormat.Norm16;  m_Rot=RotationFormat.Norm10;  m_Color=ColorFormat.Float16x4; m_SH=SHFormat.Norm11;  break;
                case PresetChoice.High:     m_Pos=VectorFormat.Norm16;  m_Scale=VectorFormat.Norm16;  m_Rot=RotationFormat.Norm10;  m_Color=ColorFormat.Float16x4; m_SH=SHFormat.Float16; break;
                case PresetChoice.VeryHigh: m_Pos=VectorFormat.Float32; m_Scale=VectorFormat.Float32; m_Rot=RotationFormat.Float32; m_Color=ColorFormat.Float32x4; m_SH=SHFormat.Float32; break;
                case PresetChoice.Custom:   break;
            }
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("Input", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                m_PlyPath = EditorGUILayout.TextField("PLY file", m_PlyPath);
                if (EditorGUI.EndChangeCheck() && File.Exists(m_PlyPath)) ReadHeader();
                if (GUILayout.Button("Browse", GUILayout.Width(70))) Browse();
            }
            if (!string.IsNullOrEmpty(m_HeaderError))
                EditorGUILayout.HelpBox(m_HeaderError, MessageType.Error);
            else if (m_SplatCount > 0)
                EditorGUILayout.HelpBox($"{m_SplatCount:N0} splats, {(m_ShRestCount == 45 ? "bands 0-3" : m_ShRestCount == 0 ? "band 0 only" : $"{m_ShRestCount} SH rest coeffs")}", MessageType.Info);
            else
                EditorGUILayout.HelpBox("Select a 3DGS .ply file.", MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Quality", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            m_Preset = (PresetChoice)EditorGUILayout.EnumPopup("Preset", m_Preset);
            if (EditorGUI.EndChangeCheck() && m_Preset != PresetChoice.Custom) ApplyPreset(m_Preset);

            using (new EditorGUI.DisabledScope(m_Preset != PresetChoice.Custom))   // greyed unless Custom
            {
                EditorGUI.indentLevel++;
                m_Pos   = (VectorFormat)  EditorGUILayout.EnumPopup("Position", m_Pos);
                m_Scale = (VectorFormat)  EditorGUILayout.EnumPopup("Scale", m_Scale);
                m_Rot   = (RotationFormat)EditorGUILayout.EnumPopup("Rotation", m_Rot);
                m_Color = (ColorFormat)   EditorGUILayout.EnumPopup("Color + alpha", m_Color);
                m_SH    = (SHFormat)      EditorGUILayout.EnumPopup("Spherical Harmonics", m_SH);
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.Space();
            DrawEstimate();

            EditorGUILayout.Space();
            m_OutputFolder = EditorGUILayout.TextField("Output folder", m_OutputFolder);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(m_SplatCount <= 0 || string.IsNullOrEmpty(m_OutputFolder)))
                if (GUILayout.Button("Convert to Asset", GUILayout.Height(30))) Convert();
        }

        void DrawEstimate()
        {
            if (m_SplatCount <= 0) { EditorGUILayout.LabelField("Estimated size", "-"); m_EstimateMB = 0f; return; }
            int n = m_SplatCount;
            bool chunks = m_Pos != VectorFormat.Float32 || m_Scale != VectorFormat.Float32 || m_Color != ColorFormat.Float32x4 || m_SH != SHFormat.Float32;
            int perSplat = GaussianSplatAsset.GetVectorSize(m_Pos) + GaussianSplatAsset.GetOtherSize(m_Rot, m_Scale)
                         + GaussianSplatAsset.GetColorSize(m_Color) + GaussianSplatAsset.GetSHSize(m_SH);
            float chunkPer = chunks ? (float)GaussianSplatAsset.CalcChunkDataSize(n) / n : 0f;
            m_EstimateMB = (perSplat + chunkPer) * n / (1024f * 1024f);
            EditorGUILayout.LabelField("Estimated size", $"{m_EstimateMB:F1} MB   ({perSplat} B/splat{(chunks ? $" + {chunkPer:F2} chunk" : "")})");
        }

        void Browse()
        {
            string p = EditorUtility.OpenFilePanel("Select 3DGS .ply file", "", "ply");
            if (string.IsNullOrEmpty(p)) return;
            m_PlyPath = p; GUI.FocusControl(null); ReadHeader();
        }

        void ReadHeader()
        {
            m_SplatCount = -1; m_ShRestCount = -1; m_HeaderError = "";
            try
            {
                PLYFileReader.ReadFileHeader(m_PlyPath, out int count, out _, out var attrs);
                int sh = 0; foreach (var a in attrs) if (a.name.StartsWith("f_rest_")) sh++;
                m_SplatCount = count; m_ShRestCount = sh;
            }
            catch (Exception e) { m_HeaderError = e.Message; }
        }

        void Convert()
        {
            NativeArray<InputSplatData> splats = default;
            try
            {
                EditorUtility.DisplayProgressBar("Gaussian Splat", "Reading & activating PLY...", 0.25f);
                GaussianSplatReader.ReadPly(m_PlyPath, out splats);
                ComputeBounds(splats, out Vector3 bMin, out Vector3 bMax);

                EditorUtility.DisplayProgressBar("Gaussian Splat", "Writing asset...", 0.7f);
                string name = $"{SanitizeName(Path.GetFileNameWithoutExtension(m_PlyPath))}_{m_Preset}_{m_EstimateMB:F1}MB";
                var res = GaussianSplatAssetWriter.WriteAsset(splats, bMin, bMax, m_Pos, m_Scale, m_Rot, m_Color, m_SH, m_OutputFolder, name);

                LogResult(res);
                Selection.activeObject = res.asset;
                EditorGUIUtility.PingObject(res.asset);
            }
            catch (Exception e) { Debug.LogError($"[GaussianSplatVR] Convert failed:\n{e}"); }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (splats.IsCreated) splats.Dispose();
            }
        }

        static void ComputeBounds(NativeArray<InputSplatData> splats, out Vector3 mn, out Vector3 mx)
        {
            mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            mx = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = 0; i < splats.Length; i++) { Vector3 p = splats[i].pos; mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        }

        static void LogResult(GaussianSplatAssetWriter.WriteResult res)
        {
            var a = res.asset; int n = a.splatCount;
            float totalMB = (GaussianSplatAsset.CalcPosDataSize(n, a.posFormat) + GaussianSplatAsset.CalcOtherDataSize(n, a.rotFormat, a.scaleFormat)
                + GaussianSplatAsset.CalcColorDataSize(n, a.colorFormat) + GaussianSplatAsset.CalcSHDataSize(n, a.shFormat)
                + (res.chunkCount > 0 ? GaussianSplatAsset.CalcChunkDataSize(n) : 0)) / (1024f * 1024f);
            string verify = res.exact ? (res.verifyOk ? "PASS (bit-exact)" : $"FAIL ({res.maxError:E3})")
                                      : (res.verifyOk ? $"PASS (position decode, maxErr {res.maxError:E3})" : $"FAIL ({res.maxError:E3})");
            Debug.Log($"[GaussianSplatVR] wrote {AssetDatabase.GetAssetPath(a)}\n" +
                      $"  Splats/chunks: {n:N0} / {res.chunkCount:N0}   Total: {totalMB:F1} MB ({(int)Mathf.Round(totalMB*1024f*1024f/n)} B/splat)\n" +
                      $"  Formats: pos {a.posFormat}, scale {a.scaleFormat}, rot {a.rotFormat}, col {a.colorFormat}, sh {a.shFormat}   Round-trip: {verify}");
        }

        static string SanitizeName(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return string.IsNullOrEmpty(s) ? "SplatAsset" : s;
        }
    }
}
