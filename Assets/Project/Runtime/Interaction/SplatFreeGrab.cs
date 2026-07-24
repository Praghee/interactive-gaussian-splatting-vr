// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// SplatFreeGrab -- a whole cloud you can pick up, move and rotate freely, then
// leave wherever you let go (the apple).
//
// No hinge, no rail: the object rigidly follows the hand in all six degrees of
// freedom. We record the hand->object offset at the instant of the grab, so the
// object keeps the exact spot and angle it had relative to your hand -- it does
// not snap to the palm. Every frame it re-applies that same offset to the
// current hand pose.
//
// World pose is driven directly (position + rotation); the object's SCALE is
// never touched, so a cloud's mirrored/negative scale -- which every splat in
// this project has -- is carried along untouched and nothing flips.

using UnityEngine;

namespace GaussianSplatVR.Runtime
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Gaussian Splat/Splat Free Grab")]
    public class SplatFreeGrab : SplatGrabInteractable
    {
        [Header("What the hand controls")]
        [Tooltip("Follow the hand's position.")]
        [SerializeField] bool m_Move = true;
        [Tooltip("Follow the hand's rotation. Turn off for move-only (stays upright).")]
        [SerializeField] bool m_Rotate = true;

        [Header("Feel")]
        [Tooltip("Seconds to ease toward the hand. 0 = rigid 1:1 (crispest). " +
                 "A little smoothing hides hand-tracking jitter at the cost of slight lag.")]
        [SerializeField, Range(0f, 0.2f)] float m_Smoothing = 0f;

        // hand-space offset captured at grab time
        Vector3 m_PosOffset;      // object position expressed in hand space
        Quaternion m_RotOffset;   // object rotation expressed in hand space

        Vector3 m_VelPos;         // SmoothDamp state

        protected override void OnGrabBegin(Pose hand)
        {
            Quaternion invHand = Quaternion.Inverse(hand.rotation);
            m_PosOffset = invHand * (transform.position - hand.position);
            m_RotOffset = invHand * transform.rotation;
            m_VelPos = Vector3.zero;
        }

        protected override void OnGrabUpdate(Pose hand)
        {
            Vector3 targetPos = m_Move ? hand.position + hand.rotation * m_PosOffset : transform.position;
            Quaternion targetRot = m_Rotate ? hand.rotation * m_RotOffset : transform.rotation;

            if (m_Smoothing > 0f)
            {
                transform.position = Vector3.SmoothDamp(transform.position, targetPos, ref m_VelPos, m_Smoothing);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot,
                    1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(m_Smoothing, 1e-4f)));
            }
            else
            {
                transform.SetPositionAndRotation(targetPos, targetRot);
            }
        }

        protected override void OnGrabRelease()
        {
            // Leave it exactly where the hand let go. Nothing to do.
        }
    }
}
