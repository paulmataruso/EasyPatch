namespace EasyPatch.Agent.Core.Abstractions;

/// <summary>Runs Windows Installer for an agent update. Only ever called with a package whose release signature and
/// hash the agent has verified.</summary>
public interface IAgentInstaller
{
    /// <summary>Starts <c>msiexec /i &lt;package&gt; /qn /norestart /l*v &lt;log&gt;</c> with no other arguments. The upgrade
    /// stops the agent service, so the installer normally outlives the process that started it.</summary>
    IInstallerProcess Start(string packagePath, string logPath);

    /// <summary>True while Windows Installer is running an installation (the <c>_MSIExecute</c> mutex exists).</summary>
    bool IsInstallerBusy();
}

public interface IInstallerProcess : IDisposable
{
    /// <summary>Waits for msiexec to exit and returns its exit code.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
}
