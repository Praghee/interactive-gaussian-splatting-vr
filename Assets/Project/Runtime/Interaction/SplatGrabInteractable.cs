// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// SplatGrabInteractable -- the base every grabbable splat object shares.
//
// It owns the grab lifecycle and the shared registry; each subclass only fills
// in what its object is ALLOWED to do while held:
//   SplatFreeGrab   -- follows the hand freely (the apple)
//   SplatSlideJoint -- follows the hand along one axis (the drawer)
//   SplatHingeJoint -- follows the hand around one axis (the door)
//
// The driver (XRGrabDriver) picks an interactable, then feeds it the hand pose:
//   BeginGrab(hand) -> UpdateGrab(hand) every frame -> EndGrab().
// A grab is released ONLY by the driver letting go (hand opened / tracking
// lost). Subclasses never end their own grab -- a joint that hits its limit
// just stops moving; it does not detach. That is the "release only when the
// hand releases" rule, enforced in one place.

using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatVR.Runtime
{
    public abstract class SplatGrabInteractable : MonoBehaviour
    {
        [Header("Grab volume")]
        [Tooltip("The box a hand must reach to grab this. Auto-found in children if left empty.")]
        [SerializeField] protected SplatGrabVolume m_Volume;

        [Tooltip("Can this be grabbed right now?")]
        [SerializeField] bool m_GrabEnabled = true;

        // Every enabled interactable, so the driver can search without allocating.
        static readonly HashSet<SplatGrabInteractable> s_All = new();
        public static IReadOnlyCollection<SplatGrabInteractable> All => s_All;

        public SplatGrabVolume Volume => m_Volume;
        public bool GrabEnabled { get => m_GrabEnabled; set => m_GrabEnabled = value; }
        public bool IsGrabbed { get; private set; }

        /// <summary>Which hand holds this (so a second hand can't steal it). -1 = free.</summary>
        public int HeldByHand { get; private set; } = -1;

        protected virtual void Awake()
        {
            if (m_Volume == null) m_Volume = GetComponentInChildren<SplatGrabVolume>();
        }

        protected virtual void OnEnable() => s_All.Add(this);

        protected virtual void OnDisable()
        {
            if (IsGrabbed) ForceRelease();
            s_All.Remove(this);
        }

        /// <summary>Can hand <paramref name="handId"/> start a grab on this object now?</summary>
        public bool CanGrab(int handId) => m_GrabEnabled && m_Volume != null && (!IsGrabbed || HeldByHand == handId);

        public void BeginGrab(int handId, Pose hand)
        {
            if (!m_GrabEnabled || m_Volume == null) return;
            IsGrabbed = true;
            HeldByHand = handId;
            OnGrabBegin(hand);
        }

        public void UpdateGrab(Pose hand)
        {
            if (!IsGrabbed) return;
            OnGrabUpdate(hand);
        }

        public void EndGrab()
        {
            if (!IsGrabbed) return;
            IsGrabbed = false;
            HeldByHand = -1;
            OnGrabRelease();
        }

        void ForceRelease() { IsGrabbed = false; HeldByHand = -1; OnGrabRelease(); }

        // ---- subclass contract ----
        // hand poses are WORLD space; the subclass decides how much of that motion
        // the object is allowed to take.
        protected abstract void OnGrabBegin(Pose hand);
        protected abstract void OnGrabUpdate(Pose hand);
        protected abstract void OnGrabRelease();
    }
}
