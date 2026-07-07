using System.Collections;
using UnityEngine;

// Minimal web target. No spawning, no placement, no respawn.
// It sits wherever you put it in the scene, and when a web hits it, it launches
// from the impact and (optionally) flashes.
//
// SETUP
//  - Put on any object with a Collider.
//  - A Rigidbody is added automatically (needed to be launched).
//  - Give it a URP material so it isn't pink.
//
// The WebShooter calls OnWebHit via SendMessage, so keep that method name.
[RequireComponent(typeof(Collider))]
public class WebTarget : MonoBehaviour
{
    [Header("Launch")]
    [Tooltip("Impulse strength applied along the web's impact direction.")]
    [SerializeField] float m_LaunchForce = 6f;
    [Tooltip("Extra upward kick so hits pop the object up a little.")]
    [SerializeField] float m_UpwardKick = 2f;
    [Tooltip("Random spin added on hit.")]
    [SerializeField] float m_Spin = 3f;

    [Header("Flash (optional)")]
    [SerializeField] bool m_Flash = true;
    [SerializeField] Color m_HitColor = Color.red;
    [SerializeField] float m_FlashTime = 0.2f;

    [Header("Sound (optional)")]
    [SerializeField] AudioSource m_HitSound;

    Rigidbody m_Body;
    Renderer m_Renderer;
    Color m_BaseColor;

    void Awake()
    {
        m_Body = GetComponent<Rigidbody>();
        if (m_Body == null) m_Body = gameObject.AddComponent<Rigidbody>();

        m_Renderer = GetComponentInChildren<Renderer>();
        if (m_Renderer) m_BaseColor = m_Renderer.material.color;
    }

    // Called by the web shooter when a web raycast hits this object's collider.
    public void OnWebHit(RaycastHit hit)
    {
        // launch: push along the web's travel direction (into the object),
        // i.e. opposite the surface normal, plus an upward pop.
        Vector3 dir = -hit.normal * m_LaunchForce + Vector3.up * m_UpwardKick;
        m_Body.AddForceAtPosition(dir, hit.point, ForceMode.Impulse);

        if (m_Spin > 0f)
            m_Body.AddTorque(Random.insideUnitSphere * m_Spin, ForceMode.Impulse);

        if (m_HitSound) m_HitSound.Play();

        if (m_Flash && m_Renderer)
        {
            StopAllCoroutines();
            StartCoroutine(FlashRoutine());
        }
    }

    IEnumerator FlashRoutine()
    {
        m_Renderer.material.color = m_HitColor;
        yield return new WaitForSeconds(m_FlashTime);
        m_Renderer.material.color = m_BaseColor;
    }
}