using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Comskip.Comskip;

/// <summary>Runs an external tool, keeping the tail of its output for error messages.</summary>
public static class ProcessRunner
{
    /// <summary>Runs a process to completion. Killed (with its children) on cancellation.</summary>
    /// <param name="fileName">Executable.</param>
    /// <param name="args">Arguments.</param>
    /// <param name="workingDirectory">Working directory.</param>
    /// <param name="lowPriority">Run at below-normal priority.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="token">Cancellation token.</param>
    /// <param name="captureAll">Return all of stdout instead of just the tail.</param>
    /// <returns>Exit code and output.</returns>
    public static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string workingDirectory,
        bool lowPriority,
        ILogger logger,
        CancellationToken token,
        bool captureAll = false)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        logger.LogDebug("Running {File} {Args}", fileName, string.Join(' ', psi.ArgumentList));

        var stdout = new System.Text.StringBuilder();
        var tail = new Queue<string>();
        void Collect(string? line, bool isStdout)
        {
            if (line is null)
            {
                return;
            }

            lock (tail)
            {
                if (captureAll && isStdout)
                {
                    stdout.AppendLine(line);
                }

                tail.Enqueue(line);
                while (tail.Count > 15)
                {
                    tail.Dequeue();
                }
            }
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Collect(e.Data, true);
        process.ErrorDataReceived += (_, e) => Collect(e.Data, false);

        if (!process.Start())
        {
            return (-1, "process did not start");
        }

        if (lowPriority)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                logger.LogDebug(ex, "Could not lower process priority");
            }
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogDebug(ex, "Process already exited");
            }

            throw;
        }

        // Make sure the async output readers have drained.
        process.WaitForExit();

        lock (tail)
        {
            return (process.ExitCode, captureAll ? stdout.ToString() : string.Join(" | ", tail));
        }
    }
}
