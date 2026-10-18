using System.Diagnostics;

namespace Scarab.Compatibility;

public static class GameProcess
{
    public static void EnsureClosed()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string name = process.ProcessName.ToLowerInvariant().Replace(".x86_64", "").Replace(".exe", "")
                        .Replace("_", "").Replace(" ", "").Replace("-", "");
                    if (name == "hollowknight")
                        throw new IOException("Close Hollow Knight before changing the API or mods / Закройте Hollow Knight перед изменением API или модов.");
                }
                catch (InvalidOperationException) { /* The process has exited. */ }
                catch (System.ComponentModel.Win32Exception) { /* Unrelated protected system process. */ }
            }
        }
    }
}
