using System.Text.RegularExpressions;
using EasyPatch.Infrastructure.Services;
using Xunit;

namespace EasyPatch.Tests.Unit;

public sealed class InstallScriptTests
{
    private static readonly AgentPackage Package = new("EasyPatchAgent-1.2.0-x64.msi", "1.2.0", "x64", 12_345_678,
        "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", DateTimeOffset.UnixEpoch);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EasyPatch.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Script_verifies_the_package_and_carries_no_secret()
    {
        var script = InstallScriptBuilder.Build("https://patch.example.com/", Package, "EPD-ABCDEFGHJKMNPQRS", "RMM 'rollout'", requireSignature: true,
            DateTimeOffset.UnixEpoch);

        Assert.Contains("$ExpectedSha256 = '" + Package.Sha256 + "'", script, StringComparison.Ordinal);
        Assert.Contains("$RequireSignature = $true", script, StringComparison.Ordinal);
        Assert.Contains("Get-AuthenticodeSignature", script, StringComparison.Ordinal);
        Assert.Contains("'https://patch.example.com/downloads/agent/EasyPatchAgent-1.2.0-x64.msi'", script, StringComparison.Ordinal);
        Assert.Contains("DEPLOYMENTCODE=$DeploymentCode", script, StringComparison.Ordinal);
        Assert.DoesNotContain("ENROLLMENTTOKEN", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Restart-Computer", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Script_looks_for_the_agent_where_the_msi_installs_it()
    {
        var wxs = File.ReadAllText(Path.Combine(RepositoryRoot(), "agent", "Installer", "Package.wxs"));
        var folder = Regex.Match(wxs, "<Directory Id=\"INSTALLFOLDER\" Name=\"(?<name>[^\"]+)\"").Groups["name"].Value;
        Assert.False(string.IsNullOrEmpty(folder));

        var script = InstallScriptBuilder.Build("https://patch.example.com", Package, "EPD-ABCDEFGHJKMNPQRS", "x", false, DateTimeOffset.UnixEpoch);
        Assert.Contains($"Join-Path $env:ProgramFiles '{folder}\\EasyPatch.Agent.exe'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Msi_keeps_the_enrollment_token_out_of_its_logs()
    {
        var wxs = File.ReadAllText(Path.Combine(RepositoryRoot(), "agent", "Installer", "Package.wxs"));

        Assert.Matches("<Property Id=\"ENROLLMENTTOKEN\"[^>]*Hidden=\"yes\"", wxs);
        // The token is written by WriteRegistryValues; its progress template must not include the value ([3]).
        var template = Regex.Match(wxs, "<ProgressText Action=\"WriteRegistryValues\"[^>]*Template=\"(?<t>[^\"]*)\"").Groups["t"].Value;
        Assert.False(string.IsNullOrEmpty(template));
        Assert.DoesNotContain("[3]", template, StringComparison.Ordinal);
    }

    [Fact]
    public void One_liner_and_msiexec_commands_point_at_the_public_url()
    {
        Assert.Contains("https://patch.example.com/downloads/install/EPD-ABC.ps1", InstallScriptBuilder.OneLiner("https://patch.example.com/", "EPD-ABC"),
            StringComparison.Ordinal);
        Assert.Equal("msiexec /i EasyPatchAgent-1.2.0-x64.msi /qn /norestart SERVERURL=\"https://patch.example.com\" ENROLLMENTTOKEN=\"ept_x\"",
            InstallScriptBuilder.MsiExecCommand("https://patch.example.com/", "ept_x", "EasyPatchAgent-1.2.0-x64.msi"));
    }
}
