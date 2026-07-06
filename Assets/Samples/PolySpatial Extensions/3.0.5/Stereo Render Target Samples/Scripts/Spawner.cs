using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Unity.PolySpatial.Samples
{
    public class Spawner : MonoBehaviour
    {
        public GameObject Prefab;
        public GameObject Parent;
        [Min(0)] public float SpawnRate = 1;
        [Min(0)] public int MaxInstanceCount = 64;

        readonly Queue<GameObject> m_Instances = new();

        void OnEnable()
        {
            InvokeRepeating(nameof(Spawn), 0.0f, SpawnRate);
        }

        void OnDisable()
        {
            CancelInvoke();
        }

        void Spawn()
        {
            var go = Instantiate(Prefab, transform.position, Quaternion.identity, Parent.transform);

            // add an impulse to launch it up into the air
            var force = 0.01f + (Random.value * 0.05f);
            var p = Random.insideUnitSphere;
            p.x = Math.Clamp(p.x, -0.7f, 0.7f);
            p.y = Math.Abs(p.y);
            p.z = Math.Clamp(p.z, -0.7f, 0.7f);

            p = p.normalized * force;

            var rb = go.GetComponent<Rigidbody>();
            rb.AddForce(p, ForceMode.Impulse);

            m_Instances.Enqueue(go);
            while (m_Instances.Count >= MaxInstanceCount)
            {
                Destroy(m_Instances.Dequeue());
            }
        }
    }
}
