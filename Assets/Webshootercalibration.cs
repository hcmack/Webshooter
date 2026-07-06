using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.Gestures;

// Runtime-calibrated web shooter. No editor testing needed.
//
// HOW IT WORKS
//  1. On launch, after m_StartupDelay, it enters calibration: hold your chosen
//     hand pose still for a moment and it records YOUR finger-shape values.
//  2. From then on, making that same pose matches (within tolerance) and fires.
//  3. Pinch your LEFT hand any time to re-calibrate to a new pose.
//
// Feedback is visual/audio so you can tell what's happening on device without
// a console: assign the indicator objects and audio clips below.
//
// Requires a LineRenderer on the same GameObject (the web strand).
[RequireComponent(typeof(LineRenderer))]
public class WebShooterCalibration : MonoBehaviour
{
    enum Cal { WaitingToStart, Holding, Capturing, Ready }
    enum FireState { Idle, Armed, Cooldown }

    [Header("Setup")]
    [SerializeField] Handedness m_ShootHand = Handedness.Right;
    [Tooltip("Your 'XR Origin (1)' transform. Required for correct world-space aim.")]
    [SerializeField] Transform m_XROrigin;

    [Header("Calibration")]
    [Tooltip("Seconds after launch before the first auto-calibration.")]
    [SerializeField] float m_StartupDelay = 3f;
    [Tooltip("Hold your pose this long before it starts recording.")]
    [SerializeField] float m_HoldTime = 1.5f;
    [Tooltip("Recording window. Values are averaged over this time.")]
    [SerializeField] float m_CaptureTime = 0.7f;
    [Tooltip("How close a live pose must be to the captured one (per finger, 0-1).")]
    [SerializeField] float m_MatchTolerance = 0.18f;
    [SerializeField] float m_Hysteresis = 0.08f;

    [Header("Fire")]
    [Tooltip("If true, fire on a wrist snap. If false, one web per pose.")]
    [SerializeField] bool m_RequireSnap = false;
    [SerializeField] float m_ArmHoldTime = 0.08f;
    [SerializeField] float m_SnapThreshold = 6f;   // rad/s
    [SerializeField] float m_Cooldown = 0.25f;
    [SerializeField] float m_Range = 15f;
    [SerializeField] LayerMask m_HitMask = ~0;

    [Header("Aim assist")]
    [SerializeField] float m_AssistAngle = 12f;
    [SerializeField] Transform m_TargetsRoot;

    [Header("Feedback (assign for on-device cues)")]
    [SerializeField] GameObject m_CalibratingIndicator; // on while holding/capturing
    [SerializeField] GameObject m_ReadyIndicator;       // blips on when calibrated
    [SerializeField] GameObject m_PalmGlow;             // on while pose matches
    [SerializeField] AudioSource m_ConfirmSound;        // plays on capture done
    [SerializeField] AudioSource m_Thwip;               // plays on fire
    [SerializeField] float m_StrandTime = 0.10f;

    static readonly XRHandFingerID[] k_Fingers =
    {
        XRHandFingerID.Thumb, XRHandFingerID.Index, XRHandFingerID.Middle,
        XRHandFingerID.Ring,  XRHandFingerID.Little
    };

    Cal m_Cal = Cal.WaitingToStart;
    FireState m_Fire = FireState.Idle;
    float m_CalTimer, m_FireTimer;
    readonly float[] m_Captured = new float[5];
    readonly float[] m_Accum = new float[5];
    int m_AccumCount;
    bool m_HasCapture;

    Quaternion m_PrevWrist;
    bool m_HasPrevWrist;
    bool m_LeftPinchLatch;
    LineRenderer m_Line;

    XRHandSubsystem m_Subsystem;
    static readonly List<XRHandSubsystem> s_Subsystems = new();

    void Awake()
    {
        m_Line = GetComponent<LineRenderer>();
        m_Line.positionCount = 2;
        m_Line.useWorldSpace = true;
        m_Line.enabled = false;
        ShowCalibrating(false);
        if (m_ReadyIndicator) m_ReadyIndicator.SetActive(false);
        if (m_PalmGlow) m_PalmGlow.SetActive(false);
        m_CalTimer = -m_StartupDelay; // negative = startup countdown
    }

    void Update()
    {
        if (!EnsureSubsystem()) return;

        var shoot = m_ShootHand == Handedness.Right
            ? m_Subsystem.rightHand : m_Subsystem.leftHand;
        var other = m_ShootHand == Handedness.Right
            ? m_Subsystem.leftHand : m_Subsystem.rightHand;

        // left-hand (other hand) pinch re-triggers calibration
        if (DetectOtherPinch(other)) BeginCalibration();

        if (m_Cal != Cal.Ready) { RunCalibration(shoot); return; }
        RunFiring(shoot);
    }

    // --- Calibration ------------------------------------------------------

    void BeginCalibration()
    {
        m_Cal = Cal.Holding;
        m_CalTimer = 0f;
        m_AccumCount = 0;
        for (int i = 0; i < 5; i++) m_Accum[i] = 0f;
        ShowCalibrating(true);
        if (m_ReadyIndicator) m_ReadyIndicator.SetActive(false);
    }

    void RunCalibration(XRHand hand)
    {
        // startup countdown before the very first calibration
        if (m_Cal == Cal.WaitingToStart)
        {
            m_CalTimer += Time.deltaTime;
            if (m_CalTimer >= 0f) BeginCalibration();
            return;
        }

        if (!hand.isTracked) return; // pause timers until the hand is visible

        m_CalTimer += Time.deltaTime;

        if (m_Cal == Cal.Holding)
        {
            if (m_CalTimer >= m_HoldTime)
            {
                m_Cal = Cal.Capturing;
                m_CalTimer = 0f;
            }
            return;
        }

        if (m_Cal == Cal.Capturing)
        {
            for (int i = 0; i < 5; i++)
                if (FullCurl(hand, k_Fingers[i], out var c)) m_Accum[i] += c;
            m_AccumCount++;

            if (m_CalTimer >= m_CaptureTime && m_AccumCount > 0)
            {
                for (int i = 0; i < 5; i++)
                    m_Captured[i] = m_Accum[i] / m_AccumCount;
                m_HasCapture = true;
                m_Cal = Cal.Ready;
                ShowCalibrating(false);
                if (m_ReadyIndicator) StartCoroutine(Blip(m_ReadyIndicator, 0.6f));
                if (m_ConfirmSound) m_ConfirmSound.Play();
            }
        }
    }

    // --- Firing -----------------------------------------------------------

    void RunFiring(XRHand hand)
    {
        if (!hand.isTracked) { SetFire(FireState.Idle); return; }

        bool matched = MatchesCaptured(hand);
        if (m_PalmGlow) m_PalmGlow.SetActive(matched && m_Fire != FireState.Cooldown);

        float snap = WristSnapSpeed(hand);

        switch (m_Fire)
        {
            case FireState.Idle:
                if (matched)
                {
                    m_FireTimer += Time.deltaTime;
                    if (m_FireTimer >= m_ArmHoldTime)
                    {
                        if (m_RequireSnap) SetFire(FireState.Armed);
                        else { Fire(hand); SetFire(FireState.Cooldown); }
                    }
                }
                else m_FireTimer = 0f;
                break;

            case FireState.Armed:
                if (!matched) { SetFire(FireState.Idle); break; }
                if (snap >= m_SnapThreshold) { Fire(hand); SetFire(FireState.Cooldown); }
                break;

            case FireState.Cooldown:
                m_FireTimer += Time.deltaTime;
                // require pose release (and cooldown) before next shot
                if (m_FireTimer >= m_Cooldown && !matched) SetFire(FireState.Idle);
                break;
        }
    }

    void SetFire(FireState f) { m_Fire = f; m_FireTimer = 0f; }

    bool MatchesCaptured(XRHand hand)
    {
        if (!m_HasCapture) return false;
        float tol = m_Fire == FireState.Idle ? m_MatchTolerance
                                        : m_MatchTolerance + m_Hysteresis;
        for (int i = 0; i < 5; i++)
        {
            if (!FullCurl(hand, k_Fingers[i], out var live)) return false;
            if (Mathf.Abs(live - m_Captured[i]) > tol) return false;
        }
        return true;
    }

    // --- Helpers ----------------------------------------------------------

    bool EnsureSubsystem()
    {
        if (m_Subsystem != null && m_Subsystem.running) return true;
        SubsystemManager.GetSubsystems(s_Subsystems);
        if (s_Subsystems.Count > 0) m_Subsystem = s_Subsystems[0];
        return m_Subsystem != null;
    }

    bool FullCurl(XRHand hand, XRHandFingerID f, out float c)
    {
        var s = XRFingerShapeMath.CalculateFingerShape(hand, f, XRFingerShapeTypes.FullCurl);
        return s.TryGetFullCurl(out c);
    }

    bool DetectOtherPinch(XRHand hand)
    {
        if (!hand.isTracked) { m_LeftPinchLatch = false; return false; }
        var s = XRFingerShapeMath.CalculateFingerShape(hand, XRHandFingerID.Index, XRFingerShapeTypes.Pinch);
        bool pinching = s.TryGetPinch(out var p) && p > 0.85f;
        bool fired = pinching && !m_LeftPinchLatch;   // rising edge only
        m_LeftPinchLatch = pinching;
        return fired;
    }

    float WristSnapSpeed(XRHand hand)
    {
        var wrist = hand.GetJoint(XRHandJointID.Wrist);
        if (!wrist.TryGetPose(out var pose)) { m_HasPrevWrist = false; return 0f; }
        float speed = 0f;
        if (m_HasPrevWrist && Time.deltaTime > 0f)
        {
            var d = pose.rotation * Quaternion.Inverse(m_PrevWrist);
            d.ToAngleAxis(out float a, out _);
            if (a > 180f) a -= 360f;
            speed = Mathf.Abs(a) * Mathf.Deg2Rad / Time.deltaTime;
        }
        m_PrevWrist = pose.rotation;
        m_HasPrevWrist = true;
        return speed;
    }

    Vector3 ToWorldPos(Vector3 p) => m_XROrigin ? m_XROrigin.TransformPoint(p) : p;
    Vector3 ToWorldDir(Vector3 d) => m_XROrigin ? m_XROrigin.TransformDirection(d) : d;

    bool TryAim(XRHand hand, out Vector3 origin, out Vector3 dir)
    {
        origin = default; dir = default;
        var wrist = hand.GetJoint(XRHandJointID.Wrist);
        var iM = hand.GetJoint(XRHandJointID.IndexMetacarpal);
        var iP = hand.GetJoint(XRHandJointID.IndexProximal);
        var lM = hand.GetJoint(XRHandJointID.LittleMetacarpal);
        var lP = hand.GetJoint(XRHandJointID.LittleProximal);
        if (!wrist.TryGetPose(out var w) ||
            !iM.TryGetPose(out var a) || !iP.TryGetPose(out var b) ||
            !lM.TryGetPose(out var c) || !lP.TryGetPose(out var d)) return false;
        Vector3 fwd = (b.position - a.position) + (d.position - c.position);
        origin = ToWorldPos(w.position);
        dir = ToWorldDir(fwd).normalized;
        return dir.sqrMagnitude > 0.0001f;
    }

    void Fire(XRHand hand)
    {
        if (!TryAim(hand, out var origin, out var dir)) return;
        dir = ApplyAimAssist(origin, dir);
        Vector3 end = origin + dir * m_Range;
        if (Physics.Raycast(origin, dir, out var hit, m_Range, m_HitMask))
        {
            end = hit.point;
            hit.collider.SendMessage("OnWebHit", hit, SendMessageOptions.DontRequireReceiver);
        }
        if (m_Thwip) m_Thwip.Play();
        StopAllCoroutines();
        StartCoroutine(DrawStrand(origin, end));
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

    IEnumerator DrawStrand(Vector3 from, Vector3 to)
    {
        m_Line.enabled = true;
        m_Line.SetPosition(0, from);
        float t = 0f;
        while (t < m_StrandTime)
        {
            t += Time.deltaTime;
            m_Line.SetPosition(1, Vector3.Lerp(from, to, t / m_StrandTime));
            yield return null;
        }
        m_Line.SetPosition(1, to);
        yield return new WaitForSeconds(0.12f);
        m_Line.enabled = false;
    }

    void ShowCalibrating(bool on)
    {
        if (m_CalibratingIndicator) m_CalibratingIndicator.SetActive(on);
    }

    IEnumerator Blip(GameObject go, float seconds)
    {
        go.SetActive(true);
        yield return new WaitForSeconds(seconds);
        go.SetActive(false);
    }
}