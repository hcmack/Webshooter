using System.Collections;
using UnityEngine;

// Teleport-in / materialize effect. Add to a prefab; it plays automatically when
// the object spawns (OnEnable). Works with ANY material out of the box:
//   - scales up from zero with a springy overshoot
//   - flashes emissive bright, then settles
//   - optional: drives a "_DissolveAmount" property if your material's shader has
//     one (Shader Graph dissolve), for a bottom-up materialize.
//
// No dependencies. Put it on the full-size object you spawn.
public class TeleportInEffect : MonoBehaviour
{
    [Header("Timing")]
    [SerializeField] float m_Duration = 0.6f;

    [Header("Scale pop")]
    [SerializeField] bool m_ScalePop = true;
    [Tooltip("Overshoot past final scale before settling (1 = none).")]
    [SerializeField] float m_Overshoot = 1.15f;

    [Header("Emissive flash")]
    [SerializeField] bool m_EmissiveFlash = true;
    [SerializeField] Color m_FlashColor = new(0.4f, 0.8f, 1f);
    [SerializeField] float m_FlashIntensity = 4f;

    [Header("Dissolve (optional, needs shader with _DissolveAmount)")]
    [Tooltip("Drives _DissolveAmount from 1 (hidden) to 0 (solid).")]
    [SerializeField] bool m_UseDissolve = false;
    [SerializeField] string m_DissolveProperty = "_DissolveAmount";

    [Header("Spawn burst (optional)")]
    [Tooltip("Particle system prefab to spawn at materialize (e.g. energy burst).")]
    [SerializeField] GameObject m_BurstPrefab;
    [SerializeField] AudioSource m_Sound;

    Vector3 m_FinalScale;
    Renderer[] m_Renderers;
    MaterialPropertyBlock m_Mpb;
    int m_EmissionID, m_DissolveID;

    void Awake()
    {
        m_FinalScale = transform.localScale;
        m_Renderers = GetComponentsInChildren<Renderer>();
        m_Mpb = new MaterialPropertyBlock();
        m_EmissionID = Shader.PropertyToID("_EmissionColor");
        m_DissolveID = Shader.PropertyToID(m_DissolveProperty);
    }

    void OnEnable()
    {
        StopAllCoroutines();
        StartCoroutine(Materialize());
    }

    IEnumerator Materialize()
    {
        if (m_Sound) m_Sound.Play();
        if (m_BurstPrefab)
            Instantiate(m_BurstPrefab, transform.position, transform.rotation);

        float t = 0f;
        while (t < m_Duration)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / m_Duration);

            if (m_ScalePop)
                transform.localScale = m_FinalScale * ScaleCurve(u);

            if (m_EmissiveFlash || m_UseDissolve)
            {
                foreach (var r in m_Renderers)
                {
                    if (r == null) continue;
                    r.GetPropertyBlock(m_Mpb);

                    if (m_EmissiveFlash)
                    {
                        // bright at start, fades to normal
                        float glow = Mathf.Lerp(m_FlashIntensity, 0f, u);
                        m_Mpb.SetColor(m_EmissionID,
                            m_FlashColor * glow);
                    }
                    if (m_UseDissolve)
                        m_Mpb.SetFloat(m_DissolveID, 1f - u); // 1 hidden -> 0 solid

                    r.SetPropertyBlock(m_Mpb);
                }
            }
            yield return null;
        }

        // settle to final state
        transform.localScale = m_FinalScale;
        foreach (var r in m_Renderers)
        {
            if (r == null) continue;
            r.GetPropertyBlock(m_Mpb);
            if (m_EmissiveFlash) m_Mpb.SetColor(m_EmissionID, Color.black);
            if (m_UseDissolve) m_Mpb.SetFloat(m_DissolveID, 0f);
            r.SetPropertyBlock(m_Mpb);
        }
    }

    // ease-out with an overshoot near the end (springy pop)
    float ScaleCurve(float u)
    {
        if (m_Overshoot <= 1f) return Mathf.SmoothStep(0f, 1f, u);
        // rise, overshoot, settle
        float peak = 0.7f;
        if (u < peak)
            return Mathf.Lerp(0f, m_Overshoot, Mathf.SmoothStep(0f, 1f, u / peak));
        return Mathf.Lerp(m_Overshoot, 1f,
            Mathf.SmoothStep(0f, 1f, (u - peak) / (1f - peak)));
    }
}