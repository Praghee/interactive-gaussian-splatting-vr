// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// XRControlLayout -- the one place that says which physical hand does what.
//
// The control scheme is: ONE hand holds a controller (locomotion), the OTHER hand
// is bare and tracked (grip / interact). They are always opposite sides. This is
// possible because multimodal is on -- OVRManager's "Simultaneous Hands And
// Controllers" -- so hand tracking keeps flowing while a controller is held.
//
// Which side is which is no longer declared by hand: OVRInput tells us, per hand,
// whether a controller is actually being held (GetControllerIsInHandState). Pick a
// controller up with the other hand mid-session and the whole scheme follows.
// Preferred Controller Hand is only the tiebreak, used when the answer is
// ambiguous -- both hands holding a controller, or neither hand tracked yet.
//
// Everything downstream reads this instead of hard-coding a side:
//   * XRSmoothLocomotion drives from ControllerType's stick + grip.
//   * XRGrabDriver only lets the InteractionHand side grab.
//
// It does NOT show or hide hand/controller models any more. That is now the job of
// OVRControllerHelper's Show State on the OVRControllerPrefab / OVRHandPrefab sitting
// on the OVRCameraRig anchors (see the multimodal setup). It still owns the grab
// marker, which is ours: the marker shows only on the active (interaction) hand.
//
// Put ONE of these on OVRCameraRig.

using UnityEngine;

namespace GaussianSplatVR.Runtime
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Gaussian Splat/XR Control Layout")]
    public class XRControlLayout : MonoBehaviour
    {
        public enum Hand { Left, Right }

        [Header("Sides")]
        [Tooltip("Which hand holds the controller when we can't tell -- both hands holding one, " +
                 "or no hand tracked yet. Once one hand is clearly holding a controller and the " +
                 "other isn't, that wins and this is ignored.")]
        [SerializeField] Hand m_PreferredControllerHand = Hand.Right;

        [Header("Grab marker")]
        [Tooltip("Show the grab marker on the ACTIVE (interaction) hand only -- the controller " +
                 "hand's marker is always hidden. Untick to hide it on both.")]
        [SerializeField] bool m_ShowActiveSideMarker = true;
        [Tooltip("Left/right grab gestures. Auto-found in children if left empty.")]
        [SerializeField] XRGrabGesture m_LeftGesture;
        [SerializeField] XRGrabGesture m_RightGesture;

        /// <summary>The active layout. Consumers read this; null-safe fallbacks assume RIGHT=controller.</summary>
        public static XRControlLayout Instance { get; private set; }

        // Controller side = locomotion; Interaction side = grab. Always opposite.
        public Hand ControllerHand { get; private set; }
        public Hand InteractionHand => ControllerHand == Hand.Left ? Hand.Right : Hand.Left;

        /// <summary>The controller to read sticks/buttons/haptics from, for the locomotion hand.</summary>
        public OVRInput.Controller ControllerType =>
            ControllerHand == Hand.Left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;

        public bool IsControllerHand(Hand h) => h == ControllerHand;
        public bool IsInteractionHand(Hand h) => h == InteractionHand;

        // Default assumption when no layout exists: RIGHT is the controller, LEFT grabs.
        public static Hand ControllerHandOrDefault => Instance != null ? Instance.ControllerHand : Hand.Right;
        public static Hand InteractionHandOrDefault => Instance != null ? Instance.InteractionHand : Hand.Left;
        public static OVRInput.Controller ControllerTypeOrDefault =>
            Instance != null ? Instance.ControllerType : OVRInput.Controller.RTouch;

        /// <summary>Maps a side to the OVRInput controller for that side, regardless of role.</summary>
        public static OVRInput.Controller ControllerFor(Hand h) =>
            h == Hand.Left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;

        /// <summary>Maps a side to the OVRInput hand for that side.</summary>
        public static OVRInput.Hand HandFor(Hand h) =>
            h == Hand.Left ? OVRInput.Hand.HandLeft : OVRInput.Hand.HandRight;

        int m_LastSig = -1;   // change signature (side + marker toggle), so we only re-apply on change

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[XRControlLayout] more than one in the scene; keeping the first.", this);
                enabled = false;
                return;
            }
            Instance = this;
            ControllerHand = m_PreferredControllerHand;

            if (m_LeftGesture == null || m_RightGesture == null)
            {
                foreach (var g in GetComponentsInChildren<XRGrabGesture>(true))
                {
                    if (g.WhichHand == XRGrabGesture.Side.Left && m_LeftGesture == null) m_LeftGesture = g;
                    else if (g.WhichHand == XRGrabGesture.Side.Right && m_RightGesture == null) m_RightGesture = g;
                }
            }
        }

        void OnEnable() => m_LastSig = -1;   // force a re-apply

        void OnDestroy() { if (Instance == this) Instance = null; }

        void Update()
        {
            ControllerHand = ResolveControllerHand();

            // Re-apply only when the side or the marker toggle changes.
            int sig = (InteractionHand == Hand.Left ? 0 : 1) * 2 + (m_ShowActiveSideMarker ? 1 : 0);
            if (sig == m_LastSig) return;
            m_LastSig = sig;

            // Marker only on the active (interaction) hand; controller hand always off.
            bool leftIsInteraction = InteractionHand == Hand.Left;
            var interaction = leftIsInteraction ? m_LeftGesture : m_RightGesture;
            var controller = leftIsInteraction ? m_RightGesture : m_LeftGesture;
            if (interaction != null) interaction.ShowMarker = m_ShowActiveSideMarker;
            if (controller != null) controller.ShowMarker = false;
        }

        // Ask OVRInput which hand is actually holding a controller. Only a clear
        // answer -- exactly one side holding -- moves the layout; anything else keeps
        // the side we already had, so a dropped frame of tracking doesn't flip the
        // whole control scheme mid-grab.
        Hand ResolveControllerHand()
        {
            var left = OVRInput.GetControllerIsInHandState(OVRInput.Hand.HandLeft);
            var right = OVRInput.GetControllerIsInHandState(OVRInput.Hand.HandRight);

            bool leftHolds = left == OVRInput.ControllerInHandState.ControllerInHand;
            bool rightHolds = right == OVRInput.ControllerInHandState.ControllerInHand;

            if (leftHolds && !rightHolds) return Hand.Left;
            if (rightHolds && !leftHolds) return Hand.Right;

            // Both holding, or neither hand tracked: fall back to the preference, but
            // only while nothing is established -- otherwise hold what we have.
            if (leftHolds && rightHolds) return m_PreferredControllerHand;
            if (left == OVRInput.ControllerInHandState.NoHand && right == OVRInput.ControllerInHandState.NoHand)
                return m_PreferredControllerHand;

            return ControllerHand;
        }
    }
}
