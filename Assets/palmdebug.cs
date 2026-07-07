using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

// ON-DEVICE palm-up debug (no editor needed). Shows a floating cube that turns
// GREEN when a palm-up direction is detected, plus 3D text with live values.
// Everything is drawn as world objects, so it renders in the headset.
//
// Attach to any GameObject, set the hand + XR Origin, build, look ahead.
public class PalmDebug : MonoBehaviour
{
    [SerializeField] Handedness m_Hand = Handedness.Left;
    [SerializeField] Transform m_XROrigin;
    [SerializeField] float m_PanelDistance = 0.6f; // meters in front of head

    XRHandSubsystem m_Subsystem;
    static readonly List<XRHandSubsystem> s_Subsystems = new();

    Transform m_Panel;
    Renderer m_Cube;
    TextMesh m_Text;
    Transform m_Head;

    void Start()
    {
        m_Head = Camera.main ? Camera.main.transform : null;

        // panel root floats in front of the head
        m_Panel = new GameObject("PalmDebugPanel").transform;

        // status cube
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.SetParent(m_Panel, false);
        cube.transform.localScale = Vector3.one * 0.08f;
        cube.transform.localPosition = new Vector3(-0.12f, 0f, 0f);
        Destroy(cube.GetComponent<Collider>());
        m_Cube = cube.GetComponent<Renderer>();
        m_Cube.material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));

        // 3D text
        var t = new GameObject("PalmDebugText");
        t.transform.SetParent(m_Panel, false);
        t.transform.localPosition = new Vector3(-0.05f, 0f, 0f);
        m_Text = t.AddComponent<TextMesh>();
        m_Text.characterSize = 0.01f;
        m_Text.fontSize = 90;
        m_Text.anchor = TextAnchor.MiddleLeft;
        m_Text.color = Color.white;
        m_Text.text = "waiting...";
    }

    void Update()
    {
        if (m_Head == null) m_Head = Camera.main ? Camera.main.transform : null;
        if (m_Head != null && m_Panel != null)
        {
            Vector3 fwd = m_Head.forward; fwd.y = 0f; fwd.Normalize();
            m_Panel.position = m_Head.position + fwd * m_PanelDistance - Vector3.up * 0.1f;
            m_Panel.rotation = Quaternion.LookRotation(fwd, Vector3.up);
        }

        if (m_Subsystem == null || !m_Subsystem.running)
        {
            SubsystemManager.GetSubsystems(s_Subsystems);
            if (s_Subsystems.Count > 0) m_Subsystem = s_Subsystems[0];
            if (m_Subsystem == null) { Set("no hand subsystem", Color.gray); return; }
        }

        var hand = m_Hand == Handedness.Right
            ? m_Subsystem.rightHand : m_Subsystem.leftHand;
        if (!hand.isTracked) { Set($"{m_Hand} hand: NOT TRACKED", Color.red); return; }

        var palm = hand.GetJoint(XRHandJointID.Palm);
        bool palmOk = palm.TryGetPose(out var pose);
        if (!palmOk)
        {
            var wrist = hand.GetJoint(XRHandJointID.Wrist);
            if (!wrist.TryGetPose(out pose)) { Set("no palm/wrist pose", Color.red); return; }
        }

        Vector3 up = Vector3.up;
        float back = Vector3.Dot(Dir(pose, Vector3.back), up);
        float fwd2 = Vector3.Dot(Dir(pose, Vector3.forward), up);
        float u = Vector3.Dot(Dir(pose, Vector3.up), up);
        float dn = Vector3.Dot(Dir(pose, Vector3.down), up);

        // best candidate = whichever is most positive right now
        float best = Mathf.Max(Mathf.Max(back, fwd2), Mathf.Max(u, dn));
        string which = best == back ? "BACK" : best == fwd2 ? "FWD" : best == u ? "UP" : "DOWN";

        Set($"palm:{(palmOk ? "OK" : "wrist")}\n" +
            $"back {back:+0.0;-0.0}  fwd {fwd2:+0.0;-0.0}\n" +
            $"up {u:+0.0;-0.0}  down {dn:+0.0;-0.0}\n" +
            $"best: {which} ({best:0.0})",
            best > 0.5f ? Color.green : Color.white);
    }

    void Set(string msg, Color c)
    {
        if (m_Text) { m_Text.text = msg; }
        if (m_Cube) m_Cube.material.color = c;
    }

    Vector3 Dir(Pose pose, Vector3 local)
    {
        Vector3 w = pose.rotation * local;
        if (m_XROrigin) w = m_XROrigin.TransformDirection(w);
        return w.normalized;
    }
}