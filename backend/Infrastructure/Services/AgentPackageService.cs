using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EasyPatch.Infrastructure.Services;

public sealed class EasyPatchOptions
{
    public const string Section = "EasyPatch";

    /// <summary>Public HTTPS base URL agents and installers use, e.g. https://patches.example.com.</summary>
    public string PublicUrl { get; set; } = "https://localhost";

    /// <summary>Directory containing agent MSI packages (EasyPatchAgent-&lt;version&gt;-&lt;arch&gt;.msi).</summary>
    public string AgentPackagesPath { get; set; } = "/app/agent-packages";
}

public sealed record AgentPackage(string FileName, string Version, string Architecture, long SizeBytes, string Sha256, DateTimeOffset ModifiedAt);

/// <summary>Lists agent MSIs placed in the packages directory (mounted read-only into the API container).</summary>
public sealed partial class AgentPackageService(IOptions<EasyPatchOptions> options, TimeProvider clock, ILogger<AgentPackageService> logger)
{
    private readonly ConcurrentDictionary<string, (long Length, DateTime Modified, string Sha256)> hashes = new();
    private IReadOnlyList<AgentPackage> cached = [];
    private DateTimeOffset cachedAt = DateTimeOffset.MinValue;
    private readonly Lock gate = new();

    [GeneratedRegex(@"^EasyPatchAgent-(?<version>\d+\.\d+\.\d+(?:\.\d+)?)-(?<arch>x64|arm64)\.msi$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    public IReadOnlyList<AgentPackage> List()
    {
        lock (gate)
        {
            if (clock.GetUtcNow() - cachedAt < TimeSpan.FromSeconds(60))
            {
                return cached;
            }

            cached = Scan();
            cachedAt = clock.GetUtcNow();
            return cached;
        }
    }

    public AgentPackage? GetLatest(string architecture = "x64") =>
        List().Where(p => p.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => Version.TryParse(p.Version, out var v) ? v : new Version(0, 0))
            .FirstOrDefault();

    public AgentPackage? Find(string fileName) =>
        List().FirstOrDefault(p => p.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves a package file path, refusing anything that is not a listed package (no path traversal).</summary>
    public string? GetPath(string fileName)
    {
        var package = Find(fileName);
        return package is null ? null : Path.Combine(options.Value.AgentPackagesPath, package.FileName);
    }

    private List<AgentPackage> Scan()
    {
        var dir = options.Value.AgentPackagesPath;
        var result = new List<AgentPackage>();
        if (!Directory.Exists(dir))
        {
            return result;
        }

        foreach (var path in Directory.EnumerateFiles(dir, "*.msi"))
        {
            var name = Path.GetFileName(path);
            var match = FileNamePattern().Match(name);
            if (!match.Success)
            {
                logger.LogWarning("Ignoring agent package with unexpected name {FileName}", name);
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                var key = info.FullName;
                if (!hashes.TryGetValue(key, out var entry) || entry.Length != info.Length || entry.Modified != info.LastWriteTimeUtc)
                {
                    using var stream = info.OpenRead();
                    entry = (info.Length, info.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(stream)));
                    hashes[key] = entry;
                }

                result.Add(new AgentPackage(name, match.Groups["version"].Value, match.Groups["arch"].Value.ToLowerInvariant(),
                    info.Length, entry.Sha256, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not read agent package {FileName}", name);
            }
        }

        return result;
    }
}

/// <summary>Builds the tokenless PowerShell installer used by RMM tools. The script verifies the MSI SHA-256 (and,
/// optionally, its Authenticode signature) before installing; the endpoint then waits for approval.</summary>
public static class InstallScriptBuilder
{
    public static string Build(string publicUrl, AgentPackage package, string deploymentCode, string label, bool requireSignature, DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(package);
        var server = publicUrl.TrimEnd('/');
        static string Q(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        var sb = new StringBuilder();
        sb.AppendLine("#Requires -Version 5.1");
        sb.AppendLine($"# EasyPatch agent installer for deployment code {deploymentCode} ({label.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)})");
        sb.AppendLine($"# Generated {generatedAt:u}. The endpoint will appear under Agent Management > Pending approvals.");
        sb.AppendLine("# This script contains no secrets: an administrator must approve the endpoint before it is managed.");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
        sb.AppendLine("try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072 -bor 12288 } catch { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072 }");
        sb.AppendLine($"$ServerUrl = {Q(server)}");
        sb.AppendLine($"$DeploymentCode = {Q(deploymentCode)}");
        sb.AppendLine($"$PackageUrl = {Q($"{server}/downloads/agent/{Uri.EscapeDataString(package.FileName)}")}");
        sb.AppendLine($"$ExpectedSha256 = {Q(package.Sha256)}");
        sb.AppendLine($"$RequireSignature = ${(requireSignature ? "true" : "false")}");
        sb.AppendLine("""
            $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
            if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script as Administrator or SYSTEM.' }
            if (-not [Environment]::Is64BitOperatingSystem) { throw 'The EasyPatch agent requires 64-bit Windows.' }
            $work = Join-Path $env:TEMP ('EasyPatch-' + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $work -Force | Out-Null
            $msi = Join-Path $work 'EasyPatchAgent.msi'
            $log = Join-Path $env:TEMP 'EasyPatchAgent-install.log'
            try {
                Write-Output "Downloading $PackageUrl"
                Invoke-WebRequest -Uri $PackageUrl -OutFile $msi -UseBasicParsing
                $hash = (Get-FileHash -Path $msi -Algorithm SHA256).Hash
                if ($hash -ne $ExpectedSha256) { throw "Package hash $hash does not match the expected $ExpectedSha256. Aborting." }
                if ($RequireSignature) {
                    $sig = Get-AuthenticodeSignature -FilePath $msi
                    if ($sig.Status -ne 'Valid') { throw "Package signature is not valid ($($sig.Status)): $($sig.StatusMessage)" }
                    Write-Output "Package signed by: $($sig.SignerCertificate.Subject)"
                }
                $arguments = @('/i', "`"$msi`"", '/qn', '/norestart', "SERVERURL=$ServerUrl", "DEPLOYMENTCODE=$DeploymentCode", '/l*v', "`"$log`"")
                $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $arguments -Wait -PassThru
                if (@(0, 1641, 3010) -notcontains $process.ExitCode) { throw "msiexec failed with exit code $($process.ExitCode). See $log" }
            }
            finally {
                Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
            }
            $agent = Join-Path $env:ProgramFiles 'EasyPatch\Agent\EasyPatch.Agent.exe'
            for ($i = 0; $i -lt 12; $i++) {
                Start-Sleep -Seconds 5
                if (Test-Path $agent) {
                    $status = & $agent status 2>&1 | Out-String
                    if ($status -match 'Fingerprint') { Write-Output $status; break }
                }
            }
            Write-Output 'EasyPatch agent installed. Approve this endpoint in the EasyPatch console (Agent Management > Pending approvals) after comparing the key fingerprint.'
            exit 0
            """);
        return sb.ToString();
    }

    public static string OneLiner(string publicUrl, string deploymentCode) =>
        $"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"[Net.ServicePointManager]::SecurityProtocol=[Net.ServicePointManager]::SecurityProtocol -bor 3072; iex ((New-Object Net.WebClient).DownloadString('{publicUrl.TrimEnd('/')}/downloads/install/{deploymentCode}.ps1'))\"";

    public static string MsiExecCommand(string publicUrl, string token, string? fileName) =>
        $"msiexec /i {fileName ?? "EasyPatchAgent.msi"} /qn /norestart SERVERURL=\"{publicUrl.TrimEnd('/')}\" ENROLLMENTTOKEN=\"{token}\"";
}
