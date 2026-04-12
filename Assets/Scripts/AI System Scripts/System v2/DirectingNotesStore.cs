using System;

// Shared directing notes across chat/interview controllers (persistent in-memory for session).
public static class DirectingNotesStore
{
    private static string _notes = "";

    public static string Notes => _notes ?? "";

    public static void Append(string note)
    {
        var txt = (note ?? "").Trim();
        if (string.IsNullOrWhiteSpace(txt)) return;
        if (!string.IsNullOrWhiteSpace(_notes)) _notes += "\n";
        _notes += txt;
    }
}
