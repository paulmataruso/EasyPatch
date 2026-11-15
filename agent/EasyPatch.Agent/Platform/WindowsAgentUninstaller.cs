using System.Diagnostics;
using System.Runtime.InteropServices;
using EasyPatch.Agent.Core.Abstractions;
using EasyPatch.Agent.Core.Jobs;

namespace EasyPatch.Agent.Platform;

/// <summary>
/// Removes the installed EasyPatch Agent product with <c>%SystemRoot%\System32\msiexec.exe /x {ProductCode} /qn /norestart</c>.
/// The product code is looked up from the MSI's fixed upgrade code, so only the EasyPatch Agent can be removed; the log path
/// is the only other argument. The MSI's own uninstall sequence stops the service, runs <c>unenroll</c> and deletes the program.
/// </summary>
internal sealed partial class WindowsAgentUninstaller : IAgentUninstaller
{
    /// <summary>UpgradeCode of agent/Installer/Package.wxs (it never changes).</summary>
    public const string UpgradeCode = "{FFC8A7E3-FD7E-4FE0-9494-327AA9E089E4}";

    private const uint ErrorSuccess = 0;

    private readonly string _productCode;

    private WindowsAgentUninstaller(string productCode)
    {
        _productCode = productCode;
    }

    public string DisplayName => "Windows Installer";

    /// <summary>The uninstaller for the installed EasyPatch Agent, or null when no product with the agent's upgrade code is
    /// installed (for example a development build run from a folder).</summary>
    public static WindowsAgentUninstaller? TryCreate() => FindInstalledProduct() is { } productCode ? new WindowsAgentUninstaller(productCode) : null;

    public IInstallerProcess Start(string logPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(logPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath))!);
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
        };

        foreach (var argument in (string[])["/x", _productCode, "/qn", "/norestart", "/l*v", Path.GetFullPath(logPath)])
        {
            info.ArgumentList.Add(argument);
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("msiexec did not start.");
        return new UninstallerProcess(process);
    }

    public bool IsInstallerBusy() => WindowsAgentInstaller.IsWindowsInstallerBusy();

    public int? ReadResult(string logPath) => AgentUpdateHandler.ReadInstallerResult(logPath);

    public string DescribeResult(int code) => code switch
    {
        1605 => " (the EasyPatch Agent is not installed)",
        1641 or 3010 => " (Windows must restart to finish removing it)",
        _ => AgentUpdateHandler.DescribeInstallerError(code),
    };

    private static unsafe string? FindInstalledProduct()
    {
        try
        {
            var buffer = stackalloc char[39]; // a GUID in braces and the terminating null
            return MsiEnumRelatedProducts(UpgradeCode, 0, 0, buffer) == ErrorSuccess ? new string(buffer) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("msi.dll", EntryPoint = "MsiEnumRelatedProductsW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint productIndex, char* productBuffer);

    private sealed class UninstallerProcess(Process process) : IInstallerProcess
    {
        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken);
            return process.ExitCode;
        }

        /// <summary>Releases the handle only; msiexec keeps running (it is what stops and removes this service).</summary>
        public void Dispose() => process.Dispose();
    }
}
