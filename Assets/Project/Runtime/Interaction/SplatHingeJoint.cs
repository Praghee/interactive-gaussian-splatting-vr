// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// SplatHingeJoint -- a door. Grab the handle and it swings around the hinge,
// following your hand, clamped between closed and open. Let go and it stays.
//
// The rotational twin of SplatSlideJoint. It lives on the DOOR PIVOT (an empty
// on the hinge edge, identity rotation, with the door cloud parented under it),
// and only ever writes pivot.localRotation = Euler(0, angle, 0). The door cloud
// is never touched, so its mirrored scale and 180 deg flip stay sealed -- and
// because the pivot has no mirror, the swing has no handedness surprises.
//
// MOTION -- absolute lock (rotational):
//   On grab we tie the door's current angle to the hand's current angle around
//   the hinge. From then on the door follows the hand 1:1:
//     angle = clamp(grabAngle + (handAngleNow - handAngleAtGrab), closed, open)
//   Swing your hand PAST a limit and the door waits at the limit; reverse and it
//   stays put until your hand comes BACK to where it stopped, then swings -- like
//   your hand is on a real handle.
//
// LEAVING THE HANDLE -- handled per direction:
//   * The swing (around the hinge): never gated. However fast you swing, the door
//     tracks and just clamps at the ends.
//   * Off the handle (hand drifts in/out from the hinge, or up/down): past
//     Off-Arc Release the door FREEZES but stays gripped. Come back onto the
//     handle and it resumes with no jump (we re-anchor while off-handle). Only
//     opening the grip detaches.
//
// Angles are measured around the hinge in the PIVOT's PARENT space (for this
// axis-aligned kitchen, world) -- the hinge is vertical (parent up / Y).
//
// Aiming it: select the pivot and use the gizmo -- the CYAN line is the hinge
// axis, GREEN is the door at the closed angle, YELLOW at the open angle, and the
// arc is the swing. Drag Marker Offset (a point ON the door, in pivot-local
// space) until green sits on the closed door; yellow then shows where it opens
// to. If the arc SKATES instead of swinging, the pivot isn't on the hinge edge.

using UnityEngine;

namespace GaussianSplatVR.Runtime
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Gaussian Splat/Splat Hinge Joint")]
    public class SplatHingeJoint : SplatGrabInteractable
    {
        [Header("Hinge angles (pivot local Y, degrees)")]
        [Tooltip("Closed angle -- 0 if the pivot is placed at the door's rest orientation.")]
        [SerializeField] float m_ClosedAngle = 0f;
        [Tooltip("Fully-open angle. Sign depends on which way the door swings; flip it if it opens the wrong way.")]
        [SerializeField] float m_OpenAngle = -120f;
        [Tooltip("Flip which way the door swings, without editing Open Angle's sign by hand.")]
        [SerializeField] bool m_Invert = false;

        [Header("Grab tolerance")]
        [Tooltip("How far the hand may drift OFF the handle (toward/away from the hinge, or up/down) " +
                 "before movement pauses, in metres. Keep it generous -- an arm swing isn't a perfect " +
                 "arc. The swing itself is never gated; the door just clamps at its ends. Only opening " +
                 "the grip detaches.")]
        [SerializeField] float m_OffArcRelease = 0.2f;

        [Header("Feel")]
        [Tooltip("Seconds to ease toward the target. 0 = rigid 1:1 (crispest).")]
        [SerializeField, Range(0f, 0.2f)] float m_Smoothing = 0f;

        [Header("Gizmo")]
        [Tooltip("A point ON the door (e.g. the handle), in the pivot's LOCAL space. Drag it until " +
                 "the GREEN marker sits on the CLOSED door; YELLOW then shows where it swings to.")]
        [SerializeField] Vector3 m_MarkerOffset = new Vector3(0.4f, 0f, 0f);

        // Open angle after the Invert toggle -- used everywhere (clamp, gizmo, readout).
        float OpenAngle => m_Invert ? -m_OpenAngle : m_OpenAngle;

        float m_Angle;          // current door angle
        float m_GrabHandAngle;  // hand angle around the hinge at the anchor
        float m_GrabDoorAngle;  // door angle at the anchor
        float m_GrabRadius;     // hand distance from the hinge axis at the anchor
        float m_GrabHeight;     // hand height relative to the pivot at the anchor

        /// <summary>0 = closed, 1 = fully open. For a future HUD / settings readout.</summary>
        public float Openness
        {
            get
            {
                float span = OpenAngle - m_ClosedAngle;
                return Mathf.Abs(span) > 1e-4f ? Mathf.Clamp01((m_Angle - m_ClosedAngle) / span) : 0f;
            }
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            // Start closed so Play begins from a known state.
            m_Angle = m_ClosedAngle;
            Apply(instant: true);
        }

        protected override void OnGrabBegin(Pose hand) => Anchor(hand.position);

        protected override void OnGrabUpdate(Pose hand)
        {
            float handAngle = HandAngle(hand.position, out float radius, out float height);

            // Off the handle: freeze, but keep the anchor on the hand so coming
            // back doesn't jump. (Stays gripped -- only the grip opening detaches.)
            float off = Mathf.Sqrt((radius - m_GrabRadius) * (radius - m_GrabRadius)
                                 + (height - m_GrabHeight) * (height - m_GrabHeight));
            if (off > m_OffArcRelease)
            {
                Anchor(hand.position);
                return;
            }

            // On the handle: absolute lock. The door follows the hand's angle around
            // the hinge, clamped -- overshoot a limit and it waits until the hand
            // returns.
            float target = m_GrabDoorAngle + Mathf.DeltaAngle(m_GrabHandAngle, handAngle);
            m_Angle = ClampAngle(target);
            Apply(instant: false);
        }

        protected override void OnGrabRelease()
        {
            // Stays where the hand left it. Nothing to do.
        }

        // ---- helpers ----

        // Tie the door's current angle to the hand's current angle around the hinge;
        // the door then tracks the hand's ABSOLUTE swing from here.
        void Anchor(Vector3 worldHand)
        {
            m_GrabHandAngle = HandAngle(worldHand, out m_GrabRadius, out m_GrabHeight);
            m_GrabDoorAngle = m_Angle;
        }

        // Hand angle around the vertical hinge through the pivot, in parent space.
        // Also outputs the hand's distance from the hinge axis (radius) and its
        // height relative to the pivot -- the two the off-handle gate watches.
        float HandAngle(Vector3 worldPoint, out float radius, out float height)
        {
            Transform p = transform.parent;
            Vector3 local = p ? p.InverseTransformPoint(worldPoint) : worldPoint;
            Vector3 rel = local - transform.localPosition;
            radius = Mathf.Sqrt(rel.x * rel.x + rel.z * rel.z);
            height = rel.y;
            // atan2(x, z) matches Euler(0, y, 0): a point at +Z sits at 0 deg.
            return Mathf.Atan2(rel.x, rel.z) * Mathf.Rad2Deg;
        }

        float ClampAngle(float a)
        {
            float lo = Mathf.Min(m_ClosedAngle, OpenAngle);
            float hi = Mathf.Max(m_ClosedAngle, OpenAngle);
            return Mathf.Clamp(a, lo, hi);
        }

        void Apply(bool instant)
        {
            Quaternion target = Quaternion.Euler(0f, m_Angle, 0f);
            if (instant || m_Smoothing <= 0f)
                transform.localRotation = target;
            else
                transform.localRotation = Quaternion.Slerp(transform.localRotation, target,
                    1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(m_Smoothing, 1e-4f)));
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            // Keep the editor preview honest when angles are tweaked while not playing.
            if (!Application.isPlaying)
                transform.localRotation = Quaternion.Euler(0f, m_ClosedAngle, 0f);
        }

        void OnDrawGizmosSelected()
        {
            Transform p = transform.parent;
            Vector3 rep = m_MarkerOffset;

            // World position of the marker point when the pivot sits at a given angle.
            System.Func<float, Vector3> worldAt = angle =>
            {
                Vector3 inParent = transform.localPosition + Quaternion.Euler(0f, angle, 0f) * rep;
                return p ? p.TransformPoint(inParent) : inParent;
            };

            Vector3 pivotWorld = p ? p.TransformPoint(transform.localPosition) : transform.localPosition;
            Vector3 up = (p ? p.TransformDirection(Vector3.up) : Vector3.up).normalized;

            // hinge axis (vertical through the pivot)
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(pivotWorld - up * 0.4f, pivotWorld + up * 0.4f);

            // closed marker (green)
            Vector3 closedW = worldAt(m_ClosedAngle);
            Gizmos.color = Color.green;
            Gizmos.DrawLine(pivotWorld, closedW);
            Gizmos.DrawWireCube(closedW, Vector3.one * 0.05f);

            // open marker (yellow)
            Vector3 openW = worldAt(OpenAngle);
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(pivotWorld, openW);
            Gizmos.DrawWireCube(openW, Vector3.one * 0.05f);

            // swing arc
            Gizmos.color = new Color(1f, 1f, 0f, 0.6f);
            Vector3 prev = closedW;
            const int seg = 20;
            for (int i = 1; i <= seg; i++)
            {
                float a = Mathf.Lerp(m_ClosedAngle, OpenAngle, i / (float)seg);
                Vector3 cur = worldAt(a);
                Gizmos.DrawLine(prev, cur);
                prev = cur;
            }
        }
#endif
    }
}
