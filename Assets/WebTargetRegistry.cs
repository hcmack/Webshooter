using System.Collections.Generic;
using UnityEngine;

// Global registry of web targets. Any target adds itself here on enable and
// removes itself on disable/destroy. Aim assist queries this instead of walking
// the children of a fixed root, so RUNTIME-SPAWNED targets are included too.
//
// USAGE
//  - Put a WebTargetTag component on anything you want aim-assisted (or add the
//    two register/unregister calls to your existing target scripts).
//  - Point the shooter's aim assist at WebTargetRegistry.Targets (see note in
//    the shooter patch).
public static class WebTargetRegistry
{
    static readonly List<Transform> s_Targets = new();
    public static IReadOnlyList<Transform> Targets => s_Targets;

    public static void Register(Transform t)
    {
        if (t != null && !s_Targets.Contains(t)) s_Targets.Add(t);
    }

    public static void Unregister(Transform t)
    {
        s_Targets.Remove(t);
    }

    // Nearest target within a cone around (origin, dir). Returns null if none.
    public static Transform FindInCone(Vector3 origin, Vector3 dir, float coneDeg)
    {
        float bestDot = Mathf.Cos(coneDeg * Mathf.Deg2Rad);
        Transform best = null;
        for (int i = s_Targets.Count - 1; i >= 0; i--)
        {
            var t = s_Targets[i];
            if (t == null) { s_Targets.RemoveAt(i); continue; }  // clean up destroyed
            Vector3 to = (t.position - origin);
            if (to.sqrMagnitude < 1e-4f) continue;
            float d = Vector3.Dot(dir, to.normalized);
            if (d > bestDot) { bestDot = d; best = t; }
        }
        return best;
    }
}

// Drop this on any target (including prefabs you instantiate) to make it
// aim-assistable. No parenting needed.
public class WebTargetTag : MonoBehaviour
{
    void OnEnable()  => WebTargetRegistry.Register(transform);
    void OnDisable() => WebTargetRegistry.Unregister(transform);
}