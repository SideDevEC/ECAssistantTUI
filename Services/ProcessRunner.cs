using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ECAssistant.Interfaces;

namespace ECAssistant.Services;

/// <summary>
/// Concrete process execution implementation.
/// </summary>
public class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> ExecuteAsync(string command, string? workDir = null, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"{command}\"",
            WorkingDirectory = workDir ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(ct);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return new ProcessResult(process.ExitCode, stdout, stderr, false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            return new ProcessResult(-1, "", "Process timed out", true);
        }
    }
}