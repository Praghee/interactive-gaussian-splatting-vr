// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// SplatContainer -- makes a MOVING object carry what's inside it (the drawer).
//
// A cupboard needs none of this: its shelf is part of the static kitchen cloud, so
// anything you set down just stays. A drawer is different -- it's a container that
// TRAVELS, so its contents have to travel with it. Without this, sliding the drawer
// would leave the apple hanging in mid-air.
//
// HOW IT DECIDES
//   * A free-grab object that is RELEASED while sitting FULLY inside the box gets
//     picked up by the container: we record its pose relative to us and re-apply
//     that every frame, so it rides along.
//   * Grab it again and the carry cancels -- the hand always wins.
//   * Half-in objects are ignored. Containment is all-or-nothing (all 8 corners of
//     the object's grab volume must be inside), which keeps it predictable.
//
// WHY OFFSET-FOLLOW, NOT RE-PARENTING
// Re-parenting contents under the drawer would run them through the drawer's
// mirrored (-1 X) scale and corrupt their transforms. Recording a relative pose
// and re-applying it sidesteps that, and also handles a container that ROTATES,
// not just one that slides.
//
// Runs in LateUpdate, after the joint has moved, so contents track with no lag.
// No colliders, no physics -- consistent with the rest of the interaction.
//
// SETUP
//   1. Put this on the drawer (alongside SplatSlideJoint).
//   2. Right-click the component header > "Fill Box From Splat Bounds" for a
//      starting box, then shrink it to just the CAVITY -- the splat bounds include
//      the drawer front, which you don't want counted as "inside".

using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatVR.Runtime
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Gaussian Splat/Splat Container")]
    public class SplatContainer : MonoBehaviour
    {
        [Header("Inside volume")]
        [Tooltip("Centre of the carry box, in this object's local space.")]
        [SerializeField] Vector3 m_Center = Vector3.zero;
        [Tooltip("Size of the carry box, in this object's local space. Shrink it to just the " +
                 "cavity -- the splat bounds include the drawer front.")]
        [SerializeField] Vector3 m_Size = Vector3.one * 0.3f;

        [Header("Gizmo")]
        [SerializeField] bool m_DrawGizmo = true;
        [SerializeField] Color m_GizmoColor = new Color(0.4f, 1f, 0.5f, 1f);

        // What we're carrying, with each object's pose expressed in OUR local space.
        struct Carried
        {
            public SplatGrabInteractable obj;
            public Vector3 localPos;
            public Quaternion localRot;
        }

        readonly List<Carried> m_Carried = new();

        public Vector3 Center { get => m_Center; set => m_Center = value; }
        public Vector3 Size { get => m_Size; set => m_Size = value; }

        /// <summary>How many objects are riding along right now.</summary>
        public int CarriedCount => m_Carried.Count;

        void OnDisable() => m_Carried.Clear();

        void LateUpdate()
        {
            // 1) Move what we're already carrying, and drop anything that got grabbed.
            for (int i = m_Carried.Count - 1; i >= 0; i--)
            {
                var c = m_Carried[i];
                if (c.obj == null || !c.obj.isActiveAndEnabled || c.obj.IsGrabbed)
                {
                    m_Carried.RemoveAt(i);   // the hand wins
                    continue;
                }
                c.obj.transform.SetPositionAndRotation(
                    transform.TransformPoint(c.localPos),
                    transform.rotation * c.localRot);
            }

            // 2) Pick up anything free-grabbable that's been left fully inside us.
            foreach (var it in SplatGrabInteractable.All)
            {
                if (it == null || it.IsGrabbed) continue;
                if (it is not SplatFreeGrab) continue;          // joints are never carried
                if (it.transform == transform) continue;        // never ourselves
                if (IsCarrying(it)) continue;
                if (it.Volume == null || !FullyInside(it.Volume)) continue;

                m_Carried.Add(new Carried
                {
                    obj = it,
                    localPos = transform.InverseTransformPoint(it.transform.position),
                    localRot = Quaternion.Inverse(transform.rotation) * it.transform.rotation
                });
            }
        }

        bool IsCarrying(SplatGrabInteractable it)
        {
            for (int i = 0; i < m_Carried.Count; i++)
                if (m_Carried[i].obj == it) return true;
            return false;
        }

        // All 8 corners of the object's grab volume must be inside our box.
        bool FullyInside(SplatGrabVolume v)
        {
            Vector3 c = v.Center, h = v.Size * 0.5f;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = c + new Vector3(
                    (i & 1) == 0 ? -h.x : h.x,
                    (i & 2) == 0 ? -h.y : h.y,
                    (i & 4) == 0 ? -h.z : h.z);
                if (!ContainsWorld(v.transform.TransformPoint(corner))) return false;
            }
            return true;
        }

        /// <summary>Is this world point inside the carry box?</summary>
        public bool ContainsWorld(Vector3 worldPoint)
        {
            Vector3 local = transform.InverseTransformPoint(worldPoint) - m_Center;
            Vector3 half = m_Size * 0.5f;
            return Mathf.Abs(local.x) <= half.x
                && Mathf.Abs(local.y) <= half.y
                && Mathf.Abs(local.z) <= half.z;
        }

        // Starting point for the box: the splat's own bounds. Almost always too big
        // (it includes the drawer front), so shrink it to the cavity afterwards.
        [ContextMenu("Fill Box From Splat Bounds")]
        void FillBoxFromSplatBounds()
        {
            var r = GetComponent<GaussianSplatRenderer>();
            if (r == null || r.Asset == null)
            {
                Debug.LogWarning("[SplatContainer] no GaussianSplatRenderer/asset on this object.", this);
                return;
            }
            Vector3 mn = r.Asset.boundsMin, mx = r.Asset.boundsMax;
            m_Center = (mn + mx) * 0.5f;
            m_Size = mx - mn;
        }

#if UNITY_EDITOR
        void OnDrawGizmos()
        {
            if (!m_DrawGizmo) return;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = m_GizmoColor;
            Gizmos.DrawWireCube(m_Center, m_Size);
            var faint = m_GizmoColor; faint.a = 0.05f;
            Gizmos.color = faint;
            Gizmos.DrawCube(m_Center, m_Size);
        }
#endif
    }
}
