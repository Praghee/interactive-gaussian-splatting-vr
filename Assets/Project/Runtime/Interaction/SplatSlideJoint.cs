// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// SplatSlideJoint -- a drawer. Grab it and it slides along ONE axis, following
// your hand, clamped between closed and fully-open. Let go and it stays.
//
// All of the drawer's behaviour lives HERE. The shared grab base and driver are
// untouched: the base just calls OnGrabBegin/OnGrabUpdate/OnGrabRelease while the
// grip is held, and this file decides what the drawer does with that.
//
// MOTION -- absolute lock:
//   On grab we tie the drawer's current position to the hand's current position
//   along the axis. From then on the drawer front is glued to the hand:
//     offset = clamp(grabOffset + (handAlongNow - handAlongAtGrab), 0, open)
//   Push your hand PAST a limit and the drawer waits there; reverse and it stays
//   put until your hand comes BACK to where it stopped, then slides -- exactly
//   like your hand is on a real drawer front.
//
// LEAVING THE DRAWER -- handled per direction:
//   * Along the axis: never gated. However fast you pull, the drawer tracks and
//     just clamps at the ends. (This is what a moving grab-box got wrong before:
//     fast pulls outran the box and froze.)
//   * Sideways (perpendicular): drift off the rail past Off-Axis Release and the
//     drawer FREEZES but stays gripped. Come back onto the rail and it resumes
//     with no jump (while off-rail we keep re-anchoring the lock, so the return
//     is seamless). Only opening your grip actually detaches it.
//
// Everything is in the PARENT's local space (for this axis-aligned kitchen that's
// just world X/Y/Z), and we only ever write localPosition -- never rotation or
// scale -- so the drawer's mirrored scale and 180 deg flip are left untouched.
//
// Aiming it: select the drawer and use the gizmo -- GREEN is the closed (placed)
// position, YELLOW is fully open, the line is the travel. Set Slide Axis and Open
// Distance until yellow lands where the open drawer front should be. Use Marker
// Offset to drop both markers onto the visible drawer front.

using UnityEngine;

namespace GaussianSplatVR.Runtime
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Gaussian Splat/Splat Slide Joint")]
    public class SplatSlideJoint : SplatGrabInteractable
    {
        [Header("Slide")]
        [Tooltip("Direction the drawer pulls out along, in PARENT local space. " +
                 "e.g. (0,0,1) = +Z. Gets normalized. Flip the sign if it slides the wrong way.")]
        [SerializeField] Vector3 m_SlideAxis = new Vector3(0f, 0f, 1f);
        [Tooltip("How far the drawer pulls out, in metres.")]
        [SerializeField] float m_OpenDistance = 0.4f;
        [Tooltip("Flip which way the drawer opens, without editing Slide Axis by hand.")]
        [SerializeField] bool m_Invert = false;

        [Header("Grab tolerance")]
        [Tooltip("How far the hand may drift sideways OFF the slide axis before movement pauses, " +
                 "in metres (a tube of this radius around the rail). Keep it generous -- a fast pull " +
                 "naturally arcs off-axis. Movement ALONG the axis is never gated; the drawer just " +
                 "clamps at its ends. Only opening the grip detaches.")]
        [SerializeField] float m_OffAxisRelease = 0.2f;

        [Header("Feel")]
        [Tooltip("Seconds to ease toward the target. 0 = rigid 1:1 (crispest).")]
        [SerializeField, Range(0f, 0.2f)] float m_Smoothing = 0f;

        [Header("Gizmo")]
        [Tooltip("Shift BOTH markers onto the visible drawer front, in PARENT local space. " +
                 "Green = closed, Yellow = open. Leave zero to mark the object origin.")]
        [SerializeField] Vector3 m_MarkerOffset = Vector3.zero;

        Vector3 m_ClosedLocalPos;   // the placed (closed) position
        float m_Offset;             // current slide distance, 0..OpenDistance
        float m_GrabProj;           // hand's axial projection at the anchor (absolute lock)
        float m_GrabOffset;         // drawer offset at the anchor (absolute lock)
        Vector3 m_Vel;              // SmoothDamp state

        /// <summary>0 = closed, 1 = fully open. For a future HUD / settings readout.</summary>
        public float Openness => m_OpenDistance > 1e-5f ? m_Offset / m_OpenDistance : 0f;

        protected override void Awake()
        {
            base.Awake();
            m_ClosedLocalPos = transform.localPosition;
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            // Start from the placed position so Play begins from a known state.
            m_ClosedLocalPos = transform.localPosition;
            m_Offset = 0f;
            Apply(instant: true);
        }

        protected override void OnGrabBegin(Pose hand) => Anchor(hand.position);

        protected override void OnGrabUpdate(Pose hand)
        {
            float proj = Project(hand.position, out float perp);

            // Off the rail: freeze in place, but keep the lock anchored to the hand
            // so coming back doesn't jump. (Stays gripped -- only the grip opening
            // detaches.)
            if (perp > m_OffAxisRelease)
            {
                Anchor(hand.position);
                return;
            }

            // On the rail: absolute lock. The drawer front follows the hand's axial
            // position, clamped -- overshoot a limit and it waits there until the
            // hand returns.
            m_Offset = Mathf.Clamp(m_GrabOffset + (proj - m_GrabProj), 0f, m_OpenDistance);
            Apply(instant: false);
        }

        protected override void OnGrabRelease()
        {
            // Stays where the hand left it. Nothing to do.
        }

        // ---- helpers ----

        Vector3 SlideDir()
        {
            Vector3 a = m_SlideAxis.sqrMagnitude > 1e-6f ? m_SlideAxis.normalized : Vector3.forward;
            return m_Invert ? -a : a;
        }

        // Tie the current drawer position to the current hand position; the drawer
        // then tracks the hand's ABSOLUTE motion from here.
        void Anchor(Vector3 worldHand)
        {
            m_GrabProj = Project(worldHand, out _);
            m_GrabOffset = m_Offset;
        }

        // Hand position measured against the fixed slide LINE (through the closed
        // position, along the axis), in parent space. Returns the distance ALONG
        // the axis; outputs the perpendicular distance from the line.
        float Project(Vector3 worldPoint, out float perpDistance)
        {
            Transform p = transform.parent;
            Vector3 local = p ? p.InverseTransformPoint(worldPoint) : worldPoint;
            Vector3 rel = local - m_ClosedLocalPos;
            Vector3 dir = SlideDir();
            float along = Vector3.Dot(rel, dir);
            perpDistance = (rel - dir * along).magnitude;
            return along;
        }

        void Apply(bool instant)
        {
            Vector3 target = m_ClosedLocalPos + SlideDir() * m_Offset;
            if (instant || m_Smoothing <= 0f)
                transform.localPosition = target;
            else
                transform.localPosition = Vector3.SmoothDamp(transform.localPosition, target, ref m_Vel, m_Smoothing);
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            Transform p = transform.parent;
            Vector3 closedLocal = Application.isPlaying ? m_ClosedLocalPos : transform.localPosition;
            Vector3 closedPt = closedLocal + m_MarkerOffset;
            Vector3 openPt = closedPt + SlideDir() * m_OpenDistance;

            Vector3 closedWorld = p ? p.TransformPoint(closedPt) : closedPt;
            Vector3 openWorld = p ? p.TransformPoint(openPt) : openPt;

            Gizmos.color = new Color(1f, 1f, 0f, 0.6f);
            Gizmos.DrawLine(closedWorld, openWorld);

            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(closedWorld, Vector3.one * 0.05f);

            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(openWorld, Vector3.one * 0.05f);
        }
#endif
    }
}
