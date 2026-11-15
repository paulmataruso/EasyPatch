using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using EasyPatch.Agent.Core.Applications;
using EasyPatch.Contracts.Agent;
using Microsoft.Extensions.Logging;

namespace EasyPatch.Agent.Platform;

/// <summary>
/// Provisions App Installer (winget) for all users with Windows' own DISM
/// (<c>dism.exe /Online /Add-ProvisionedAppxPackage</c>), the documented way to add it to Windows Server and other
/// editions without the Microsoft Store. The package files were verified against the pinned SHA-256 values before this
/// runs, and Windows checks Microsoft's package signatures as it installs them. Arguments are passed as a vector, with
/// <c>/NoRestart</c> so DISM never restarts the computer.
/// </summary>
internal sealed class WindowsWingetProvisioner(string logDirectory, ILogger<WindowsWingetProvisioner> logger) : IWingetProvisioner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    public string Architecture => RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

    public string? UnsupportedReason
    {
        get
        {
            var build = Environment.OSVersion.Version.Build;
            if (build < WingetDistribution.MinimumWindowsBuild)
            {
                return $"winget needs Windows 10 version 1809 or Windows Server 2019 or later; this computer runs build {build}.";
            }

            return RuntimeInformation.OSArchitecture is System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.Arm64
                ? null
                : "winget is installed only on x64 and Arm64 Windows.";
        }
    }

    public async Task ProvisionAsync(string bundlePath, IReadOnlyList<string> dependencyPaths, string licensePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(bundlePath);
        ArgumentNullException.ThrowIfNull(dependencyPaths);
        ArgumentException.ThrowIfNullOrEmpty(licensePath);
        Directory.CreateDirectory(logDirectory);
        var log = Path.Combine(logDirectory, "winget-setup.log");
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "dism.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        info.ArgumentList.Add("/Online");
        info.ArgumentList.Add("/Add-ProvisionedAppxPackage");
        info.ArgumentList.Add("/PackagePath:" + Path.GetFullPath(bundlePath));
        foreach (var dependency in dependencyPaths)
        {
            info.ArgumentList.Add("/DependencyPackagePath:" + Path.GetFullPath(dependency));
        }

        info.ArgumentList.Add("/LicensePath:" + Path.GetFullPath(licensePath));
        info.ArgumentList.Add("/NoRestart"); // never prompt for or start a restart
        info.ArgumentList.Add("/English");
        info.ArgumentList.Add("/LogPath:" + log);

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        void Append(string? line)
        {
            if (line is not null)
            {
                lock (output)
                {
                    if (output.Length < 256 * 1024)
                    {
                        output.Append(line).Append('\n');
                    }
                }
            }
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new WingetException($"DISM could not be started ({ex.Message}).", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(Timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw; // the service is stopping; DISM finishes on its own
            }

            Kill(process);
            throw new WingetException($"DISM did not finish within {Timeout.TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} minutes. See {log}.");
        }

        process.WaitForExit();
        string text;
        lock (output)
        {
            text = output.ToString();
        }

        logger.LogDebug("DISM exited with {ExitCode}.", process.ExitCode);
        if (process.ExitCode is 0 or 3010)
        {
            return;
        }

        throw new WingetException($"DISM could not install App Installer (exit code {FormatExitCode(process.ExitCode)}): {Summarize(text)} See {log}.");
    }

    private static string FormatExitCode(int code) =>
        code < 0 ? "0x" + code.ToString("X8", CultureInfo.InvariantCulture) : code.ToString(CultureInfo.InvariantCulture);

    /// <summary>The "Error: ..." line and the message after it, without DISM's banner and progress bars.</summary>
    private static string Summarize(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith('[') && !l.StartsWith("Deployment Image Servicing", StringComparison.Ordinal) &&
                        !l.StartsWith("Version:", StringComparison.Ordinal) && !l.StartsWith("Image Version:", StringComparison.Ordinal))
            .ToList();
        var error = lines.FindIndex(l => l.StartsWith("Error", StringComparison.OrdinalIgnoreCase));
        var relevant = error >= 0 ? lines.Skip(error).Take(3) : lines.TakeLast(3);
        var text = string.Join(" ", relevant);
        return text.Length > 500 ? text[..500] + "…" : text.Length == 0 ? "no output." : text;
    }

    private void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "DISM had already exited.");
        }
    }
}
