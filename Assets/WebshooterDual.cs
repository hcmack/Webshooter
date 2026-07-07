using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.Gestures;

// Dual-hand web shooter with sagging-rope tethers.
// Each hand independently: detect the web-shooter pose (index & pinky extended,
// middle & ring curled, thumb ignored), fire a rope that sticks to whatever it
// hits, and (if the hit object has a Rigidbody) pull it as you move your hand.
//
// Attach to ONE GameObject. It builds its own rope renderers at runtime, so no
// LineRenderer is required on this object. Assign a rope material (URP/Unlit).
public class WebShooterDual : MonoBehaviour
{
    [Header("Setup")]
    [Tooltip("Your 'XR Origin (1)' transform. Required for correct world-space aim.")]
    [SerializeField] Transform m_XROrigin;
    [Tooltip("Material for the rope line. Use URP/Unlit or it renders pink.")]
    [SerializeField] Material m_RopeMaterial;
    [SerializeField] float m_RopeWidth = 0.006f;

    [Header("Pose thresholds (full curl: 0 = straight, 1 = curled)")]
    [SerializeField] float m_IndexExtendedMax = 0.35f;
    [SerializeField] float m_PinkyExtendedMax = 0.45f;
    [SerializeField] float m_MiddleCurledMin = 0.55f;
    [SerializeField] float m_RingCurledMin = 0.50f;
    [SerializeField] float m_Hysteresis = 0.12f;
    [Range(1f, 30f)][SerializeField] float m_Smoothing = 12f;

    [Header("Fire")]
    [SerializeField] float m_ArmHoldTime = 0.08f;
    [SerializeField] float m_Range = 15f;
    [SerializeField] LayerMask m_HitMask = ~0;
    [Tooltip("Release the rope when you break the pose.")]
    [SerializeField] bool m_ReleaseOnPoseEnd = true;

    [Header("Pull / tether")]
    [Tooltip("Spring strength pulling the object toward your hand.")]
    [SerializeField] float m_PullSpring = 40f;
    [SerializeField] float m_PullDamper = 5f;
    [Tooltip("Max force the rope can apply (stops explosive yanks).")]
    [SerializeField] float m_MaxPullForce = 60f;

    [Header("Rope sag")]
    [Tooltip("How many segments the rope is drawn with.")]
    [SerializeField] int m_RopeSegments = 14;
    [Tooltip("How far the rope droops at full slack, in meters.")]
    [SerializeField] float m_SagAmount = 0.25f;

    [Header("Aim assist")]
    [SerializeField] float m_AssistAngle = 12f;
    [SerializeField] Transform m_TargetsRoot;

    [Header("Feedback")]
    [SerializeField] AudioSource m_Thwip;

    XRHandSubsystem m_Subsystem;
    static readonly List<XRHandSubsystem> s_Subsystems = new();

    HandWeb m_Left, m_Right;

    void Awake()
    {
        m_Left = new HandWeb(this, Handedness.Left);
        m_Right = new HandWeb(this, Handedness.Right);
    }

    void Update()
    {
        if (!EnsureSubsystem()) return;
        m_Left.Tick(m_Subsystem.leftHand);
        m_Right.Tick(m_Subsystem.rightHand);
    }

    void FixedUpdate()
    {
        m_Left.FixedTick();
        m_Right.FixedTick();
    }

    bool EnsureSubsystem()
    {
        if (m_Subsystem != null && m_Subsystem.running) return true;
        SubsystemManager.GetSubsystems(s_Subsystems);
        if (s_Subsystems.Count > 0) m_Subsystem = s_Subsystems[0];
        return m_Subsystem != null;
    }

    Vector3 ToWorldPos(Vector3 p) => m_XROrigin ? m_XROrigin.TransformPoint(p) : p;
    Vector3 ToWorldDir(Vector3 d) => m_XROrigin ? m_XROrigin.TransformDirection(d) : d;

    // Per-hand state + behavior.
    class HandWeb
    {
        readonly WebShooterDual o;
        readonly Handedness hand;

        static readonly XRHandFingerID[] k_Tracked =
        {
            XRHandFingerID.Index, XRHandFingerID.Middle,
            XRHandFingerID.Ring,  XRHandFingerID.Little
        };

        readonly float[] curl = new float[4];
        bool curlInit;
        bool posedPrev;
        float armTimer;

        LineRenderer rope;
        Transform wristAnchor;         // kinematic body that follows the wrist
        Rigidbody wristBody;
        SpringJoint joint;             // connects wristAnchor to the hit body
        Rigidbody hitBody;
        Vector3 attachLocal;           // hit point in the hit object's local space
        bool attached;
        Vector3 lastWristPos;

        public HandWeb(WebShooterDual owner, Handedness h) { o = owner; hand = h; }

        void EnsureVisuals()
        {
            if (rope != null) return;

            var go = new GameObject($"Rope_{hand}");
            go.transform.SetParent(o.transform, false);
            rope = go.AddComponent<LineRenderer>();
            rope.useWorldSpace = true;
            rope.material = o.m_RopeMaterial;
            rope.widthMultiplier = o.m_RopeWidth;
            rope.numCapVertices = 2;
            rope.positionCount = o.m_RopeSegments;
            rope.enabled = false;
            rope.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var an = new GameObject($"WristAnchor_{hand}");
            an.transform.SetParent(o.transform, false);
            wristBody = an.AddComponent<Rigidbody>();
            wristBody.isKinematic = true;
            wristBody.useGravity = false;
            wristAnchor = an.transform;
        }

        // --- called every frame (Update) ---
        public void Tick(XRHand xrHand)
        {
            EnsureVisuals();

            if (!xrHand.isTracked)
            {
                curlInit = false;
                if (o.m_ReleaseOnPoseEnd) Release();
                rope.enabled = false;
                return;
            }

            bool posed = UpdatePose(xrHand);

            // fire on the rising edge of a held pose
            if (posed)
            {
                armTimer += Time.deltaTime;
                if (!posedPrev && armTimer >= o.m_ArmHoldTime && !attached)
                    Fire(xrHand);
            }
            else
            {
                armTimer = 0f;
                if (o.m_ReleaseOnPoseEnd) Release();
            }
            posedPrev = posed && armTimer >= o.m_ArmHoldTime;

            // keep the wrist anchor glued to the wrist
            if (TryWrist(xrHand, out var wpos))
            {
                wristAnchor.position = wpos;
                lastWristPos = wpos;
            }

            DrawRope(xrHand);
        }

        // --- called every physics step (FixedUpdate) ---
        public void FixedTick()
        {
            if (!attached || joint == null || hitBody == null) return;
            // clamp the pull so fast hand moves don't launch objects
            var f = joint.currentForce.magnitude;
            if (f > o.m_MaxPullForce)
                hitBody.AddForce(-joint.currentForce.normalized *
                    (f - o.m_MaxPullForce), ForceMode.Force);
        }

        bool UpdatePose(XRHand xrHand)
        {
            float t = 1f - Mathf.Exp(-o.m_Smoothing * Time.deltaTime);
            for (int i = 0; i < 4; i++)
            {
                var s = XRFingerShapeMath.CalculateFingerShape(
                    xrHand, k_Tracked[i], XRFingerShapeTypes.FullCurl);
                if (!s.TryGetFullCurl(out float c)) return false;
                curl[i] = curlInit ? Mathf.Lerp(curl[i], c, t) : c;
            }
            curlInit = true;

            float h = posedPrev ? o.m_Hysteresis : 0f;
            bool idx = curl[0] < o.m_IndexExtendedMax + h;
            bool pky = curl[3] < o.m_PinkyExtendedMax + h;
            int curled = (curl[1] > o.m_MiddleCurledMin - h ? 1 : 0)
                       + (curl[2] > o.m_RingCurledMin - h ? 1 : 0);
            return idx && pky && curled >= 1;
        }

        void Fire(XRHand xrHand)
        {
            if (!TryAim(xrHand, out var origin, out var dir)) return;
            dir = o.ApplyAimAssist(origin, dir);

            if (o.m_Thwip) o.m_Thwip.Play();

            if (Physics.Raycast(origin, dir, out var hit, o.m_Range, o.m_HitMask))
            {
                hit.collider.SendMessage("OnWebHit", hit,
                    SendMessageOptions.DontRequireReceiver);

                var body = hit.rigidbody;
                if (body != null && !body.isKinematic)
                {
                    hitBody = body;
                    attachLocal = body.transform.InverseTransformPoint(hit.point);

                    joint = wristBody.gameObject.AddComponent<SpringJoint>();
                    joint.autoConfigureConnectedAnchor = false;
                    joint.connectedBody = body;
                    joint.anchor = Vector3.zero;
                    joint.connectedAnchor = attachLocal;
                    joint.spring = o.m_PullSpring;
                    joint.damper = o.m_PullDamper;
                    joint.maxDistance = 0.05f;   // reel toward the hand
                    joint.minDistance = 0f;
                    attached = true;
                }
                else
                {
                    // stuck to static geometry: rope just anchors visually
                    hitBody = null;
                    attached = true;
                    attachLocal = hit.point; // store world point directly
                }
            }
        }

        void Release()
        {
            if (joint != null) Object.Destroy(joint);
            joint = null;
            hitBody = null;
            attached = false;
        }

        Vector3 CurrentAttachPoint()
        {
            if (!attached) return lastWristPos;
            if (hitBody != null)
                return hitBody.transform.TransformPoint(attachLocal);
            return attachLocal; // static hit stored as world point
        }

        void DrawRope(XRHand xrHand)
        {
            if (!attached) { rope.enabled = false; return; }

            Vector3 a = lastWristPos;
            Vector3 b = CurrentAttachPoint();

            // sag scales down as the rope approaches taut length
            float dist = Vector3.Distance(a, b);
            float sag = o.m_SagAmount * Mathf.Clamp01(dist / o.m_Range * 3f);

            rope.enabled = true;
            int n = o.m_RopeSegments;
            rope.positionCount = n;
            for (int i = 0; i < n; i++)
            {
                float u = i / (float)(n - 1);
                Vector3 p = Vector3.Lerp(a, b, u);
                p.y -= sag * Mathf.Sin(u * Mathf.PI); // parabolic droop
                rope.SetPosition(i, p);
            }
        }

        bool TryWrist(XRHand xrHand, out Vector3 wpos)
        {
            wpos = default;
            var wrist = xrHand.GetJoint(XRHandJointID.Wrist);
            if (!wrist.TryGetPose(out var w)) return false;
            wpos = o.ToWorldPos(w.position);
            return true;
        }

        bool TryAim(XRHand xrHand, out Vector3 origin, out Vector3 dir)
        {
            origin = default; dir = default;
            var wrist = xrHand.GetJoint(XRHandJointID.Wrist);
            var iM = xrHand.GetJoint(XRHandJointID.IndexMetacarpal);
            var iP = xrHand.GetJoint(XRHandJointID.IndexProximal);
            var lM = xrHand.GetJoint(XRHandJointID.LittleMetacarpal);
            var lP = xrHand.GetJoint(XRHandJointID.LittleProximal);
            if (!wrist.TryGetPose(out var w) ||
                !iM.TryGetPose(out var a) || !iP.TryGetPose(out var b) ||
                !lM.TryGetPose(out var c) || !lP.TryGetPose(out var d)) return false;
            Vector3 fwd = (b.position - a.position) + (d.position - c.position);
            origin = o.ToWorldPos(w.position);
            dir = o.ToWorldDir(fwd).normalized;
            return dir.sqrMagnitude > 0.0001f;
        }
    }

    Vector3 ApplyAimAssist(Vector3 origin, Vector3 dir)
    {
        if (m_TargetsRoot == null) return dir;
        float bestDot = Mathf.Cos(m_AssistAngle * Mathf.Deg2Rad);
        Vector3 best = dir;
        foreach (Transform t in m_TargetsRoot)
        {
            Vector3 to = (t.position - origin).normalized;
            float dot = Vector3.Dot(dir, to);
            if (dot > bestDot) { bestDot = dot; best = to; }
        }
        return best;
    }
}