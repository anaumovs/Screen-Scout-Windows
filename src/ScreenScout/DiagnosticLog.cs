using System.Text.Json;

namespace ScreenScout;

/// Bounded, transition-only diagnostics; no key logging or pointer coordinates.
internal sealed class DiagnosticLog(string directory)
{
    private string? previous;
    public void Record(string status, string reason, object diagnostics)
    {
        var state = status + ":" + reason;
        if (state == previous) return;
        previous = state;
        try
        {
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, "recovery.log");
            if (File.Exists(file) && new FileInfo(file).Length > 256 * 1024) File.Move(file, file + ".previous", true);
            File.AppendAllText(file, JsonSerializer.Serialize(new { At = DateTimeOffset.Now, Status = status, Reason = reason, Diagnostics = diagnostics }) + Environment.NewLine);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* Logging must not stop automation. */ }
    }
}
