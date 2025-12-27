using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class ReceivePosition : MonoBehaviour
{
    [Header("OSC")]
    public OSC osc; // Drag your OSC GameObject here

    [Header("Agent spawning")]
    [Tooltip("Prefab to spawn for each tracked position. Must have a NavMeshAgent (or one will be added).")]
    public GameObject agentPrefab;
    [Tooltip("Optional parent for spawned agents (keeps hierarchy tidy).")]
    public Transform agentParent;
    [Tooltip("Hard cap for safety.")]
    public int maxAgents = 64;

    [Header("Mapping 0..100 tracking space to Unity world")]
    public Vector2 xWorld = new Vector2(-5f, 5f);
    public Vector2 zWorld = new Vector2(-5f, 5f);

    [Header("Batching")]
    [Tooltip("How often we apply the latest batch of received /position messages (seconds).")]
    public float applyInterval = 0.10f;
    [Tooltip("If no messages for this long, the current batch is considered complete (seconds).")]
    public float batchTimeout = 0.12f;

    [Header("NavMesh")]
    [Tooltip("Max distance to snap targets onto the NavMesh.")]
    public float navmeshSampleRadius = 2.0f;
    [Tooltip("If true, newly spawned agents will Warp to their first target (avoids long initial paths).")]
    public bool warpOnSpawn = true;

    [Header("Debug")]
    [Tooltip("Enable verbose runtime logging for received positions, batching and assignments.")]
    public bool verboseDebug = true;

    // Spawned agents (index = position index in current batch)
    readonly List<NavMeshAgent> _agents = new List<NavMeshAgent>();

    // Incoming position buffer for the current batch (0..100 space)
    readonly List<Vector2> _batch = new List<Vector2>(16);
    float _lastReceiveTime;
    float _lastApplyTime;
    bool _registeredHandler = false;

    void OnEnable()
    {
        if (osc == null) return;

        if (!_registeredHandler)
        {
            osc.SetAddressHandler("/position", OnReceivePosition);
            _registeredHandler = true;
            if (verboseDebug) Debug.Log($"{nameof(ReceivePosition)} OnEnable - registered handler");
        }
    }

    void Start()
    {
        if (osc == null)
        {
            Debug.LogError($"{nameof(ReceivePosition)}: OSC reference is null.");
            enabled = false;
            return;
        }

        // Handler registration moved to OnEnable (keeps toggling safe)
        if (verboseDebug) Debug.Log($"{nameof(ReceivePosition)} Start - registered handler: {_registeredHandler}");

        // Reuse any already-spawned agents to avoid continuous duplicates when toggling script
        PopulateExistingAgents();

        if (verboseDebug) Debug.Log($"{nameof(ReceivePosition)} Start - existing agents reused: {_agents.Count}");

        _lastReceiveTime = Time.time;
        _lastApplyTime = Time.time;
    }

    void OnDisable()
    {
        if (_registeredHandler && osc != null)
        {
            osc.RemoveAddressHandler("/position", OnReceivePosition);
            _registeredHandler = false;
            if (verboseDebug) Debug.Log($"{nameof(ReceivePosition)} handler removed on disable");
        }
    }

    void OnDestroy()
    {
        if (_registeredHandler && osc != null)
        {
            osc.RemoveAddressHandler("/position", OnReceivePosition);
            _registeredHandler = false;
            if (verboseDebug) Debug.Log($"{nameof(ReceivePosition)} handler removed on destroy");
        }
    }

    void PopulateExistingAgents()
    {
        // If agentParent set, use its children
        if (agentParent != null)
        {
            var existingN = agentParent.GetComponentsInChildren<NavMeshAgent>();
            foreach (var n in existingN)
            {
                if (!_agents.Contains(n)) _agents.Add(n);
            }
            if (verboseDebug) Debug.Log($"{nameof(PopulateExistingAgents)} found {existingN.Length} children under agentParent, added {_agents.Count} agents");
            return;
        }

        // Otherwise look in the scene for objects named TrackedPerson_XX
        var all = FindObjectsByType<NavMeshAgent>(FindObjectsSortMode.None);
        foreach (var n in all)
        {
            if (n == null || n.gameObject == null) continue;
            if (n.gameObject.name != null && n.gameObject.name.StartsWith("TrackedPerson_"))
            {
                if (!_agents.Contains(n)) _agents.Add(n);
            }
        }
        if (verboseDebug) Debug.Log($"{nameof(PopulateExistingAgents)} scanned scene and added {_agents.Count} existing TrackedPerson_* agents");
    }

    void Update()
    {
        // Apply periodically, or when a batch seems "complete" (no new messages for a moment)
        bool dueByInterval = (Time.time - _lastApplyTime) >= Mathf.Max(0.01f, applyInterval);
        bool dueByTimeout = _batch.Count > 0 && (Time.time - _lastReceiveTime) >= Mathf.Max(0.01f, batchTimeout);

        if (dueByInterval || dueByTimeout)
        {
            ApplyBatch();
            _lastApplyTime = Time.time;
        }
    }

    void OnReceivePosition(OscMessage message)
    {
        // Supports:
        //  - /position x y
        //  - /position index x y   (optional future-proofing)
        if (message == null || message.values == null) return;

        if (verboseDebug)
        {
            // build printable values string
            var sb = new System.Text.StringBuilder();
            sb.Append("OSC recv: ").Append(message.address).Append(" vals=[");
            for (int i = 0; i < message.values.Count; i++)
            {
                sb.Append(message.values[i]);
                if (i < message.values.Count - 1) sb.Append(", ");
            }
            sb.Append("]");
            Debug.Log(sb.ToString());
        }

        int start = 0;

        // If 3+ args, treat first as index (ignored for now, but parsed safely)
        if (message.values.Count >= 3)
        {
            // int idx = message.GetInt(0); // reserved
            start = 1;
        }

        if (message.values.Count < start + 2) return;

        float x100 = Mathf.Clamp(message.GetFloat(start + 0), 0f, 100f);
        float y100 = Mathf.Clamp(message.GetFloat(start + 1), 0f, 100f);

        if (_batch.Count < maxAgents)
        {
            _batch.Add(new Vector2(x100, y100));
            if (verboseDebug) Debug.Log($"{nameof(OnReceivePosition)} added pos ({x100:F1},{y100:F1}) to batch (size now {_batch.Count})");
        }
        else
        {
            if (verboseDebug) Debug.LogWarning($"{nameof(OnReceivePosition)} batch full (maxAgents={maxAgents}) - dropping pos ({x100:F1},{y100:F1})");
        }

        _lastReceiveTime = Time.time;
    }

    void ApplyBatch()
    {
        if (_batch.Count == 0) return;

        if (verboseDebug) Debug.Log($"{nameof(ApplyBatch)} batch contains {_batch.Count} positions; current agents {_agents.Count}");

        // Respect maxAgents
        int desired = Mathf.Clamp(_batch.Count, 0, maxAgents);
        EnsureAgentCount(desired);

        if (verboseDebug) Debug.Log($"{nameof(ApplyBatch)} after EnsureAgentCount desired={desired}, agents={_agents.Count}");

        // Build world-space target list
        var targetsWorld = new List<Vector3>(_batch.Count);
        for (int i = 0; i < desired; i++)
        {
            var mapped = MapToWorld(_batch[i].x, _batch[i].y, (_agents.Count > i ? _agents[i].transform.position.y : 1f));
            targetsWorld.Add(mapped);
            if (verboseDebug) Debug.Log($"{nameof(ApplyBatch)} target[{i}] 0..100=({_batch[i].x:F1},{_batch[i].y:F1}) -> world=({mapped.x:F2},{mapped.y:F2},{mapped.z:F2})");
        }

        // Greedy nearest-neighbor assignment to reduce identity swapping
        var assignedTarget = new bool[targetsWorld.Count];
        for (int ai = 0; ai < _agents.Count && ai < targetsWorld.Count; ai++)
        {
            var a = _agents[ai];
            if (a == null) continue;

            // find nearest unassigned target
            int bestIdx = -1;
            float bestDist = float.MaxValue;
            Vector2 apos = new Vector2(a.transform.position.x, a.transform.position.z);
            for (int ti = 0; ti < targetsWorld.Count; ti++)
            {
                if (assignedTarget[ti]) continue;
                var t = targetsWorld[ti];
                float d = (apos - new Vector2(t.x, t.z)).sqrMagnitude;
                if (d < bestDist) { bestDist = d; bestIdx = ti; }
            }
            if (bestIdx >= 0)
            {
                assignedTarget[bestIdx] = true;
                SetAgentTarget(a, targetsWorld[bestIdx]);
                if (verboseDebug)
                    Debug.Log($"{nameof(ApplyBatch)} assign: agent='{a.gameObject.name}' -> targetIndex={bestIdx} world=({targetsWorld[bestIdx].x:F2},{targetsWorld[bestIdx].y:F2},{targetsWorld[bestIdx].z:F2}) dist={Mathf.Sqrt(bestDist):F2}");
            }
        }

        _batch.Clear();
    }

    Vector3 MapToWorld(float x100, float y100, float yWorld)
    {
        float x = Mathf.Lerp(xWorld.x, xWorld.y, x100 / 100f);
        float z = Mathf.Lerp(zWorld.x, zWorld.y, y100 / 100f);
        return new Vector3(x, yWorld, z);
    }

    void EnsureAgentCount(int desired)
    {
        desired = Mathf.Clamp(desired, 0, maxAgents);

        // Despawn extras
        for (int i = _agents.Count - 1; i >= desired; i--)
        {
            var a = _agents[i];
            if (verboseDebug) Debug.Log($"{nameof(EnsureAgentCount)} despawning agent '{(a!=null? a.gameObject.name : "null")}' at index {i}");
            _agents.RemoveAt(i);
            if (a != null) Destroy(a.gameObject);
        }

        // Spawn missing
        while (_agents.Count < desired)
        {
            if (agentPrefab == null)
            {
                Debug.LogError($"{nameof(ReceivePosition)}: agentPrefab is null.");
                break;
            }

            var go = Instantiate(agentPrefab, Vector3.zero, Quaternion.identity, agentParent);
            go.name = $"TrackedPerson_{_agents.Count + 1:D2}";
            if (verboseDebug) Debug.Log($"{nameof(EnsureAgentCount)} spawned '{go.name}'");

            var nav = go.GetComponent<NavMeshAgent>();
            if (nav == null) nav = go.AddComponent<NavMeshAgent>();

            _agents.Add(nav);

            // If we have a batch target for this spawn, warp it there to avoid long initial paths
            if (warpOnSpawn)
            {
                int idx = _agents.Count - 1;
                if (idx >= 0 && idx < _batch.Count)
                {
                    Vector3 t = MapToWorld(_batch[idx].x, _batch[idx].y, go.transform.position.y);
                    if (NavMesh.SamplePosition(t, out var hit, navmeshSampleRadius, NavMesh.AllAreas))
                        nav.Warp(hit.position);
                    else
                        nav.Warp(t);
                    if (verboseDebug) Debug.Log($"{nameof(EnsureAgentCount)} warped '{go.name}' to initial target idx={idx}");
                }
            }
        }
    }

    void SetAgentTarget(NavMeshAgent nav, Vector3 target)
    {
        if (NavMesh.SamplePosition(target, out var hit, navmeshSampleRadius, NavMesh.AllAreas))
            nav.SetDestination(hit.position);
        else
            nav.SetDestination(target);
    }
}
