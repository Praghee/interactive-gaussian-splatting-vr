// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// XRGrabDriver -- the bridge between "a hand is gripping" and "an object moves".
//
// One component on the OVRCameraRig, handling both hands. It reads the two
// XRGrabGesture components (which already say whether each hand is closed and
// where it is) and, on a fresh grip, hands the nearest reachable interactable to
// that hand. While held, it feeds the hand pose to the object every frame; when
// the grip opens -- or tracking drops -- it lets go.
//
// The interaction rules live here, in one place:
//   * A grab is released ONLY when the hand opens (or tracking is lost).
//   * One object per hand, and one hand per object (no tug-of-war).
//   * A short haptic tick confirms the grab on controllers.
//
// It owns no grab math -- that's the interactable's job. It only decides WHO
// grabs WHAT, and WHEN.

using System.Collections;
using UnityEngine;

namespace GaussianSplatVR.Runtime
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Gaussian Splat/XR Grab Driver")]
    public class XRGrabDriver : MonoBehaviour
    {
        [Header("Hands")]
        [Tooltip("Left/right gesture sources. Auto-found in children if left empty.")]
        [SerializeField] XRGrabGesture m_Left;
        [SerializeField] XRGrabGesture m_Right;

        [Header("Reach")]
        [Tooltip("How close the grip point must get to a grab volume to take hold, in metres. " +
                 "Being INSIDE the volume always counts, whatever this is.")]
        [SerializeField, Range(0.02f, 0.5f)] float m_ReachRadius = 0.12f;

        [Header("Haptics")]
        [Tooltip("Buzz the controller when a grab takes hold.")]
        [SerializeField] bool m_HapticOnGrab = true;
        [SerializeField, Range(0f, 1f)] float m_HapticAmplitude = 0.4f;
        [Tooltip("Buzz pitch, 0..1. Mid-range reads as a crisp tick; low is a dull thud.")]
        [SerializeField, Range(0f, 1f)] float m_HapticFrequency = 0.5f;
        [SerializeField, Range(0f, 0.3f)] float m_HapticDuration = 0.06f;

        // What each hand currently holds (index 0 = left, 1 = right).
        readonly SplatGrabInteractable[] m_Held = new SplatGrabInteractable[2];

        /// <summary>True while either hand is holding something. Locomotion reads this to lock turning.</summary>
        public bool IsHoldingAnything => m_Held[0] != null || m_Held[1] != null;

        void Awake()
        {
            if (m_Left == null || m_Right == null)
            {
                foreach (var g in GetComponentsInChildren<XRGrabGesture>(true))
                {
                    if (g.WhichHand == XRGrabGesture.Side.Left && m_Left == null) m_Left = g;
                    else if (g.WhichHand == XRGrabGesture.Side.Right && m_Right == null) m_Right = g;
                }
            }
        }

        void Update()
        {
            // Only the bare INTERACTION hand grabs; the controller hand is for
            // locomotion. With no layout present, both hands may grab (fallback).
            var layout = XRControlLayout.Instance;
            bool leftGrabs  = layout == null || layout.InteractionHand == XRControlLayout.Hand.Left;
            bool rightGrabs = layout == null || layout.InteractionHand == XRControlLayout.Hand.Right;

            DriveSide(0, m_Left,  leftGrabs);
            DriveSide(1, m_Right, rightGrabs);

            // Tell each gesture whether it actually caught something, so the marker
            // can show green (holding) vs red (gripping but empty).
            if (m_Left  != null) m_Left.IsHoldingSomething  = m_Held[0] != null;
            if (m_Right != null) m_Right.IsHoldingSomething = m_Held[1] != null;
        }

        // Gate a side: if it may not grab, make sure it isn't holding anything, then skip.
        void DriveSide(int handId, XRGrabGesture gesture, bool canGrab)
        {
            if (!canGrab)
            {
                if (m_Held[handId] != null) { m_Held[handId].EndGrab(); m_Held[handId] = null; }
                return;
            }
            Drive(handId, gesture);
        }

        void Drive(int handId, XRGrabGesture gesture)
        {
            if (gesture == null) return;

            SplatGrabInteractable held = m_Held[handId];

            // Holding something: keep feeding it the hand, until the grip opens
            // or the hand stops being tracked.
            if (held != null)
            {
                if (gesture.IsGrabbing && gesture.IsTracked)
                {
                    held.UpdateGrab(new Pose(gesture.GrabPosition, gesture.GrabRotation));
                }
                else
                {
                    held.EndGrab();
                    m_Held[handId] = null;
                }
                return;
            }

            // Empty hand: only try to take hold on the frame the grip closes.
            if (!gesture.GrabbedThisFrame || !gesture.IsTracked) return;

            SplatGrabInteractable pick = FindNearest(handId, gesture.GrabPosition);
            if (pick == null) return;

            pick.BeginGrab(handId, new Pose(gesture.GrabPosition, gesture.GrabRotation));
            m_Held[handId] = pick;

            if (m_HapticOnGrab) Pulse(gesture.WhichHand);
        }

        // Prefer a volume the grip is INSIDE; otherwise the closest within reach.
        SplatGrabInteractable FindNearest(int handId, Vector3 grip)
        {
            SplatGrabInteractable best = null;
            bool bestInside = false;
            float bestSqr = m_ReachRadius * m_ReachRadius;

            foreach (var it in SplatGrabInteractable.All)
            {
                if (!it.CanGrab(handId) || it.Volume == null) continue;

                bool inside = it.Volume.Contains(grip);
                if (inside)
                {
                    // Among volumes we're inside, take the one whose centre is nearest.
                    float d = (it.Volume.WorldCenter - grip).sqrMagnitude;
                    if (!bestInside || d < bestSqr) { best = it; bestInside = true; bestSqr = d; }
                }
                else if (!bestInside)
                {
                    float d = it.Volume.SqrDistance(grip);
                    if (d < bestSqr) { best = it; bestSqr = d; }
                }
            }
            return best;
        }

        // OVRInput has no fire-and-forget impulse -- vibration runs until it is switched
        // off -- so the duration is timed here.
        void Pulse(XRGrabGesture.Side side)
        {
            var controller = side == XRGrabGesture.Side.Left
                ? OVRInput.Controller.LTouch
                : OVRInput.Controller.RTouch;

            if (!OVRInput.IsControllerConnected(controller)) return;
            StartCoroutine(PulseRoutine(controller));
        }

        IEnumerator PulseRoutine(OVRInput.Controller controller)
        {
            OVRInput.SetControllerVibration(m_HapticFrequency, m_HapticAmplitude, controller);
            yield return new WaitForSeconds(m_HapticDuration);
            OVRInput.SetControllerVibration(0f, 0f, controller);
        }

        // A grab can be released by the object going away, so make sure we never leave
        // a controller buzzing.
        void OnDisable()
        {
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.LTouch);
            OVRInput.SetControllerVibration(0f, 0f, OVRInput.Controller.RTouch);
        }
    }
}
