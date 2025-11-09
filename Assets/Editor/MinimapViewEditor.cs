using UnityEngine;
using UnityEditor;
using UnityEngine.UI;
using UnityEditor.SceneManagement;

[CustomEditor(typeof(MinimapView))]
public class MinimapViewEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        var mv = (MinimapView)target;

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Editor Preview", EditorStyles.boldLabel);

        if (GUILayout.Button("Preview Map (create 4 corner markers)"))
        {
            CreatePreviewMarkers(mv);
        }

        if (GUILayout.Button("Clear Preview Markers"))
        {
            ClearPreviewMarkers(mv);
        }
    }

    static void CreatePreviewMarkers(MinimapView mv)
    {
        if (mv == null)
        {
            Debug.LogWarning("No MinimapView target");
            return;
        }

        RectTransform mapArea = mv.mapArea != null ? mv.mapArea : (mv.mapBackground != null ? mv.mapBackground.rectTransform : null);
        if (mapArea == null)
        {
            Debug.LogWarning("MinimapView: assign a Map Area or Map Background in the inspector before previewing.");
            return;
        }

        // Determine world bounds to use
        Vector2 min = Vector2.zero, max = Vector2.one;
        if (mv.useFixedBounds)
        {
            min = mv.fixedMin;
            max = mv.fixedMax;
        }
        else if (mv.sampler != null)
        {
            // try sampler's public fields
            min = mv.sampler.worldMin;
            max = mv.sampler.worldMax;
        }
        else
        {
            // default to a centered 10x10
            min = new Vector2(-5f, -5f);
            max = new Vector2(5f, 5f);
        }

        // corners in world XZ space
        Vector2[] corners = new Vector2[] { new Vector2(min.x, min.y), new Vector2(max.x, min.y), new Vector2(min.x, max.y), new Vector2(max.x, max.y) };

        // create markers
        for (int i = 0; i < corners.Length; i++)
        {
            // compute UV
            float u = Mathf.InverseLerp(min.x, max.x, corners[i].x);
            float v = Mathf.InverseLerp(min.y, max.y, corners[i].y);

            var size = mapArea.rect.size;
            Vector2 pos = new Vector2(u * size.x, v * size.y);
            pos -= Vector2.Scale(size, mapArea.pivot);

            // create marker GameObject
            GameObject go = new GameObject($"EditorPreview_Marker_{i}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            Undo.RegisterCreatedObjectUndo(go, "Create Preview Marker");
            go.transform.SetParent(mapArea, false);

            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(mv.markerSize * 1.8f, mv.markerSize * 1.8f);

            var img = go.GetComponent<Image>();
            img.raycastTarget = false;
            if (mv.markerPrefab != null && mv.markerPrefab.sprite != null)
                img.sprite = mv.markerPrefab.sprite;
            img.color = mv.forceWhiteMarkers ? Color.white : Color.cyan;

            // mark scene dirty so change persists
            EditorSceneManager.MarkSceneDirty(go.scene);
        }
    }

    static void ClearPreviewMarkers(MinimapView mv)
    {
        if (mv == null) return;
        RectTransform mapArea = mv.mapArea != null ? mv.mapArea : (mv.mapBackground != null ? mv.mapBackground.rectTransform : null);
        if (mapArea == null) return;

        // Collect children to remove
        var toRemove = new System.Collections.Generic.List<GameObject>();
        foreach (Transform child in mapArea)
        {
            if (child.name.StartsWith("EditorPreview_Marker_"))
                toRemove.Add(child.gameObject);
        }

        foreach (var go in toRemove)
        {
            Undo.DestroyObjectImmediate(go);
            EditorSceneManager.MarkSceneDirty(go.scene);
        }
    }
}
