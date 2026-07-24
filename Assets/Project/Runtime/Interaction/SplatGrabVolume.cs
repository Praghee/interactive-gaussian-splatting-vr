// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// SplatGrabVolume -- an invisible box that says "a hand can grab here".
//
// Splat clouds are made of light, not geometry, so there is no surface for a
// hand to hit-test against. This is the stand-in: a box, authored by eye in the
// Inspector, that sits where the object LOOKS like it is. Nothing renders it in
// the headset -- it exists only so the grab code has something to find.
//
// Author it with the wire-box gizmo: drag Center / Size until the box hugs the
// visible part of the cloud (or just the handle you actually want to grab). A
// Gaussian's transform origin usually sits off the visible surface, which is
// exactly why Center is separate from the object's pivot.
//
// The test is done in world space, so it stays correct however the object is
// moved, rotated or (mirror-)scaled -- which every cloud in this project is.

using UnityEngine;

namespace GaussianSplatVR.Runtime
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Gaussian Splat/Splat Grab Volume")]
    public class SplatGrabVolume : MonoBehaviour
    {
        [Tooltip("Box centre, in this object's local space.")]
        [SerializeField] Vector3 m_Center = Vector3.zero;
        [Tooltip("Box size, in this object's local space.")]
        [SerializeField] Vector3 m_Size = Vector3.one * 0.2f;

        [Header("Gizmo")]
        [SerializeField] bool m_DrawGizmo = true;
        [SerializeField] Color m_GizmoColor = new Color(0.2f, 0.7f, 1f, 1f);

        public Vector3 Center { get => m_Center; set => m_Center = value; }
        public Vector3 Size { get => m_Size; set => m_Size = value; }

        /// <summary>World-space centre of the box (what "reach distance" is measured to).</summary>
        public Vector3 WorldCenter => transform.TransformPoint(m_Center);

        /// <summary>Is this world point inside the box?</summary>
        public bool Contains(Vector3 worldPoint)
        {
            Vector3 local = transform.InverseTransformPoint(worldPoint) - m_Center;
            Vector3 half = m_Size * 0.5f;
            return Mathf.Abs(local.x) <= half.x
                && Mathf.Abs(local.y) <= half.y
                && Mathf.Abs(local.z) <= half.z;
        }

        /// <summary>
        /// Squared distance from a world point to the box surface (0 when inside).
        /// Used to pick the nearest grab volume when a hand is close but not inside.
        /// </summary>
        public float SqrDistance(Vector3 worldPoint)
        {
            Vector3 local = transform.InverseTransformPoint(worldPoint) - m_Center;
            Vector3 half = m_Size * 0.5f;
            Vector3 d = new Vector3(
                Mathf.Max(0f, Mathf.Abs(local.x) - half.x),
                Mathf.Max(0f, Mathf.Abs(local.y) - half.y),
                Mathf.Max(0f, Mathf.Abs(local.z) - half.z));
            // Scale back to world so the metric is comparable across objects.
            Vector3 world = transform.TransformVector(d);
            return world.sqrMagnitude;
        }

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            if (!m_DrawGizmo) return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = m_GizmoColor;
            Gizmos.DrawWireCube(m_Center, m_Size);
            var faint = m_GizmoColor; faint.a = 0.06f;
            Gizmos.color = faint;
            Gizmos.DrawCube(m_Center, m_Size);
        }
#endif
    }
}
