// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// XRSmoothLocomotion -- ONE-stick fly/move for inspecting the scene in VR.
//
// Single-controller scheme (the other hand is bare, for grabbing): all locomotion
// is on the controller side's ONE thumbstick, with the trigger as a mode switch.
//
//   Stick, no grip : MOVE  -- forward/back + strafe on the horizontal plane,
//                             relative to where you're looking.
//   Stick + grip   : TURN  -- X axis yaws you smoothly (around your head).
//                  : FLY   -- Y axis moves you up/down (optional).
//
// Which controller drives it comes from XRControlLayout, which detects live -- via
// OVRInput.GetControllerIsInHandState -- whichever hand is actually holding one. If
// no layout is present it defaults to the RIGHT controller.
//
// Everything is KINEMATIC and eased: stick input is run through SmoothDamp so
// motion accelerates and decelerates smoothly instead of snapping on/off. A radial
// deadzone keeps the sticks from drifting when centred.
//
// This drives the OVRCameraRig (the rig), never the camera directly -- the headset
// still owns the camera pose, we just move/rotate the rig underneath it. Turning
// rotates around the camera position so you pivot about your head.
//
// Camera reference: if you leave Camera empty it is resolved automatically at
// startup -- first OVRCameraRig's centreEyeAnchor, then a Camera in the children of
// this rig, then Camera.main. (A null camera was why movement silently did nothing
// before: the update needs the camera to know which way "forward" is.)
//
// Built on OVRInput, so a held controller keeps reporting its stick even while the
// other hand is tracked as a bare hand (multimodal).
//
// Setup: put this on OVRCameraRig. Camera can be left empty (auto-found) or you
// can drag CenterEyeAnchor in explicitly.

using UnityEngine;
using UnityEngine.Serialization;

namespace GaussianSplatVR.Runtime
{
    [DisallowMultipleComponent]
    public class XRSmoothLocomotion : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The tracked centre-eye camera under this rig. " +
                 "Leave empty to auto-find (CenterEyeAnchor, then children, then Camera.main).")]
        [SerializeField] Transform m_Camera;

        [Header("Move (stick, no trigger)")]
        [Tooltip("Horizontal movement speed, metres/second.")]
        [SerializeField] float m_MoveSpeed = 2.0f;
        [Tooltip("Move relative to head facing (on) or the rig's forward (off).")]
        [SerializeField] bool m_MoveRelativeToHead = true;

        [Header("Turn (stick X + grip)")]
        [Tooltip("Smooth turn speed, degrees/second.")]
        [SerializeField] float m_TurnSpeed = 90f;

        [Header("Fly (stick Y + grip)")]
        [Tooltip("Allow up/down movement on the stick's Y axis while the grip is held.")]
        [SerializeField] bool m_EnableVertical = true;
        [Tooltip("Vertical fly speed, metres/second.")]
        [SerializeField] float m_VerticalSpeed = 1.5f;

        [Header("Mode grip")]
        [Tooltip("Squeeze the locomotion controller's grip past this to switch the stick " +
                 "from MOVE to TURN/FLY.")]
        [SerializeField, Range(0.1f, 0.95f), FormerlySerializedAs("m_TriggerThreshold")] float m_GripThreshold = 0.5f;

        [Header("Feel")]
        [Range(0f, 0.4f)]
        [Tooltip("Radial stick deadzone (ignored below this).")]
        [SerializeField] float m_Deadzone = 0.15f;
        [Tooltip("Seconds to ease input in/out. Higher = smoother/floatier. 0 = instant.")]
        [SerializeField] float m_Smoothing = 0.08f;
        [Tooltip("In TURN/FLY mode, do only ONE action at a time -- turn OR up/down, " +
                 "whichever you push toward most. Prevents turning while rising/lowering.")]
        [SerializeField] bool m_SingleActionOnTurnStick = true;

        [Header("Grab")]
        [Tooltip("Lock the turn stick while a hand is holding something. Turning rotates the rig " +
                 "around your head, which whips your grip through a fast arc and flings the door/drawer " +
                 "open -- so we disable it while grabbing. Move and fly still work.")]
        [SerializeField] bool m_LockTurnWhileGrabbing = true;
        [Tooltip("The grab driver to ask 'is anything held?'. Auto-found on this object if left empty.")]
        [SerializeField] XRGrabDriver m_GrabDriver;

        // smoothing state
        Vector2 m_MoveSmoothed, m_MoveVel;
        Vector2 m_TurnSmoothed, m_TurnVel;   // x = turn, y = vertical

        void Awake()
        {
            ResolveCamera();
            // Driver lives on the same OVRCameraRig object as this component.
            if (m_GrabDriver == null) m_GrabDriver = GetComponent<XRGrabDriver>();
        }

        void ResolveCamera()
        {
            if (m_Camera != null) return;

            // 1) the rig's own centre eye -- the right answer whenever we're on OVRCameraRig
            var rig = GetComponent<OVRCameraRig>();
            if (rig == null) rig = GetComponentInChildren<OVRCameraRig>();
            if (rig != null && rig.centerEyeAnchor != null) { m_Camera = rig.centerEyeAnchor; return; }

            // 2) a Camera somewhere under this rig
            var cam = GetComponentInChildren<Camera>();
            if (cam != null) { m_Camera = cam.transform; return; }

            // 3) fall back to the tagged MainCamera
            if (Camera.main != null) { m_Camera = Camera.main.transform; return; }

            Debug.LogWarning("XRSmoothLocomotion: no camera found. Assign Camera on " +
                             "the component (drag in CenterEyeAnchor).", this);
        }

        void Update()
        {
            // late-resolve in case the camera spawned after Awake
            if (m_Camera == null) { ResolveCamera(); if (m_Camera == null) return; }

            float dt = Time.deltaTime;

            // One controller drives everything; the grip switches the stick's job.
            OVRInput.Controller loco = XRControlLayout.ControllerTypeOrDefault;
            Vector2 stick = ReadStick(loco);
            bool turnMode = ReadGrip(loco);   // grip held => TURN/FLY, else MOVE

            Vector2 rawMove = turnMode ? Vector2.zero : stick;   // x = strafe, y = fwd/back
            Vector2 rawTurn = turnMode ? stick : Vector2.zero;   // x = turn,   y = vertical

            // Lock turning while holding: a head-centred yaw whips your grip and flings
            // the door/drawer open. Move and fly (rawTurn.y) stay free.
            if (m_LockTurnWhileGrabbing && m_GrabDriver != null && m_GrabDriver.IsHoldingAnything)
                rawTurn.x = 0f;

            // One action at a time in TURN/FLY mode: the axis you push toward most
            // wins, the other is zeroed. So you never turn AND go up/down together.
            if (m_SingleActionOnTurnStick)
            {
                if (Mathf.Abs(rawTurn.x) >= Mathf.Abs(rawTurn.y)) rawTurn.y = 0f;  // turn only
                else                                              rawTurn.x = 0f;  // up/down only
            }

            // ease in/out for a smooth transition
            m_MoveSmoothed = Vector2.SmoothDamp(m_MoveSmoothed, rawMove, ref m_MoveVel, m_Smoothing);
            m_TurnSmoothed = Vector2.SmoothDamp(m_TurnSmoothed, rawTurn, ref m_TurnVel, m_Smoothing);

            // --- horizontal move, flattened to the ground plane ---
            Vector3 fwd    = m_MoveRelativeToHead ? m_Camera.forward : transform.forward;
            Vector3 right3  = m_MoveRelativeToHead ? m_Camera.right   : transform.right;
            fwd.y = 0f; right3.y = 0f;
            fwd.Normalize(); right3.Normalize();

            transform.position += (right3 * m_MoveSmoothed.x + fwd * m_MoveSmoothed.y) * (m_MoveSpeed * dt);

            // --- vertical fly ---
            if (m_EnableVertical)
                transform.position += Vector3.up * (m_TurnSmoothed.y * m_VerticalSpeed * dt);

            // --- smooth yaw around the head ---
            float yaw = m_TurnSmoothed.x * m_TurnSpeed * dt;
            if (Mathf.Abs(yaw) > 0f)
                transform.RotateAround(m_Camera.position, Vector3.up, yaw);
        }

        Vector2 ReadStick(OVRInput.Controller controller)
        {
            if (!OVRInput.IsControllerConnected(controller)) return Vector2.zero;
            Vector2 v = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, controller);
            return ApplyDeadzone(v, m_Deadzone);
        }

        // Grip as the MOVE vs TURN/FLY mode switch (analog first, digital fallback).
        bool ReadGrip(OVRInput.Controller controller)
        {
            if (!OVRInput.IsControllerConnected(controller)) return false;
            float v = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, controller);
            if (v > 0f) return v >= m_GripThreshold;
            return OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, controller);
        }

        // Radial deadzone with edge rescaling so motion ramps from zero at the edge.
        static Vector2 ApplyDeadzone(Vector2 v, float dz)
        {
            float m = v.magnitude;
            if (m <= dz) return Vector2.zero;
            float scaled = Mathf.Clamp01((m - dz) / (1f - dz));
            return v * (scaled / m);
        }
    }
}