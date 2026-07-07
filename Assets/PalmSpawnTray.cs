using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.Gestures;

// Palm-up wrist tray with throw-to-spawn.
//
// FLOW
//  1. Turn your menu-hand palm up  -> a row of 4 mini previews appears above the wrist.
//  2. Pinch a mini with the OTHER hand (thumb+index) -> you grab it.
//  3. Throw it. On release it flies with your hand's velocity.
//  4. Where it lands, the matching full-size prefab teleports in. The mini vanishes
//     and a fresh one returns to the tray.
//
// SETUP
//  - Put this on one GameObject. Fill the 4 Spawn Entries (mini prefab + full prefab).
//  - Mini prefabs: small, with a Collider + Rigidbody. Give them a URP material.
//  - Assign XR Origin. Pick which hand shows the tray (Menu Hand).
[System.Serializable]
public class SpawnEntry
{
    public string name;
    public GameObject miniPrefab;   // small preview, needs Collider + Rigidbody
    public GameObject fullPrefab;   // full-size object that teleports in on landing
}

public class PalmSpawnTray : MonoBehaviour
{
    [Header("Setup")]
    [SerializeField] Transform m_XROrigin;
    [SerializeField] Handedness m_MenuHand = Handedness.Left;
    [SerializeField] SpawnEntry[] m_Entries = new SpawnEntry[4];

    [Header("Palm-up detection")]
    [Tooltip("Palm normal must point this much toward world-up (dot).")]
    [SerializeField] float m_PalmUpDot = 0.5f;
    [Tooltip("Also require the palm to face the head this much (dot).")]
    [SerializeField] float m_FaceUserDot = 0.2f;
    [SerializeField] float m_OpenHoldTime = 0.2f;
    [Range(1f, 30f)][SerializeField] float m_Smoothing = 12f;

    [Header("Tray layout (above the wrist)")]
    [SerializeField] float m_TrayHeight = 0.08f;   // meters above wrist
    [SerializeField] float m_TraySpacing = 0.06f;  // gap between minis
    [SerializeField] float m_FollowSmooth = 15f;

    [Header("Pinch grab (other hand)")]
    [SerializeField] float m_PinchOn = 0.85f;
    [SerializeField] float m_PinchOff = 0.6f;
    [SerializeField] float m_GrabRadius = 0.06f;

    [Header("Throw")]
    [Tooltip("Multiplies measured hand velocity on release.")]
    [SerializeField] float m_ThrowScale = 1.2f;
    [SerializeField] LayerMask m_LandingMask = ~0;

    XRHandSubsystem m_Subsystem;
    static readonly List<XRHandSubsystem> s_Subsystems = new();

    Transform m_TrayRoot;
    readonly List<GameObject> m_Minis = new();
    readonly List<int> m_MiniEntry = new();   // which entry each slot holds

    float m_PalmUp;
    bool m_Open, m_OpenLatch;
    float m_OpenTimer;

    // grab state
    GameObject m_Grabbed;
    int m_GrabbedSlot = -1;
    bool m_PinchLatch;
    Vector3 m_PrevPinchPos;
    Vector3 m_PinchVel;

    void Awake()
    {
        var root = new GameObject("WristTray");
        root.transform.SetParent(transform, false);
        m_TrayRoot = root.transform;
    }

    void Update()
    {
        if (!EnsureSubsystem()) return;

        var menu = m_MenuHand == Handedness.Right
            ? m_Subsystem.rightHand : m_Subsystem.leftHand;
        var other = m_MenuHand == Handedness.Right
            ? m_Subsystem.leftHand : m_Subsystem.rightHand;

        UpdatePalmUp(menu);
        UpdateTrayTransform(menu);
        UpdateGrab(other);
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

    // --- Palm-up gesture --------------------------------------------------

    void UpdatePalmUp(XRHand hand)
    {
        if (!hand.isTracked) { SetOpen(false); return; }

        // palm normal from the palm joint (fallbacks to wrist if unavailable)
        var palm = hand.GetJoint(XRHandJointID.Palm);
        if (!palm.TryGetPose(out var pose))
        {
            var wrist = hand.GetJoint(XRHandJointID.Wrist);
            if (!wrist.TryGetPose(out pose)) return;
        }

        // palm faces along -up of the joint for most rigs; use joint up flipped
        Vector3 normal = ToWorldDir(pose.rotation * Vector3.back).normalized;
        float up = Vector3.Dot(normal, Vector3.up);

        Vector3 toHead = Camera.main
            ? (Camera.main.transform.position - ToWorldPos(pose.position)).normalized
            : Vector3.up;
        float face = Vector3.Dot(normal, toHead);

        float t = 1f - Mathf.Exp(-m_Smoothing * Time.deltaTime);
        m_PalmUp = Mathf.Lerp(m_PalmUp, up, t);

        bool wantOpen = m_PalmUp > m_PalmUpDot && face > m_FaceUserDot;

        if (wantOpen)
        {
            m_OpenTimer += Time.deltaTime;
            if (m_OpenTimer >= m_OpenHoldTime) SetOpen(true);
        }
        else
        {
            m_OpenTimer = 0f;
            // keep tray while a mini is grabbed even if palm drops
            if (m_Grabbed == null) SetOpen(false);
        }
    }

    void SetOpen(bool open)
    {
        if (open == m_Open) return;
        m_Open = open;
        if (open) BuildTray();
        else ClearTray();
    }

    // --- Tray -------------------------------------------------------------

    void BuildTray()
    {
        ClearTray();
        int count = Mathf.Min(4, m_Entries.Length);
        float startX = -(count - 1) * 0.5f * m_TraySpacing;

        for (int i = 0; i < count; i++)
        {
            var e = m_Entries[i];
            if (e == null || e.miniPrefab == null) { m_Minis.Add(null); m_MiniEntry.Add(-1); continue; }

            var mini = Instantiate(e.miniPrefab, m_TrayRoot);
            mini.transform.localPosition = new Vector3(startX + i * m_TraySpacing, 0f, 0f);
            SetKinematic(mini, true);       // rest quietly in the tray
            m_Minis.Add(mini);
            m_MiniEntry.Add(i);
        }
    }

    void ClearTray()
    {
        foreach (var m in m_Minis)
            if (m != null && m != m_Grabbed) Destroy(m);
        m_Minis.Clear();
        m_MiniEntry.Clear();
    }

    void UpdateTrayTransform(XRHand hand)
    {
        if (!m_Open || !hand.isTracked) return;
        var wrist = hand.GetJoint(XRHandJointID.Wrist);
        if (!wrist.TryGetPose(out var w)) return;

        Vector3 pos = ToWorldPos(w.position);
        Quaternion rot = Quaternion.LookRotation(
            ToWorldDir(w.rotation * Vector3.forward),
            ToWorldDir(w.rotation * Vector3.up));
        Vector3 up = rot * Vector3.up;

        float t = 1f - Mathf.Exp(-m_FollowSmooth * Time.deltaTime);
        m_TrayRoot.position = Vector3.Lerp(
            m_TrayRoot.position, pos + up * m_TrayHeight, t);
        m_TrayRoot.rotation = Quaternion.Slerp(m_TrayRoot.rotation, rot, t);
    }

    // --- Grab + throw -----------------------------------------------------

    void UpdateGrab(XRHand hand)
    {
        if (!hand.isTracked) return;

        // pinch point = midpoint of thumb tip and index tip
        if (!TryPinchPoint(hand, out Vector3 pinchPos, out float pinch)) return;

        // velocity of the pinch point (for throwing)
        if (Time.deltaTime > 0f)
            m_PinchVel = (pinchPos - m_PrevPinchPos) / Time.deltaTime;
        m_PrevPinchPos = pinchPos;

        bool pinching = m_PinchLatch ? pinch > m_PinchOff : pinch > m_PinchOn;
        m_PinchLatch = pinching;

        if (m_Grabbed == null)
        {
            if (pinching) TryGrab(pinchPos);
        }
        else
        {
            // hold: mini follows the pinch point
            m_Grabbed.transform.position = pinchPos;
            if (!pinching) ReleaseThrow();
        }
    }

    bool TryPinchPoint(XRHand hand, out Vector3 point, out float pinch)
    {
        point = default; pinch = 0f;
        var thumb = hand.GetJoint(XRHandJointID.ThumbTip);
        var index = hand.GetJoint(XRHandJointID.IndexTip);
        if (!thumb.TryGetPose(out var tp) || !index.TryGetPose(out var ip)) return false;
        point = ToWorldPos(Vector3.Lerp(tp.position, ip.position, 0.5f));

        var s = XRFingerShapeMath.CalculateFingerShape(
            hand, XRHandFingerID.Index, XRFingerShapeTypes.Pinch);
        s.TryGetPinch(out pinch);
        return true;
    }

    void TryGrab(Vector3 pinchPos)
    {
        int best = -1; float bestDist = m_GrabRadius;
        for (int i = 0; i < m_Minis.Count; i++)
        {
            if (m_Minis[i] == null) continue;
            float d = Vector3.Distance(pinchPos, m_Minis[i].transform.position);
            if (d < bestDist) { bestDist = d; best = i; }
        }
        if (best < 0) return;

        m_Grabbed = m_Minis[best];
        m_GrabbedSlot = best;
        m_Grabbed.transform.SetParent(null, true);   // detach from tray
        SetKinematic(m_Grabbed, true);               // move by transform while held
    }

    void ReleaseThrow()
    {
        if (m_Grabbed == null) return;

        int entryIdx = (m_GrabbedSlot >= 0 && m_GrabbedSlot < m_MiniEntry.Count)
            ? m_MiniEntry[m_GrabbedSlot] : -1;

        SetKinematic(m_Grabbed, false);
        var body = m_Grabbed.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.useGravity = true;
            body.linearVelocity = m_PinchVel * m_ThrowScale;
        }

        // attach the lander so the mini spawns the full object where it hits
        var lander = m_Grabbed.AddComponent<ThrownMiniLander>();
        lander.Init(entryIdx >= 0 ? m_Entries[entryIdx].fullPrefab : null, m_LandingMask);

        m_Grabbed = null;
        m_GrabbedSlot = -1;

        // regenerate the tray next time it opens (rebuild now so a fresh mini returns)
        if (m_Open) BuildTray();
    }

    static void SetKinematic(GameObject go, bool kinematic)
    {
        var b = go.GetComponent<Rigidbody>();
        if (b == null) return;
        b.isKinematic = kinematic;
        if (kinematic) { b.linearVelocity = Vector3.zero; b.angularVelocity = Vector3.zero; }
    }
}

// Added to a thrown mini. Waits for the first surface collision, then teleports
// the full-size prefab to that spot (oriented to the surface) and removes itself.
public class ThrownMiniLander : MonoBehaviour
{
    GameObject m_FullPrefab;
    LayerMask m_Mask;
    bool m_Spawned;
    float m_Life;

    public void Init(GameObject fullPrefab, LayerMask mask)
    {
        m_FullPrefab = fullPrefab;
        m_Mask = mask;
    }

    void Update()
    {
        // safety: if it never hits anything, spawn where it ends up after a while
        m_Life += Time.deltaTime;
        if (!m_Spawned && m_Life > 6f) Spawn(transform.position, Vector3.up);
    }

    void OnCollisionEnter(Collision c)
    {
        if (m_Spawned) return;
        if (((1 << c.gameObject.layer) & m_Mask) == 0) return;
        var contact = c.GetContact(0);
        Spawn(contact.point, contact.normal);
    }

    void Spawn(Vector3 point, Vector3 normal)
    {
        m_Spawned = true;
        if (m_FullPrefab != null)
        {
            Quaternion rot = Quaternion.FromToRotation(Vector3.up, normal);
            Instantiate(m_FullPrefab, point + normal * 0.02f, rot);
        }
        Destroy(gameObject);   // mini vanishes
    }
}