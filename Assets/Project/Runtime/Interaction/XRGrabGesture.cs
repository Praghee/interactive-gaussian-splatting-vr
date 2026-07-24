// SPDX-License-Identifier: MIT
// InteractiveGaussianSplattingVR
//
// XRGrabGesture -- decides whether ONE hand is currently gripping, from either
// input, and reports where that grip is in the world.
//
//   Hand tracking : all five fingers curled together = grab, uncurl = release.
//   Controllers   : the grip button = grab.
//
// Nothing is grabbed here. This component answers one question -- "is this hand
// closed, and where is it?" -- and everything downstream (apple, drawer, door) reads
// that answer. Keeping the gesture separate means the door never needs to know
// whether you used your hand or a controller.
//
// HOW THE FIST IS MEASURED
// For each finger we add up how sharply it bends at its two knuckles, using the
// joint positions. A flat finger bends by almost nothing; a curled one bends by
// around 150 degrees in total. That total is mapped to 0..1 through Open/Closed
// Degrees, and the hand counts as a fist once EVERY finger passes the threshold.
// Angles are used rather than fingertip-to-palm distance because they don't care
// how big your hands are.
//
// Grab and release use DIFFERENT thresholds on purpose (Grab Enter is higher
// than Release Exit). With a single threshold, a hand hovering right at the line
// flickers between grabbed and released many times a second. The gap between
// them is what makes a grip feel like it holds.
//
// WHERE THE JOINTS COME FROM
// OVRSkeleton, on the OVRHandPrefab instances hanging off the OVRCameraRig anchors.
// There are TWO per side under multimodal -- one on <Side>HandAnchor (bare hand) and
// one on <Side>HandOnControllerAnchor (hand wrapped around a held controller) -- so we
// collect every skeleton of the matching handedness and use whichever is reporting
// valid data. Meta ships two skeleton formats (legacy Hand_* and OpenXR XRHand_*);
// both are handled, chosen from the skeleton's own start bone.
//
// The marker is your debug view: YELLOW while the hand is open, GREEN the moment
// it counts as gripping. Watch it to learn where your own thresholds sit.
//
// SETUP
//   1. Put this on a child of OVRCameraRig, one for the left hand and one for the
//      right, transform reset to zero.
//   2. Set Side.
//   3. (Optional but recommended) Make a small sphere, drop it in as a child,
//      turn OFF its collider, and assign its Renderer to Marker.
//   4. Play, curl your fingers, watch it turn green. Tune Closed Degrees and
//      Grab Enter until it triggers when YOUR fist feels closed.

using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatVR.Runtime
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Gaussian Splat/XR Grab Gesture")]
    public class XRGrabGesture : MonoBehaviour
    {
        public enum Side { Left, Right }

        [Header("Which hand")]
        [SerializeField] Side m_Side = Side.Right;

        [Header("Rig")]
        [Tooltip("The OVRCameraRig. Leave empty to auto-find in the parents, then anywhere in the scene.")]
        [SerializeField] OVRCameraRig m_Rig;

        [Header("Fist detection (hand tracking)")]
        [Tooltip("Total bend across a finger's knuckles when it is FLAT, in degrees. Raise it if an open hand already reads as curled.")]
        [SerializeField] float m_OpenDegrees = 20f;
        [Tooltip("Total bend when a finger is FULLY curled. Lower it if you have to over-clench to trigger a grab.")]
        [SerializeField] float m_ClosedDegrees = 150f;
        [Tooltip("Every finger must curl past this (0..1) to start a grab.")]
        [SerializeField, Range(0f, 1f)] float m_GrabEnter = 0.7f;
        [Tooltip("The grab holds until a finger drops below this. Keep it well under Grab Enter -- the gap is what stops the grip flickering.")]
        [SerializeField, Range(0f, 1f)] float m_GrabExit = 0.45f;
        [Tooltip("Include the thumb in the test. The thumb curls much less than the fingers, so leaving this OFF is usually more reliable.")]
        [SerializeField] bool m_IncludeThumb;
        [Tooltip("Only trust hand joints the runtime reports as high confidence. Off is more responsive, on is steadier.")]
        [SerializeField] bool m_RequireHighConfidence;

        [Header("Grip (controllers)")]
        [Tooltip("How far the grip must be squeezed to count as a grab.")]
        [SerializeField, Range(0.05f, 1f)] float m_GripThreshold = 0.6f;

        [Header("Debug marker")]
        [Tooltip("Show the marker at all. Turn it off once the gesture is tuned, or to see the " +
                 "scene without it. Safe to toggle while playing.")]
        [SerializeField] bool m_ShowMarker = true;
        [Tooltip("Renderer of a small object that shows the grab state. Optional.")]
        [SerializeField] Renderer m_Marker;
        [Tooltip("Lifts the marker clear of the hand/controller model, in the grip's own space. " +
                 "The palm joint sits INSIDE the opaque hand mesh, so without an offset you " +
                 "would never see it. Tune until it floats just off your hand.")]
        [SerializeField] Vector3 m_MarkerOffset = new Vector3(0f, 0.06f, 0f);
        // Three states: open hand, gripping-but-caught-nothing, gripping-and-holding.
        [Tooltip("Hand open -- not gripping.")]
        [SerializeField] Color m_OpenColor = new Color(1f, 0.85f, 0.1f);   // yellow: watching
        [Tooltip("Gripping, but nothing was caught (grip closed away from a grab volume).")]
        [SerializeField] Color m_MissColor = new Color(0.95f, 0.2f, 0.15f); // red: caught nothing
        [Tooltip("Gripping AND holding something.")]
        [SerializeField] Color m_GrabColor = new Color(0.15f, 0.9f, 0.3f); // green: got it
        [Tooltip("Hide the marker when neither hands nor controllers are tracked.")]
        [SerializeField] bool m_HideMarkerWhenUntracked = true;

        // ---- outputs, read by everything downstream ----

        /// <summary>True while this hand is gripping, from either input.</summary>
        public bool IsGrabbing { get; private set; }

        /// <summary>
        /// Set by XRGrabDriver: true while this hand actually has hold of an interactable.
        /// Drives the marker's green (holding) vs red (gripping but caught nothing).
        /// </summary>
        public bool IsHoldingSomething { get; set; }

        /// <summary>Fired on the frame the grip closes / opens.</summary>
        public bool GrabbedThisFrame { get; private set; }
        public bool ReleasedThisFrame { get; private set; }

        /// <summary>How closed the hand is, 0..1. The LEAST curled finger decides.</summary>
        public float Curl01 { get; private set; }

        /// <summary>Where the grip is, in world space. Palm for hands, grip pose for controllers.</summary>
        public Vector3 GrabPosition { get; private set; }
        public Quaternion GrabRotation { get; private set; }

        /// <summary>True when this hand is tracked at all (by either input).</summary>
        public bool IsTracked { get; private set; }

        /// <summary>True when the reading came from hand tracking rather than a controller.</summary>
        public bool UsingHandTracking { get; private set; }

        public Side WhichHand => m_Side;

        /// <summary>Show/hide the debug marker from script (e.g. a settings toggle in the app).</summary>
        public bool ShowMarker { get => m_ShowMarker; set => m_ShowMarker = value; }

        // Each finger, listed as the four bones whose two corner angles we add up.
        // Meta ships two skeleton formats; the chains are the same shape in both.
        static readonly OVRSkeleton.BoneId[][] k_LegacyChains =
        {
            new[] { OVRSkeleton.BoneId.Hand_Thumb1, OVRSkeleton.BoneId.Hand_Thumb2, OVRSkeleton.BoneId.Hand_Thumb3, OVRSkeleton.BoneId.Hand_ThumbTip },
            new[] { OVRSkeleton.BoneId.Hand_Index1, OVRSkeleton.BoneId.Hand_Index2, OVRSkeleton.BoneId.Hand_Index3, OVRSkeleton.BoneId.Hand_IndexTip },
            new[] { OVRSkeleton.BoneId.Hand_Middle1, OVRSkeleton.BoneId.Hand_Middle2, OVRSkeleton.BoneId.Hand_Middle3, OVRSkeleton.BoneId.Hand_MiddleTip },
            new[] { OVRSkeleton.BoneId.Hand_Ring1, OVRSkeleton.BoneId.Hand_Ring2, OVRSkeleton.BoneId.Hand_Ring3, OVRSkeleton.BoneId.Hand_RingTip },
            new[] { OVRSkeleton.BoneId.Hand_Pinky1, OVRSkeleton.BoneId.Hand_Pinky2, OVRSkeleton.BoneId.Hand_Pinky3, OVRSkeleton.BoneId.Hand_PinkyTip },
        };

        static readonly OVRSkeleton.BoneId[][] k_XrChains =
        {
            new[] { OVRSkeleton.BoneId.XRHand_ThumbMetacarpal, OVRSkeleton.BoneId.XRHand_ThumbProximal, OVRSkeleton.BoneId.XRHand_ThumbDistal, OVRSkeleton.BoneId.XRHand_ThumbTip },
            new[] { OVRSkeleton.BoneId.XRHand_IndexProximal, OVRSkeleton.BoneId.XRHand_IndexIntermediate, OVRSkeleton.BoneId.XRHand_IndexDistal, OVRSkeleton.BoneId.XRHand_IndexTip },
            new[] { OVRSkeleton.BoneId.XRHand_MiddleProximal, OVRSkeleton.BoneId.XRHand_MiddleIntermediate, OVRSkeleton.BoneId.XRHand_MiddleDistal, OVRSkeleton.BoneId.XRHand_MiddleTip },
            new[] { OVRSkeleton.BoneId.XRHand_RingProximal, OVRSkeleton.BoneId.XRHand_RingIntermediate, OVRSkeleton.BoneId.XRHand_RingDistal, OVRSkeleton.BoneId.XRHand_RingTip },
            new[] { OVRSkeleton.BoneId.XRHand_LittleProximal, OVRSkeleton.BoneId.XRHand_LittleIntermediate, OVRSkeleton.BoneId.XRHand_LittleDistal, OVRSkeleton.BoneId.XRHand_LittleTip },
        };

        static readonly int k_BaseColor = Shader.PropertyToID("_BaseColor");
        static readonly int k_Color = Shader.PropertyToID("_Color");
        MaterialPropertyBlock m_Mpb;

        // Every skeleton of our handedness in the rig -- bare hand AND hand-on-controller.
        readonly List<OVRSkeleton> m_Skeletons = new();
        // Scratch for the rescan, so the once-a-second sweep allocates nothing.
        readonly List<OVRSkeleton> m_ScanBuffer = new();
        // Bone lookup for the skeleton we're currently reading, rebuilt when it changes.
        readonly Dictionary<OVRSkeleton.BoneId, Transform> m_Bones = new();
        OVRSkeleton m_BoundSkeleton;
        int m_BoundBoneCount = -1;
        float m_NextSkeletonScan;

        OVRInput.Controller Controller =>
            m_Side == Side.Left ? OVRInput.Controller.LTouch : OVRInput.Controller.RTouch;

        void OnEnable()
        {
            IsGrabbing = false; Curl01 = 0f;
            m_NextSkeletonScan = 0f;   // rescan on the first Update
        }

        void Awake()
        {
            if (m_Rig == null) m_Rig = GetComponentInParent<OVRCameraRig>(true);
            if (m_Rig == null) m_Rig = FindAnyObjectByType<OVRCameraRig>();
            if (m_Rig == null)
                Debug.LogWarning("XRGrabGesture: no OVRCameraRig found. Hand joints and controller " +
                                 "poses can't be resolved -- assign Rig on the component.", this);
        }

        void Update()
        {
            bool was = IsGrabbing;

            // Hand tracking first -- if real fingers are visible, they win.
            if (TryReadHand(out float curl, out Pose palm))
            {
                UsingHandTracking = true;
                IsTracked = true;
                Curl01 = curl;
                GrabPosition = palm.position;
                GrabRotation = palm.rotation;
                IsGrabbing = Hysteresis(was, curl, m_GrabEnter, m_GrabExit);
            }
            else if (TryReadController(out float grip, out Pose ctrl))
            {
                UsingHandTracking = false;
                IsTracked = true;
                Curl01 = grip;
                GrabPosition = ctrl.position;
                GrabRotation = ctrl.rotation;
                // The grip button is already decisive, so a small fixed gap is enough.
                IsGrabbing = Hysteresis(was, grip, m_GripThreshold, m_GripThreshold * 0.7f);
            }
            else
            {
                UsingHandTracking = false;
                IsTracked = false;
                Curl01 = 0f;
                IsGrabbing = false;   // lost tracking mid-grip: let go cleanly
            }

            GrabbedThisFrame = IsGrabbing && !was;
            ReleasedThisFrame = !IsGrabbing && was;

            UpdateMarker();
        }

        // Grab needs to pass 'enter'; once grabbing it only lets go below 'exit'.
        static bool Hysteresis(bool current, float value, float enter, float exit)
            => current ? value > exit : value >= enter;

        // ---- hand tracking ----

        bool TryReadHand(out float curl, out Pose palm)
        {
            curl = 0f; palm = default;

            OVRSkeleton skeleton = FindLiveSkeleton();
            if (skeleton == null) return false;
            if (!BindBones(skeleton)) return false;

            bool xr = IsXrSkeleton(skeleton);
            if (!TryGetPalm(xr, out palm)) return false;

            // The least-curled finger decides: a fist means ALL of them are closed.
            var chains = xr ? k_XrChains : k_LegacyChains;
            float lowest = 1f;
            int counted = 0;
            for (int f = m_IncludeThumb ? 0 : 1; f < chains.Length; f++)
            {
                if (!TryFingerCurl(chains[f], out float c)) continue;
                lowest = Mathf.Min(lowest, c);
                counted++;
            }
            if (counted == 0) return false;

            curl = lowest;
            return true;
        }

        // The bare hand and the hand-on-controller are separate prefabs; only one of
        // them has data at a time. Take the first that does.
        OVRSkeleton FindLiveSkeleton()
        {
            // Rescan occasionally -- the rig creates its multimodal anchors lazily, so
            // the hand-on-controller skeleton may not exist when we first look.
            if (Time.unscaledTime >= m_NextSkeletonScan)
            {
                m_NextSkeletonScan = Time.unscaledTime + 1f;
                CollectSkeletons();
            }

            for (int i = 0; i < m_Skeletons.Count; i++)
            {
                var s = m_Skeletons[i];
                if (s == null || !s.IsInitialized || !s.IsDataValid) continue;
                if (m_RequireHighConfidence && !s.IsDataHighConfidence) continue;
                if (s.Bones == null || s.Bones.Count == 0) continue;
                return s;
            }
            return null;
        }

        void CollectSkeletons()
        {
            m_Skeletons.Clear();
            if (m_Rig == null) return;

            bool wantLeft = m_Side == Side.Left;
            m_Rig.GetComponentsInChildren(true, m_ScanBuffer);   // list overload: no array allocated
            foreach (var s in m_ScanBuffer)
            {
                var type = s.GetSkeletonType();
                bool isLeft = type == OVRSkeleton.SkeletonType.HandLeft || type == OVRSkeleton.SkeletonType.XRHandLeft;
                bool isRight = type == OVRSkeleton.SkeletonType.HandRight || type == OVRSkeleton.SkeletonType.XRHandRight;
                if (isLeft == wantLeft && (isLeft || isRight)) m_Skeletons.Add(s);
            }
        }

        static bool IsXrSkeleton(OVRSkeleton s)
        {
            var type = s.GetSkeletonType();
            return type == OVRSkeleton.SkeletonType.XRHandLeft || type == OVRSkeleton.SkeletonType.XRHandRight;
        }

        // Bone ids are stable but their index in Bones is not, so map once per skeleton.
        bool BindBones(OVRSkeleton skeleton)
        {
            if (m_BoundSkeleton == skeleton && m_BoundBoneCount == skeleton.Bones.Count) return m_Bones.Count > 0;

            m_Bones.Clear();
            foreach (var bone in skeleton.Bones)
            {
                if (bone?.Transform == null) continue;
                m_Bones[bone.Id] = bone.Transform;
            }
            m_BoundSkeleton = skeleton;
            m_BoundBoneCount = skeleton.Bones.Count;
            return m_Bones.Count > 0;
        }

        // OVRSkeleton bone transforms are already world-space, so the grip pose needs
        // no tracking-space conversion.
        bool TryGetPalm(bool xr, out Pose palm)
        {
            palm = default;
            Transform t = null;
            if (xr)
            {
                if (!m_Bones.TryGetValue(OVRSkeleton.BoneId.XRHand_Palm, out t))
                    m_Bones.TryGetValue(OVRSkeleton.BoneId.XRHand_Wrist, out t);
            }
            else
            {
                m_Bones.TryGetValue(OVRSkeleton.BoneId.Hand_WristRoot, out t);
            }
            if (t == null) return false;

            palm = new Pose(t.position, t.rotation);
            return true;
        }

        // Total bend across the finger's two knuckles, mapped to 0..1.
        bool TryFingerCurl(OVRSkeleton.BoneId[] chain, out float curl)
        {
            curl = 0f;
            if (!m_Bones.TryGetValue(chain[0], out Transform a)) return false;
            if (!m_Bones.TryGetValue(chain[1], out Transform b)) return false;
            if (!m_Bones.TryGetValue(chain[2], out Transform c)) return false;
            if (!m_Bones.TryGetValue(chain[3], out Transform d)) return false;
            if (a == null || b == null || c == null || d == null) return false;

            Vector3 s1 = b.position - a.position;
            Vector3 s2 = c.position - b.position;
            Vector3 s3 = d.position - c.position;
            if (s1.sqrMagnitude < 1e-10f || s2.sqrMagnitude < 1e-10f || s3.sqrMagnitude < 1e-10f) return false;

            float bend = Vector3.Angle(s1, s2) + Vector3.Angle(s2, s3);
            curl = Mathf.InverseLerp(m_OpenDegrees, m_ClosedDegrees, bend);
            return true;
        }

        // ---- controllers ----

        bool TryReadController(out float grip, out Pose pose)
        {
            grip = 0f; pose = default;

            OVRInput.Controller c = Controller;
            if (!OVRInput.IsControllerConnected(c)) return false;
            if (!OVRInput.GetControllerPositionValid(c)) return false;

            // OVRInput reports controller poses in tracking space; lift them to world.
            Vector3 p = OVRInput.GetLocalControllerPosition(c);
            Quaternion r = OVRInput.GetLocalControllerRotation(c);
            Transform space = m_Rig != null ? m_Rig.trackingSpace : null;
            pose = space != null
                ? new Pose(space.TransformPoint(p), space.rotation * r)
                : new Pose(p, r);

            grip = OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, c);
            if (grip <= 0f && OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, c)) grip = 1f;
            return true;
        }

        // ---- debug marker ----

        void UpdateMarker()
        {
            if (m_Marker == null) return;

            bool show = m_ShowMarker && (IsTracked || !m_HideMarkerWhenUntracked);
            if (m_Marker.gameObject.activeSelf != show) m_Marker.gameObject.SetActive(show);
            if (!show) return;

            m_Marker.transform.SetPositionAndRotation(
                GrabPosition + GrabRotation * m_MarkerOffset, GrabRotation);

            m_Mpb ??= new MaterialPropertyBlock();
            // open hand -> yellow; gripping with nothing caught -> red; holding -> green.
            Color c = !IsGrabbing ? m_OpenColor
                    : IsHoldingSomething ? m_GrabColor
                    : m_MissColor;
            m_Marker.GetPropertyBlock(m_Mpb);
            m_Mpb.SetColor(k_BaseColor, c);   // URP
            m_Mpb.SetColor(k_Color, c);       // built-in, harmless if unused
            m_Marker.SetPropertyBlock(m_Mpb);
        }
    }
}
