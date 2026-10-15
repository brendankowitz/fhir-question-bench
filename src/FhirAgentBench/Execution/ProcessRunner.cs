using System.Diagnostics;

namespace FhirAgentBench.Execution;

public sealed record ProcessResult(int ExitCode, bool TimedOut, TimeSpan Duration);

public static class ProcessRunner
{
    private static readonly TimeSpan TreeExitGrace = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(30);

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

    public static Task<string> CaptureAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
        CaptureAsync(fileName, arguments, CaptureTimeout, cancellationToken);

    internal static async Task<string> CaptureAsync(
        string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        var commandLine = $"{fileName} {string.Join(' ', arguments)}";
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start '{fileName}'.");
        using var tree = ProcessTree.Attach(process);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var output = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var error = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeoutSource.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException($"'{commandLine}' did not finish within {timeout.TotalSeconds:0} seconds.");
        }
        finally
        {
            tree.Terminate();
        }

        if (process.ExitCode != 0)
        {
            var detail = (await error.ConfigureAwait(false)).Trim();
            throw new InvalidOperationException(
                $"'{commandLine}' exited with code {process.ExitCode}{(detail.Length > 0 ? ": " + FirstLine(detail) : ".")}");
        }

        return (await output.ConfigureAwait(false)).Trim();
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].TrimEnd('\r');

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
            await copies.WaitAsync(TreeExitGrace, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"Child processes of '{fileName}' were still holding its output {TreeExitGrace.TotalSeconds:0} seconds after the process tree was terminated; the attempt cannot be trusted.");
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
            // Only reached on an already-failing path (or after WaitForPipesAsync gave up); the original exception matters more.
        }
    }
}
