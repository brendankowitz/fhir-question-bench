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

        info.Environment.Clear();
        foreach (var (name, value) in environment)
        {
            info.Environment[name] = value;
        }

        await using var stdout = File.Create(stdoutPath);
        await using var stderr = File.Create(stderrPath);
        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        using var tree = ProcessTree.Attach(process);
        using var copyCancellation = new CancellationTokenSource();
        Task copies = Task.CompletedTask;
        try
        {
            process.StandardInput.Close();
            copies = Task.WhenAll(
                process.StandardOutput.BaseStream.CopyToAsync(stdout, copyCancellation.Token),
                process.StandardError.BaseStream.CopyToAsync(stderr, copyCancellation.Token));

            var timedOut = await WaitForExitAsync(process, timeout, cancellationToken).ConfigureAwait(false);
            tree.Terminate();
            await WaitForPipesAsync(copies, fileName, cancellationToken).ConfigureAwait(false);
            return new ProcessResult(timedOut ? -1 : process.ExitCode, timedOut, stopwatch.Elapsed);
        }
        finally
        {
            tree.Terminate();
            await SettleCopiesAsync(copies, copyCancellation).ConfigureAwait(false);
        }
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

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return true;
        }
    }

    // Descendants inherit the redirected pipes, so the copies only finish once the whole tree is gone.
    private static async Task WaitForPipesAsync(Task copies, string fileName, CancellationToken cancellationToken)
    {
        try
        {
            await copies.WaitAsync(s_treeExitGrace, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"Child processes of '{fileName}' were still holding its output {s_treeExitGrace.TotalSeconds:0} seconds after the process tree was terminated; the attempt cannot be trusted.");
        }
    }

    private static async Task SettleCopiesAsync(Task copies, CancellationTokenSource copyCancellation)
    {
        if (!copies.IsCompleted)
        {
            await copyCancellation.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await copies.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }
}
