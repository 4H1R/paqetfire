using System.ComponentModel;
using System.Diagnostics;

namespace PaqetFire.Broker.Tests;

internal static class StandInProcess
{
    /// <summary>
    /// Starts a long-running ping from <paramref name="path"/> and waits until Windows reports
    /// its image path. Orphan cleanup matches processes by <see cref="Process.MainModule"/>,
    /// which cannot be read for a process whose modules are still loading, so a cleanup that
    /// runs right after <see cref="Process.Start()"/> would otherwise skip it on a slow machine.
    /// </summary>
    public static Process Start(string path)
    {
        var process = Process.Start(new ProcessStartInfo(path, "-t 127.0.0.1") { CreateNoWindow = true })!;
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                process.Refresh();
                if (process.MainModule?.FileName is not null)
                {
                    return process;
                }
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                // Still loading.
            }

            Thread.Sleep(20);
        }

        return process;
    }
}
