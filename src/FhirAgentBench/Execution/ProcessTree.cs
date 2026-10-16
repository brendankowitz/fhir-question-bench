using System.Diagnostics;

namespace FhirAgentBench.Execution;

/// <summary>
/// Owns the lifetime of a started process and everything it spawns. On Windows a kill-on-close job object also reaches
/// orphans whose parent already exited; elsewhere only the live tree below the process can be killed.
/// </summary>
internal sealed class ProcessTree : IDisposable
{
    private readonly Process _process;
    private readonly JobObject? _job;

    private ProcessTree(Process process, JobObject? job)
    {
        _process = process;
        _job = job;
    }

    public static ProcessTree Attach(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ProcessTree(process, null);
        }

        JobObject? job = null;
        try
        {
            job = JobObject.CreateKillOnClose();
            job.Assign(process);
            return new ProcessTree(process, job);
        }
        catch
        {
            job?.Dispose();
            process.Kill(entireProcessTree: true);
            throw;
        }
    }

    public void Terminate()
    {
        try
        {
            if (_job is not null)
            {
                _job.Terminate();
            }
            else
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The tree already exited (or is exiting); there is nothing left to terminate.
        }
    }

    public void Dispose() => _job?.Dispose();
}
