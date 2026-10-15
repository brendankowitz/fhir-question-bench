using System.Diagnostics;

namespace FhirAgentBench.Execution;

public sealed record ProcessResult(int ExitCode, bool TimedOut, TimeSpan Duration);

public static class ProcessRunner
{
    private static readonly TimeSpan s_treeExitGrace = TimeSpan.FromSeconds(15);

    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string workingDirectory,
        string stdoutPath,
        string stderrPath,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            info.Environment[name] = value;
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        process.StandardInput.Close();
        await using var stdout = File.Create(stdoutPath);
        await using var stderr = File.Create(stderrPath);
        var copyOut = process.StandardOutput.BaseStream.CopyToAsync(stdout, CancellationToken.None);
        var copyErr = process.StandardError.BaseStream.CopyToAsync(stderr, CancellationToken.None);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await KillTreeAsync(process, fileName, Task.WhenAll(copyOut, copyErr)).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            timedOut = true;
        }

        await Task.WhenAll(copyOut, copyErr).ConfigureAwait(false);
        return new ProcessResult(timedOut ? -1 : process.ExitCode, timedOut, stopwatch.Elapsed);
    }

    public static async Task<string> CaptureAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{fileName} {string.Join(' ', arguments)}' exited with code {process.ExitCode}.");
        }

        return output.Trim();
    }

    // Descendants inherit the redirected pipes, so the copies only finish once the whole tree is gone.
    private static async Task KillTreeAsync(Process process, string fileName, Task pipesClosed)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await pipesClosed.WaitAsync(s_treeExitGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"Child processes of '{fileName}' were still running {s_treeExitGrace.TotalSeconds:0} seconds after the process tree was killed; the attempt cannot be trusted.");
        }
    }
}
