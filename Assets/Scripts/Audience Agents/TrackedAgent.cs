// Assets/Scripts/TrackedAgent.cs
using UnityEngine;
using LivePositions;   // <-- This lets us access PositionSampler inside the LivePositions namespace

[DisallowMultipleComponent]
public class TrackedAgent : MonoBehaviour
{
    [Tooltip("Unique identifier. If empty, GameObject.name is used")]
    public string agentId;

    [Tooltip("Stable numeric id (e.g., 5 for AudienceMember_05). If > 0, PositionSampler will use this as uid.")]
    public int numericId;

    [Tooltip("Dot color on the mini-map")]
    public Color mapColor = Color.white;

    [Tooltip("Optional label for color/material (e.g., material name or HEX)")]
    public string colorTag;

    void Awake()
    {
        if (string.IsNullOrEmpty(agentId))
            agentId = gameObject.name;
    }

    void OnEnable()
    {
        if (PositionSampler.Instance != null)
            PositionSampler.Instance.RegisterAgent(this);
    }

    void OnDisable()
    {
        if (PositionSampler.Instance != null)
            PositionSampler.Instance.UnregisterAgent(this);
    }
}
