using System.Diagnostics;
using System.Text;

namespace Scarab.Services;

public static class ToolRunner
{
    public static async Task<string> RunAsync(string executable, IEnumerable<string> arguments, string directory,
        string logPath, TimeSpan limit)
    {
        var args = arguments.ToArray();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        start.Environment["VSLANG"] = "1033";
        // Use the SDK selected by this dotnet host, not a stale environment from another MSBuild.
        start.Environment.Remove("MSBuildSDKsPath");
        start.Environment.Remove("MSBUILD_EXE_PATH");
        using var writer = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        writer.WriteLine($"\n[{DateTimeOffset.Now:O}] {executable} {string.Join(" ", args)}");
        var tail = new Queue<string>();
        var gate = new object();
        async Task Pump(StreamReader reader, string channel)
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                lock (gate)
                {
                    writer.WriteLine($"[{channel}] {line}");
                    tail.Enqueue(line);
                    while (tail.Count > 100) tail.Dequeue();
                }
            }
        }
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("Could not start build tool.");
            var stdout = Pump(process.StandardOutput, "out");
            var stderr = Pump(process.StandardError, "err");
            using var timeout = new CancellationTokenSource(limit);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(true);
                await Task.WhenAll(stdout, stderr);
                throw new IOException($"Превышено время ожидания {executable}. Полный лог: {logPath}");
            }
            await Task.WhenAll(stdout, stderr);
            string last = string.Join("\n", tail);
            writer.WriteLine($"Exit code: {process.ExitCode}");
            if (process.ExitCode != 0) throw new IOException($"Сборка API завершилась с кодом {process.ExitCode}. Полный лог: {logPath}\n{last}");
            return last;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            writer.WriteLine(e);
            throw new IOException($"Не удалось запустить {executable}. Нужен .NET SDK 8.0 в PATH; на Linux/macOS также Mono. Лог: {logPath}", e);
        }
    }
}
