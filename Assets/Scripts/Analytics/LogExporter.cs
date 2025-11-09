// Assets/Scripts/LogExporter.cs
using System.IO;
using System.Text;
using UnityEngine;
using LivePositions;   // to access PositionSampler

public class LogExporter : MonoBehaviour
{
    [Tooltip("Reference to your PositionSampler in the scene")]
    public PositionSampler sampler;

    [Tooltip("File name for CSV (saved in Application.persistentDataPath)")]
    public string fileName = "positions.csv";

    [ContextMenu("Export Log To CSV")]
    public void Export()
    {
        if (sampler == null)
        {
            Debug.LogError("No PositionSampler assigned!");
            return;
        }

        var log = sampler.Log;
        if (log.Count == 0)
        {
            Debug.LogWarning("No samples to export.");
            return;
        }

        StringBuilder sb = new StringBuilder();
    sb.AppendLine("time,uid,id,x,z,isExtra,color,colorTag");

        foreach (var entry in log)
        {
            string hex = ColorUtility.ToHtmlStringRGB(entry.color);
            string tag = string.IsNullOrEmpty(entry.colorTag) ? ("#" + hex) : entry.colorTag.Replace(",", "_");
            sb.AppendFormat(
                "{0:F3},{1},{2},{3:F3},{4:F3},{5},#{6},{7}\n",
                entry.t,
                entry.uid,
                entry.id,
                entry.xz.x,
                entry.xz.y,
                entry.isExtra ? 1 : 0,
                hex,
                tag
            );
        }

        string path = Path.Combine(Application.persistentDataPath, fileName);
        File.WriteAllText(path, sb.ToString());

        Debug.Log($"Exported {log.Count} samples to {path}");
    }
}
