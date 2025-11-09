using UnityEngine;

public class DotSpawner : MonoBehaviour
{
    public GameObject agentPrefab;     // Drag the Body+Head prefab here
    public int agentCount = 20;
    public float spawnArea = 10f;

    [Header("Identity")]
    [Tooltip("Prefix for generated agent IDs and names")] 
    public string idPrefix = "AudienceMember";
    [Tooltip("Start index for generated IDs (inclusive)")] 
    public int startIndex = 1;

    [Header("Materials")]
    public Material[] possibleMaterials;  // Assign a list of materials
    [Tooltip("If true, assigns materials without repeats until the list is exhausted")] 
    public bool uniqueMaterialPerAgent = true;
    [Tooltip("If true, randomizes the material order once at Start before assignment")] 
    public bool shuffleMaterialOrder = true;

    [Header("Auto Colors")]
    [Tooltip("If true and no materials are provided, assigns a unique per-agent color")] 
    public bool autoUniqueColors = true;
    [Tooltip("Saturation for auto-generated colors")] 
    [Range(0f,1f)] public float autoSat = 0.7f;
    [Tooltip("Value/Brightness for auto-generated colors")] 
    [Range(0f,1f)] public float autoVal = 0.95f;

    [Tooltip("When using materials, override TrackedAgent.mapColor with a unique palette color for the mini-map/logs")] 
    public bool enforceUniqueMapColors = true;

    [Header("Height Settings")]
    [Tooltip("If enabled, raycasts to find the ground and spawns slightly above it.")]
    public bool snapToGround = true;

    [Tooltip("Each object will spawn at least at this height (fallback if ground is not found).")]
    public float minSpawnY = 1f;

    [Tooltip("How high above the spawn XZ to start the downward raycast.")]
    public float raycastStartHeight = 100f;

    [Tooltip("How far above the ground to place the object.")]
    public float heightAboveGround = 0.5f;

    [Tooltip("Layers that count as ground (default: all).")]
    public LayerMask groundLayers = ~0; // default: all layers

    void Start()
    {
        // Prepare a material pool (optional)
        Material[] matPool = null;
        if (possibleMaterials != null && possibleMaterials.Length > 0)
        {
            matPool = (Material[])possibleMaterials.Clone();
            if (shuffleMaterialOrder && matPool.Length > 1)
            {
                // Fisher–Yates shuffle using UnityEngine.Random
                for (int n = matPool.Length - 1; n > 0; n--)
                {
                    int k = Random.Range(0, n + 1);
                    var tmp = matPool[n];
                    matPool[n] = matPool[k];
                    matPool[k] = tmp;
                }
            }
        }

        for (int i = 0; i < agentCount; i++)
        {
            int idx = startIndex + i;
            string newId = $"{idPrefix}_{idx:D2}";

            Vector3 pos = new Vector3(
                Random.Range(-spawnArea, spawnArea),
                0f, // temporary value, will be computed below
                Random.Range(-spawnArea, spawnArea)
            );

            // Compute a safe height
            if (snapToGround)
            {
                // Raycast from above downwards to find the ground
                Vector3 rayOrigin = new Vector3(pos.x, raycastStartHeight, pos.z);
                if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, Mathf.Infinity, groundLayers, QueryTriggerInteraction.Ignore))
                {
                    pos.y = hit.point.y + heightAboveGround;
                }
                else
                {
                    // If no ground is found, use at least minSpawnY
                    pos.y = Mathf.Max(minSpawnY, 0f);
                }
            }
            else
            {
                // Without ground snap, just ensure a minimum height
                pos.y = Mathf.Max(minSpawnY, 0f);
            }

            GameObject agent = Instantiate(agentPrefab, pos, Quaternion.identity);
            agent.name = newId; // rename in hierarchy for clarity

            // Ensure a TrackedAgent exists now so subsequent color/material logic can write into it.
            var tracked = agent.GetComponent<TrackedAgent>();
            if (tracked == null) tracked = agent.AddComponent<TrackedAgent>();
            tracked.agentId = newId;
            tracked.numericId = idx; // keep uid aligned with the suffix

            // If materials are provided → assign one
            if (matPool != null && matPool.Length > 0)
            {
                Material chosenMat = null;
                if (uniqueMaterialPerAgent)
                {
                    // No repeats until we run out; if more agents than materials, wrap around
                    int pickIndex = i < matPool.Length ? i : (i % matPool.Length);
                    chosenMat = matPool[pickIndex];
                }
                else
                {
                    chosenMat = matPool[Random.Range(0, matPool.Length)];
                }

                // Apply the material to all MeshRenderers in the prefab
                MeshRenderer[] renderers = agent.GetComponentsInChildren<MeshRenderer>();
                foreach (MeshRenderer rend in renderers)
                {
                    if (chosenMat != null) rend.material = chosenMat;
                }

                // If the spawned object is tracked, sync its mini-map color from material if possible
                var trackedFromMat = tracked;
                if (trackedFromMat != null && chosenMat != null)
                {
                    // Try to read a reasonable color from the material for reference
                    try
                    {
                        if (chosenMat.HasProperty("_BaseColor"))
                            trackedFromMat.mapColor = chosenMat.GetColor("_BaseColor");
                        else if (chosenMat.HasProperty("_Color"))
                            trackedFromMat.mapColor = chosenMat.GetColor("_Color");
                        // else leave mapColor as previously set
                    }
                    catch { /* ignore if shader has different properties */ }
                }

                // Set a descriptive color tag for later CSV analysis
                if (trackedFromMat != null)
                {
                    // Label with material name
                    trackedFromMat.colorTag = chosenMat != null ? chosenMat.name : string.Empty;

                    // Optionally enforce unique mini-map color regardless of material duplicates
                    if (enforceUniqueMapColors)
                    {
                        float hU = Mathf.Repeat(0.61803398875f * idx, 1f);
                        Color uniqueC = Color.HSVToRGB(hU, autoSat, autoVal);
                        trackedFromMat.mapColor = uniqueC;
                        // Keep material name in colorTag; the actual hex will be exported from mapColor
                    }
                }
            }
            else if (autoUniqueColors)
            {
                // Generate a distinct color deterministically by index (golden ratio)
                float h = Mathf.Repeat(0.61803398875f * idx, 1f);
                Color c = Color.HSVToRGB(h, autoSat, autoVal);

                // Apply color to renderers using MaterialPropertyBlock where possible
                var trackedAuto = agent.GetComponent<TrackedAgent>();
                if (trackedAuto == null) trackedAuto = agent.AddComponent<TrackedAgent>();
                trackedAuto.mapColor = c;
                trackedAuto.colorTag = "#" + ColorUtility.ToHtmlStringRGB(c);

                var renderers = agent.GetComponentsInChildren<Renderer>();
                foreach (var r in renderers)
                {
                    if (r == null) continue;
                    var mpb = new MaterialPropertyBlock();
                    r.GetPropertyBlock(mpb);

                    // Try common color property names
                    bool applied = false;
                    if (r.sharedMaterial != null)
                    {
                        if (r.sharedMaterial.HasProperty("_BaseColor")) { mpb.SetColor("_BaseColor", c); applied = true; }
                        if (r.sharedMaterial.HasProperty("_Color")) { mpb.SetColor("_Color", c); applied = true; }
                    }
                    if (applied)
                    {
                        r.SetPropertyBlock(mpb);
                    }
                    else
                    {
                        // Fallback: this creates a unique material instance
                        try { r.material.color = c; } catch { /* ignore if shader has no color */ }
                    }
                }
            }

            // (TrackedAgent already ensured and populated earlier)
        }
    }
}
