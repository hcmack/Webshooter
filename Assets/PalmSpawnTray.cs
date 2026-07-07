using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Hands.Gestures;

// Palm-up wrist tray with throw-to-spawn. (Release-bug fixed version.)
//
// FIXES vs previous:
//  - Release now uses thumb-index DISTANCE (metres), which is far more reliable
//    than the pinch shape value and cannot get stuck "always pinching".
//  - The thrown mini is fully severed from tray tracking before the tray rebuilds,
//    so it can no longer snap back to the wrist.
//  - Palm normal axis is now an inspector field (set it from PalmDebugBars).
//  - Optional color feedback: mini tints cyan while grabbed, white on release.
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
    [Tooltip("Local axis that points OUT of the palm. Set from PalmDebugBars.")]
    [SerializeField] Vector3 m_PalmNormalAxis = Vector3.down;
    [SerializeField] float m_PalmUpDot = 0.5f;
    [Tooltip("0 = ignore head-facing. Raise to require palm toward your face.")]
    [SerializeField] float m_FaceUserDot = 0f;
    [SerializeField] float m_OpenHoldTime = 0.2f;
    [Range(1f, 30f)][SerializeField] float m_Smoothing = 12f;

    [Header("Tray layout (above the wrist)")]
    [SerializeField] float m_TrayHeight = 0.08f;
    [SerializeField] float m_TraySpacing = 0.06f;
    [SerializeField] float m_FollowSmooth = 15f;

    [Header("Pinch grab (other hand) - distance based")]
    [Tooltip("Thumb-index closer than this (metres) = pinching (grab).")]
    [SerializeField] float m_PinchCloseDist = 0.03f;
    [Tooltip("Thumb-index farther than this (metres) = released (throw).")]
    [SerializeField] float m_PinchOpenDist = 0.05f;
    [SerializeField] float m_GrabRadius = 0.07f;

    [Header("Throw")]
    [SerializeField] float m_ThrowScale = 1.4f;
    [Tooltip("Cap on throw speed (m/s). Stops fast throws tunneling through floor.")]
    [SerializeField] float m_MaxThrowSpeed = 6f;
    [SerializeField] LayerMask m_LandingMask = ~0;

    [Header("Debug feedback")]
    [SerializeField] bool m_TintWhileHeld = true;

    XRHandSubsystem m_Subsystem;
    static readonly List<XRHandSubsystem> s_Subsystems = new();

    Transform m_TrayRoot;
    readonly List<GameObject> m_Minis = new();
    readonly List<int> m_MiniEntry = new();

    float m_PalmUp;
    bool m_Open;
    float m_OpenTimer;

    GameObject m_Grabbed;
    int m_GrabbedEntry = -1;
    bool m_Pinching;
    Vector3 m_PrevPinchPos;
    Vector3 m_PinchVel;
    bool m_HasPrevPinch;

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

    // --- Palm-up ----------------------------------------------------------

    void UpdatePalmUp(XRHand hand)
    {
        if (!hand.isTracked) { if (m_Grabbed == null) SetOpen(false); return; }

        var palm = hand.GetJoint(XRHandJointID.Palm);
        if (!palm.TryGetPose(out var pose))
        {
            var wrist = hand.GetJoint(XRHandJointID.Wrist);
            if (!wrist.TryGetPose(out pose)) return;
        }

        Vector3 normal = ToWorldDir(pose.rotation * m_PalmNormalAxis).normalized;
        float up = Vector3.Dot(normal, Vector3.up);

        float face = 1f;
        if (m_FaceUserDot > 0f && Camera.main)
        {
            Vector3 toHead = (Camera.main.transform.position
                              - ToWorldPos(pose.position)).normalized;
            face = Vector3.Dot(normal, toHead);
        }

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
            SetKinematic(mini, true);
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
        m_TrayRoot.position = Vector3.Lerp(m_TrayRoot.position, pos + up * m_TrayHeight, t);
        m_TrayRoot.rotation = Quaternion.Slerp(m_TrayRoot.rotation, rot, t);
    }

    // --- Grab + throw (distance-based, cannot stick) ----------------------

    void UpdateGrab(XRHand hand)
    {
        if (!hand.isTracked)
        {
            if (m_Grabbed != null) ReleaseThrow(Vector3.zero);
            m_HasPrevPinch = false;
            return;
        }

        var thumb = hand.GetJoint(XRHandJointID.ThumbTip);
        var index = hand.GetJoint(XRHandJointID.IndexTip);
        if (!thumb.TryGetPose(out var tp) || !index.TryGetPose(out var ip))
        {
            m_HasPrevPinch = false;
            return;
        }

        Vector3 tpW = ToWorldPos(tp.position);
        Vector3 ipW = ToWorldPos(ip.position);
        Vector3 pinchPos = Vector3.Lerp(tpW, ipW, 0.5f);
        float gap = Vector3.Distance(tpW, ipW);

        if (m_HasPrevPinch && Time.deltaTime > 0f)
            m_PinchVel = (pinchPos - m_PrevPinchPos) / Time.deltaTime;
        m_PrevPinchPos = pinchPos;
        m_HasPrevPinch = true;

        // hysteresis on distance: close to grab, must open wider to release
        if (m_Pinching) { if (gap > m_PinchOpenDist) m_Pinching = false; }
        else            { if (gap < m_PinchCloseDist) m_Pinching = true; }

        if (m_Grabbed == null)
        {
            if (m_Pinching) TryGrab(pinchPos);
        }
        else
        {
            if (m_Pinching)
                m_Grabbed.transform.position = pinchPos;
            else
                ReleaseThrow(m_PinchVel * m_ThrowScale);
        }
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
        m_GrabbedEntry = m_MiniEntry[best];
        m_Minis[best] = null;   // remove from tray now so a rebuild can't touch it

        m_Grabbed.transform.SetParent(null, true);
        SetKinematic(m_Grabbed, true);
        if (m_TintWhileHeld) Tint(m_Grabbed, Color.cyan);
    }

    void ReleaseThrow(Vector3 velocity)
    {
        if (m_Grabbed == null) return;

        var thrown = m_Grabbed;
        m_Grabbed = null;                       // sever first
        if (m_TintWhileHeld) Tint(thrown, Color.white);

        SetKinematic(thrown, false);
        var body = thrown.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.useGravity = true;
            // cap speed so a hard throw can't tunnel through thin floor mesh
            if (velocity.magnitude > m_MaxThrowSpeed)
                velocity = velocity.normalized * m_MaxThrowSpeed;
            body.linearVelocity = velocity;
            // sweep the path between physics steps so it can't skip the floor
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }

        var lander = thrown.AddComponent<ThrownMiniLander>();
        lander.Init(m_GrabbedEntry >= 0 ? m_Entries[m_GrabbedEntry].fullPrefab : null,
                    m_LandingMask);
        m_GrabbedEntry = -1;

        if (m_Open) BuildTray();
    }

    static void Tint(GameObject go, Color c)
    {
        var r = go.GetComponentInChildren<Renderer>();
        if (r) r.material.color = c;
    }

    static void SetKinematic(GameObject go, bool kinematic)
    {
        var b = go.GetComponent<Rigidbody>();
        if (b == null) return;
        b.isKinematic = kinematic;
        if (kinematic) { b.linearVelocity = Vector3.zero; b.angularVelocity = Vector3.zero; }
    }
}

// Added to a thrown mini. Spawns the full prefab where it first lands.
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
            var full = Instantiate(m_FullPrefab, point + normal * 0.02f, rot);
            // make the spawned object aim-assistable even though it's runtime-made
            if (full.GetComponent<WebTargetTag>() == null)
                full.AddComponent<WebTargetTag>();
        }
        Destroy(gameObject);
    }
}