using System.Runtime.InteropServices;
using System.Text;

namespace Scarab.Services;

public record DiagnosticReport(string Text, string? SavedPath);
public static class Diagnostics
{
    private static readonly object Gate = new();
    private static string? _lastError;
    public static string? LastBuildLog { get; private set; }
    public static string LogDirectory
    {
        get
        {
            string path = Path.Combine(Settings.GetOrCreateDirPath(), "Logs");
            Directory.CreateDirectory(path);
            return path;
        }
    }
    public static string BeginBuildLog()
    {
        string path = Path.Combine(LogDirectory, $"api-build-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "Scarab Modern: API build\n", new UTF8Encoding(false));
        LastBuildLog = path;
        return path;
    }
    public static DiagnosticReport Capture(Exception error, string operation, ISettings? settings = null)
    {
        Log.Error(error, "Operation failed: {Operation}", operation);
        lock (Gate) _lastError = $"Operation: {operation}\nTime: {DateTimeOffset.Now:O}\n\n{error}";
        string text = Collect(settings);
        try
        {
            string path = Path.Combine(LogDirectory, $"error-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return new DiagnosticReport(text, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DiagnosticReport(text + "\nAutomatic report save failed: " + e.Message, null);
        }
    }
    public static string Collect(ISettings? settings = null)
    {
        var text = new StringBuilder();
        text.AppendLine("Scarab Modern diagnostic report");
        text.AppendLine($"Time: {DateTimeOffset.Now:O}");
        text.AppendLine($"OS: {RuntimeInformation.OSDescription}; architecture: {RuntimeInformation.ProcessArchitecture}");
        text.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        text.AppendLine($"Manager: {typeof(Diagnostics).Assembly.GetName().Version}");
        if (settings is not null)
        {
            text.AppendLine($"Managed: {settings.ManagedFolder}");
            try { text.AppendLine("Game: " + Compatibility.GameBuild.Read(new System.IO.Abstractions.FileSystem(), settings.ManagedFolder)); }
            catch (Exception e) { text.AppendLine("Game detection: " + e.Message); }
        }
        lock (Gate) text.AppendLine(_lastError ?? "No operation error recorded in this session.");
        if (LastBuildLog is not null) AppendTail(text, LastBuildLog);
        try
        {
            foreach (string path in Directory.GetFiles(Settings.GetOrCreateDirPath(), "ModInstaller-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(2)) AppendTail(text, path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { text.AppendLine(e.Message); }
        return text.ToString();
    }
    private static void AppendTail(StringBuilder text, string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bool trimmed = file.Length > 256 * 1024;
            if (trimmed) file.Seek(-256 * 1024, SeekOrigin.End);
            using var reader = new StreamReader(file, Encoding.UTF8, true);
            text.AppendLine($"\n--- {Path.GetFileName(path)}{(trimmed ? " (last 256 KiB)" : "")} ---");
            text.AppendLine(reader.ReadToEnd());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { text.AppendLine($"Cannot read {path}: {e.Message}"); }
    }
}
