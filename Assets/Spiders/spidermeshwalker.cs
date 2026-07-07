using UnityEngine;

// Surface-walking bot for a LIVE ARKit scene mesh. Test with a cube.
//
// It drops onto the mesh, clings to whatever surface is under it, aligns its
// up-vector to the surface normal, wanders, and climbs from floor onto walls.
//
// REQUIREMENTS
//  - Scene mesh must have colliders (you confirmed this: the thrown cube lands).
//  - Put the scene mesh on a layer and set m_SurfaceMask to it. If you leave it
//    as Everything, put the bot on its OWN layer and REMOVE that layer from the
//    mask (see m_SurfaceMask note) so it doesn't detect itself.
//
// SETUP
//  - Cube with a Collider. Rigidbody added automatically.
//  - Set m_BotRadius to about half your cube's size.
//  - Give it a URP material.
[RequireComponent(typeof(Collider))]
public class spidermeshwalker : MonoBehaviour
{
    [Header("Surface layers")]
    [Tooltip("Layers the scene mesh is on. Must NOT include the bot's own layer.")]
    [SerializeField] LayerMask m_SurfaceMask = ~0;

    [Header("Body")]
    [Tooltip("About half the cube's size. Bot rides this far off the surface.")]
    [SerializeField] float m_BotRadius = 0.05f;
    [Tooltip("How far it probes toward the surface each step.")]
    [SerializeField] float m_ProbeDistance = 0.6f;

    [Header("Movement")]
    [SerializeField] float m_Speed = 0.3f;
    [SerializeField] float m_AlignSpeed = 10f;    // how fast up-vector follows the surface
    [SerializeField] float m_TurnSpeed = 3f;      // how fast it yaws toward heading
    [Tooltip("How far ahead it looks for a wall to climb.")]
    [SerializeField] float m_LookAhead = 0.15f;

    [Header("Wander")]
    [SerializeField] Vector2 m_TurnInterval = new(1.5f, 4f);
    [SerializeField] float m_WanderAngle = 70f;

    [Header("Startup")]
    [Tooltip("On start, search downward this far to find the floor mesh.")]
    [SerializeField] float m_DropSearch = 3f;

    Rigidbody m_Body;
    Vector3 m_Normal = Vector3.up;   // current surface up
    Vector3 m_Heading = Vector3.forward;
    float m_NextTurn;
    bool m_Placed;
    float m_RetryAt;

    void Awake()
    {
        m_Body = GetComponent<Rigidbody>();
        if (m_Body == null) m_Body = gameObject.AddComponent<Rigidbody>();
        m_Body.useGravity = false;
        m_Body.isKinematic = true;
        m_Body.interpolation = RigidbodyInterpolation.Interpolate;
    }

    void OnEnable()  { WebTargetRegistry.Register(transform); }
    void OnDisable() { WebTargetRegistry.Unregister(transform); }

    void FixedUpdate()
    {
        if (!m_Placed) { TryPlaceOnMesh(); return; }

        // 1) find the surface under the bot (probe along -normal)
        if (!Probe(transform.position, -m_Normal, m_ProbeDistance,
                out Vector3 hitP, out Vector3 hitN))
        {
            // lost it (walked off an edge): search around to re-cling
            if (!Reacquire(out hitP, out hitN))
                return; // nothing nearby; just hold position this frame
        }

        // 2) align up to the surface normal (smoothed)
        m_Normal = Vector3.Slerp(m_Normal, hitN, m_AlignSpeed * Time.fixedDeltaTime);

        // 3) keep heading tangent to the surface
        m_Heading = Vector3.ProjectOnPlane(m_Heading, m_Normal);
        if (m_Heading.sqrMagnitude < 1e-4f)
            m_Heading = Vector3.ProjectOnPlane(transform.forward, m_Normal);
        m_Heading.Normalize();

        // 4) wall ahead? adopt its normal to climb onto it
        if (Probe(transform.position, m_Heading, m_LookAhead,
                out _, out Vector3 wallN))
        {
            m_Normal = Vector3.Slerp(m_Normal, wallN, m_AlignSpeed * Time.fixedDeltaTime);
            m_Heading = Vector3.ProjectOnPlane(m_Heading, m_Normal).normalized;
        }

        // 5) wander: rotate heading around the surface normal now and then
        if (Time.time >= m_NextTurn)
        {
            float a = Random.Range(-m_WanderAngle, m_WanderAngle);
            m_Heading = (Quaternion.AngleAxis(a, m_Normal) * m_Heading).normalized;
            ScheduleTurn();
        }

        // 6) stick to the surface at ride height, and step forward
        Vector3 stick = hitP + m_Normal * m_BotRadius;
        Vector3 step = m_Heading * (m_Speed * Time.fixedDeltaTime);
        m_Body.MovePosition(Vector3.Lerp(m_Body.position, stick, 0.4f) + step);

        // 7) orient: up = surface normal, forward = heading
        Quaternion look = Quaternion.LookRotation(m_Heading, m_Normal);
        m_Body.MoveRotation(Quaternion.Slerp(m_Body.rotation, look,
            m_TurnSpeed * Time.fixedDeltaTime));
    }

    // find the floor beneath the bot on startup, then start walking
    void TryPlaceOnMesh()
    {
        if (Time.time < m_RetryAt) return;

        if (Probe(transform.position + Vector3.up * 0.2f, Vector3.down,
                m_DropSearch, out Vector3 p, out Vector3 n))
        {
            transform.position = p + n * m_BotRadius;
            m_Normal = n;
            m_Heading = Vector3.ProjectOnPlane(transform.forward, n).normalized;
            if (m_Heading.sqrMagnitude < 1e-4f) m_Heading = Vector3.forward;
            m_Placed = true;
            ScheduleTurn();
        }
        else
        {
            // mesh may not be generated under the bot yet; retry shortly
            m_RetryAt = Time.time + 0.5f;
        }
    }

    bool Probe(Vector3 origin, Vector3 dir, float dist,
        out Vector3 point, out Vector3 normal)
    {
        // start slightly back along the ray so we don't start inside geometry
        Vector3 o = origin - dir.normalized * (m_BotRadius * 0.5f);
        if (Physics.Raycast(o, dir.normalized, out var hit,
                dist + m_BotRadius, m_SurfaceMask, QueryTriggerInteraction.Ignore))
        {
            point = hit.point; normal = hit.normal; return true;
        }
        point = default; normal = default; return false;
    }

    bool Reacquire(out Vector3 point, out Vector3 normal)
    {
        Vector3[] dirs =
        {
            -m_Normal, Vector3.down, m_Heading, -m_Heading,
            Vector3.Cross(m_Heading, m_Normal), -Vector3.Cross(m_Heading, m_Normal)
        };
        foreach (var d in dirs)
            if (Probe(transform.position, d, m_ProbeDistance * 1.5f, out point, out normal))
                return true;
        point = default; normal = default; return false;
    }

    void ScheduleTurn()
    {
        m_NextTurn = Time.time + Random.Range(m_TurnInterval.x, m_TurnInterval.y);
    }

    // Optional: web hit knocks it loose briefly, then it re-clings.
    public void OnWebHit(RaycastHit hit)
    {
        m_Body.isKinematic = false;
        m_Body.useGravity = true;
        m_Body.AddForceAtPosition(-hit.normal * 3f, hit.point, ForceMode.Impulse);
        CancelInvoke(nameof(Recling));
        Invoke(nameof(Recling), 1.5f);
    }

    void Recling()
    {
        m_Body.isKinematic = true;
        m_Body.useGravity = false;
        m_Placed = false;        // re-drop onto whatever surface it landed near
        m_RetryAt = 0f;
    }
}