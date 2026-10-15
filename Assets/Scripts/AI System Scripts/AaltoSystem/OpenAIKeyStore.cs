using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Single source of truth for the OpenAI API key. Every script that talks to
/// OpenAI resolves its key through here instead of holding its own copy.
///
/// Resolution order:
///   1. A per-component Inspector value, ONLY if it looks like a real key
///      (placeholders like "REDACTED_OPENAI_KEY" and blanks are ignored).
///   2. The local key file (outside the project folder, never committed):
///      persistentDataPath/cnc_openai_key.txt — the file the CNC demo's
///      Technical tab has always written, so previously saved keys keep working.
///   3. The OPENAI_API_KEY environment variable.
///
/// To enter your key ONCE, either paste it in the demo's Technical tab
/// (writes the file above) or set the OPENAI_API_KEY environment variable.
/// </summary>
public static class OpenAIKeyStore
{
    // Application.persistentDataPath must be touched on the main thread; cache it.
    private static string _keyFilePath;
    public static string KeyFilePath =>
        _keyFilePath ?? (_keyFilePath = Path.Combine(Application.persistentDataPath, "cnc_openai_key.txt"));

    /// <summary>Resolves the key. Returns null when nothing usable is found.</summary>
    public static string Resolve(string inspectorOverride = null) => Resolve(inspectorOverride, out _);

    /// <summary>Resolves the key and reports where it came from ("inspector" | "saved" | "env var").</summary>
    public static string Resolve(string inspectorOverride, out string source)
    {
        if (LooksLikeKey(inspectorOverride)) { source = "inspector"; return inspectorOverride.Trim(); }

        try
        {
            if (File.Exists(KeyFilePath))
            {
                var saved = File.ReadAllText(KeyFilePath).Trim();
                if (LooksLikeKey(saved)) { source = "saved"; return saved; }
            }
        }
        catch { /* unreadable file — fall through to env var */ }

        var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (LooksLikeKey(env)) { source = "env var"; return env.Trim(); }

        source = null;
        return null;
    }

    /// <summary>Persists the key to the local file (the one place you enter it).</summary>
    public static void Save(string key) => File.WriteAllText(KeyFilePath, (key ?? "").Trim());

    /// <summary>Non-empty and not a scrubbed placeholder like "REDACTED_OPENAI_KEY".</summary>
    private static bool LooksLikeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return !value.Trim().StartsWith("REDACTED", StringComparison.OrdinalIgnoreCase);
    }
}
