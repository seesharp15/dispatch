using System.Collections.Concurrent;
using System.Diagnostics;

namespace Dispatch.Web.Services;

public class ChildProcessRegistry
{
    private readonly ConcurrentDictionary<int, Process> _processes = new();
    private readonly ILogger<ChildProcessRegistry> _logger;

    public ChildProcessRegistry(ILogger<ChildProcessRegistry> logger)
    {
        _logger = logger;
    }

    public void Register(Process process)
    {
        _processes[process.Id] = process;
        process.Exited += (_, _) => _processes.TryRemove(process.Id, out _);
        process.EnableRaisingEvents = true;
    }

    public void Unregister(Process process)
    {
        _processes.TryRemove(process.Id, out _);
    }

    public void KillAll()
    {
        foreach (var (_, process) in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    _logger.LogInformation("Killing child process {ProcessId} ({ProcessName}).", process.Id, process.ProcessName);
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to kill process {ProcessId}.", process.Id);
            }
        }

        _processes.Clear();
    }
}
