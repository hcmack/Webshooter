using System.Collections;
using UnityEngine;

// Web target that holds still until hit. Scatters to a random spot on a ring
// in front of you instead of stacking at one point. Spawns kinematic so it does
// not drift or fall; physics turns on only when a web hits it.
//
// Put on a Cube (or any mesh) with a Collider. A Rigidbody is added automatically.
[RequireComponent(typeof(Collider))]
public class WebTargetScatter : MonoBehaviour
{
    [Header("Placement (ring in front of you)")]
    [SerializeField] float m_MinDistance = 1.5f;
    [SerializeField] float m_MaxDistance = 3.0f;
    [SerializeField] float m_ArcDegrees = 120f;   // spread left/right of forward
    [SerializeField] float m_MinHeight = -0.3f;   // relative to eye level
    [SerializeField] float m_MaxHeight = 0.6f;

    [Header("Hit reaction")]
    [SerializeField] Color m_HitColor = Color.red;
    [SerializeField] float m_FlashTime = 0.25f;
    [SerializeField] bool m_PhysicsOnHit = true;  // let it be knocked/pulled after hit
    [SerializeField] AudioSource m_HitSound;

    [Header("Respawn")]
    [SerializeField] bool m_RespawnAfterHit = true;
    [SerializeField] float m_RespawnDelay = 1.0f;

    Transform m_Head;
    Renderer m_Renderer;
    Color m_BaseColor;
    Rigidbody m_Body;
    bool m_Hit;

    void Start()
    {
        m_Renderer = GetComponentInChildren<Renderer>();
        if (m_Renderer) m_BaseColor = m_Renderer.material.color;

        m_Body = GetComponent<Rigidbody>();
        if (m_Body == null) m_Body = gameObject.AddComponent<Rigidbody>();
        m_Body.useGravity = false;
        m_Body.isKinematic = true;           // hold still, no drifting

        Place();
    }

    void Place()
    {
        if (m_Head == null)
            m_Head = Camera.main ? Camera.main.transform : null;

        // Fail safe: if no camera yet, retry next frame instead of spawning at origin
        if (m_Head == null) { Invoke(nameof(Place), 0.2f); return; }

        Vector3 fwd = m_Head.forward; fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
        fwd.Normalize();

        float angle = Random.Range(-m_ArcDegrees * 0.5f, m_ArcDegrees * 0.5f);
        Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * fwd;
        float dist = Random.Range(m_MinDistance, m_MaxDistance);
        float height = Random.Range(m_MinHeight, m_MaxHeight);

        Vector3 pos = m_Head.position + dir * dist;
        pos.y = m_Head.position.y + height;
        transform.position = pos;
        transform.rotation = Quaternion.LookRotation(-dir, Vector3.up);

        // reset to a held, unhit state
        m_Body.isKinematic = true;
        m_Body.useGravity = false;
        m_Hit = false;
        if (m_Renderer) m_Renderer.material.color = m_BaseColor;
    }

    public void OnWebHit(RaycastHit hit)
    {
        if (m_Hit) return;
        m_Hit = true;

        if (m_HitSound) m_HitSound.Play();

        if (m_PhysicsOnHit)
        {
            m_Body.isKinematic = false;   // now it can be knocked / pulled
            m_Body.useGravity = true;
        }

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
            Place();
        }
    }
}