using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

// Bulletproof on-device palm debug. Uses ONLY primitive cubes (no TextMesh, no
// Shader.Find, no fonts), parented directly to the camera so it is always in
// view regardless of tags. If you don't see the row of cubes, the script isn't
// running or the camera reference is wrong -- not a rendering issue.
//
// LAYOUT: a row of 5 cubes pinned to the bottom of your view.
//   Cube 0 (status): GREEN = menu hand tracked, RED = not tracked.
//   Cubes 1-4 (meters): back / fwd / up / down palm-up dot. A cube grows/glows
//   as its value rises; the tallest bright one is your correct palm normal.
public class PalmDebug : MonoBehaviour
{
    [SerializeField] Handedness m_Hand = Handedness.Left;
    [SerializeField] Transform m_XROrigin;

    XRHandSubsystem m_Subsystem;
    static readonly List<XRHandSubsystem> s_Subsystems = new();

    Transform m_Root;
    Transform[] m_Bars = new Transform[4];
    Renderer[] m_BarR = new Renderer[4];
    Renderer m_StatusR;
    bool m_Built;

    void Build()
    {
        var cam = Camera.main;
        // parent to the camera if tagged; else to XR origin; else world.
        Transform parent = cam ? cam.transform : (m_XROrigin ? m_XROrigin : null);

        m_Root = new GameObject("PalmDebugBars").transform;
        m_Root.SetParent(parent, false);
        m_Root.localPosition = new Vector3(0f, -0.25f, 0.6f); // in front, low
        m_Root.localRotation = Quaternion.identity;

        m_StatusR = MakeCube(new Vector3(-0.20f, 0f, 0f)).GetComponent<Renderer>();

        for (int i = 0; i < 4; i++)
        {
            var c = MakeCube(new Vector3(-0.08f + i * 0.06f, 0f, 0f));
            m_Bars[i] = c.transform;
            m_BarR[i] = c.GetComponent<Renderer>();
        }
        m_Built = true;
    }

    GameObject MakeCube(Vector3 localPos)
    {
        var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
        c.transform.SetParent(m_Root, false);
        c.transform.localPosition = localPos;
        c.transform.localScale = new Vector3(0.04f, 0.04f, 0.04f);
        Destroy(c.GetComponent<Collider>());
        // reuse the primitive's default material (guaranteed to render);
        // just tint it, no Shader.Find.
        return c;
    }

    void Update()
    {
        if (!m_Built)
        {
            if (Camera.main == null && m_XROrigin == null) return; // wait for a parent
            Build();
        }

        if (m_Subsystem == null || !m_Subsystem.running)
        {
            SubsystemManager.GetSubsystems(s_Subsystems);
            if (s_Subsystems.Count > 0) m_Subsystem = s_Subsystems[0];
            if (m_Subsystem == null) { Status(Color.gray); return; }
        }

        var hand = m_Hand == Handedness.Right
            ? m_Subsystem.rightHand : m_Subsystem.leftHand;
        if (!hand.isTracked) { Status(Color.red); ZeroBars(); return; }
        Status(Color.green);

        var palm = hand.GetJoint(XRHandJointID.Palm);
        if (!palm.TryGetPose(out var pose))
        {
            var wrist = hand.GetJoint(XRHandJointID.Wrist);
            if (!wrist.TryGetPose(out pose)) return;
        }

        float[] d =
        {
            Vector3.Dot(Dir(pose, Vector3.back), Vector3.up),
            Vector3.Dot(Dir(pose, Vector3.forward), Vector3.up),
            Vector3.Dot(Dir(pose, Vector3.up), Vector3.up),
            Vector3.Dot(Dir(pose, Vector3.down), Vector3.up),
        };

        for (int i = 0; i < 4; i++)
        {
            float v = Mathf.Clamp01((d[i] + 1f) * 0.5f);     // -1..1 -> 0..1
            float h = Mathf.Lerp(0.02f, 0.12f, v);
            var s = m_Bars[i].localScale; s.y = h; m_Bars[i].localScale = s;
            // bright green when this direction is strongly palm-up
            m_BarR[i].material.color = d[i] > 0.5f
                ? Color.green
                : Color.Lerp(Color.blue, Color.white, v);
        }
    }

    void Status(Color c) { if (m_StatusR) m_StatusR.material.color = c; }
    void ZeroBars()
    {
        for (int i = 0; i < 4; i++)
            if (m_Bars[i]) { var s = m_Bars[i].localScale; s.y = 0.02f; m_Bars[i].localScale = s; }
    }

    Vector3 Dir(Pose pose, Vector3 local)
    {
        Vector3 w = pose.rotation * local;
        if (m_XROrigin) w = m_XROrigin.TransformDirection(w);
        return w.normalized;
    }
}