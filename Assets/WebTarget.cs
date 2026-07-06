using System.Collections;
using UnityEngine;

// Put this on a Cube (3D Object > Cube). It positions itself in front of you
// on launch and reacts when a web hits it. The WebShooter calls OnWebHit via
// SendMessage, so the method name must stay exactly "OnWebHit".
//
// Give the cube a URP material (URP/Lit or URP/Unlit) or it renders pink.
[RequireComponent(typeof(BoxCollider))]
public class WebTarget : MonoBehaviour
{
    [Header("Spawn placement (relative to your head)")]
    [SerializeField] float m_Distance = 2.0f;      // meters in front
    [SerializeField] float m_HeightOffset = 0f;    // + = higher than eye level
    [SerializeField] bool m_FaceUser = true;

    [Header("Hit reaction")]
    [SerializeField] Color m_HitColor = Color.red;
    [SerializeField] float m_FlashTime = 0.25f;
    [SerializeField] float m_Knockback = 2.0f;     // needs a Rigidbody to show
    [SerializeField] AudioSource m_HitSound;

    [Header("Respawn")]
    [SerializeField] bool m_RespawnAfterHit = true;
    [SerializeField] float m_RespawnDelay = 0.8f;

    Transform m_Head;
    Renderer m_Renderer;
    Color m_BaseColor;
    Rigidbody m_Body;
    bool m_Hit;

    void Start()
    {
        m_Head = Camera.main ? Camera.main.transform : null;
        m_Renderer = GetComponentInChildren<Renderer>();
        if (m_Renderer) m_BaseColor = m_Renderer.material.color;
        m_Body = GetComponent<Rigidbody>();
        PlaceInFront();
    }

    void PlaceInFront()
    {
        if (m_Head == null)
        {
            m_Head = Camera.main ? Camera.main.transform : null;
            if (m_Head == null) return;
        }

        // flatten forward so the box sits at a stable height, not tilted by gaze
        Vector3 fwd = m_Head.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
        fwd.Normalize();

        Vector3 pos = m_Head.position + fwd * m_Distance;
        pos.y = m_Head.position.y + m_HeightOffset;
        transform.position = pos;

        if (m_FaceUser)
            transform.rotation = Quaternion.LookRotation(-fwd, Vector3.up);

        if (m_Body)
        {
            m_Body.linearVelocity = Vector3.zero;
            m_Body.angularVelocity = Vector3.zero;
        }
        m_Hit = false;
        if (m_Renderer) m_Renderer.material.color = m_BaseColor;
    }

    // Called by WebShooter when a web raycast hits this object's collider.
    public void OnWebHit(RaycastHit hit)
    {
        if (m_Hit) return;
        m_Hit = true;

        if (m_HitSound) m_HitSound.Play();
        if (m_Body) m_Body.AddForceAtPosition(
            -hit.normal * m_Knockback, hit.point, ForceMode.Impulse);

        StopAllCoroutines();
        StartCoroutine(HitRoutine());
    }

    IEnumerator HitRoutine()
    {
        if (m_Renderer) m_Renderer.material.color = m_HitColor;
        yield return new WaitForSeconds(m_FlashTime);
        if (m_Renderer) m_Renderer.material.color = m_BaseColor;

        if (m_RespawnAfterHit)
        {
            yield return new WaitForSeconds(m_RespawnDelay);
            PlaceInFront();
        }
        else
        {
            m_Hit = false; // allow being hit again in place
        }
    }
}