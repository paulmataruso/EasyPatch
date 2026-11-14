using EasyPatch.Agent.Core.Jobs;
using EasyPatch.Contracts.Agent;

namespace EasyPatch.Agent.Core.Abstractions;

/// <summary>Installs an agent update package (Windows Installer for MSIs; apt or dnf for deb/rpm packages). Only ever
/// called with a package whose release signature and hash the agent has verified. The default members describe Windows
/// Installer.</summary>
public interface IAgentInstaller
{
    /// <summary>Package format this installer handles (<see cref="AgentRelease.FormatMsi"/>, <c>deb</c> or <c>rpm</c>).</summary>
    string PackageFormat => AgentRelease.FormatMsi;

    /// <summary>Used in messages, e.g. "Windows Installer", "apt".</summary>
    string DisplayName => "Windows Installer";

    /// <summary>Starts the installation (Windows: <c>msiexec /i &lt;package&gt; /qn /norestart /l*v &lt;log&gt;</c> with no other
    /// arguments). The upgrade stops the agent service, so the installer normally outlives the process that started it.</summary>
    IInstallerProcess Start(string packagePath, string logPath);

    /// <summary>True while an installation is running (Windows: the <c>_MSIExecute</c> mutex exists).</summary>
    bool IsInstallerBusy();

    /// <summary>The result of an installation started by an earlier agent process, read after the service restarted
    /// (Windows: from the verbose log). Null when unknown.</summary>
    int? ReadResult(string logPath) => AgentUpdateHandler.ReadInstallerResult(logPath);

    /// <summary>Text appended after an exit code, e.g. " (a fatal error occurred during installation)".</summary>
    string DescribeResult(int code) => AgentUpdateHandler.DescribeInstallerError(code);

    /// <summary>Exit codes that mean the package was installed.</summary>
    bool IsSuccess(int code) => code is 0 or 1641 or 3010;
}

public interface IInstallerProcess : IDisposable
{
    /// <summary>Waits for the installer to exit and returns its exit code.</summary>
    Task<int> WaitForExitAsync(CancellationToken cancellationToken);
}
