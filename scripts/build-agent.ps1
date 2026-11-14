#Requires -Version 5.1
<#
.SYNOPSIS
  Builds and tests the EasyPatch agent and, on Windows, produces the MSI installer(s).

.EXAMPLE
  pwsh scripts/build-agent.ps1 -Version 1.2.0
  pwsh scripts/build-agent.ps1 -Version 1.2.0 -Architecture x64,arm64 -RequireSigning
#>
[CmdletBinding()]
param(
    [ValidatePattern('^\d{1,3}\.\d{1,3}\.\d{1,5}$')][string]$Version = '1.1.1',
    [ValidateSet('x64', 'arm64')][string[]]$Architecture = @('x64'),
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [switch]$RequireSigning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    $projects = @(
        'agent/EasyPatch.Communication/EasyPatch.Communication.csproj',
        'agent/EasyPatch.Agent.Core/EasyPatch.Agent.Core.csproj',
        'agent/EasyPatch.WindowsUpdate/EasyPatch.WindowsUpdate.csproj',
        'agent/EasyPatch.Inventory/EasyPatch.Inventory.csproj',
        'agent/EasyPatch.Agent/EasyPatch.Agent.csproj',
        'agent/EasyPatch.Agent.Tests/EasyPatch.Agent.Tests.csproj'
    )

    Write-Host "==> Building agent projects ($Version, warnings are errors)"
    foreach ($project in $projects) {
        dotnet build $project -c Release "-p:Version=$Version"
        if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
    }

    if (-not $SkipTests) {
        Write-Host '==> Running agent tests'
        dotnet test agent/EasyPatch.Agent.Tests/EasyPatch.Agent.Tests.csproj -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Agent tests failed.' }
    }

    $onWindows = $PSVersionTable.PSEdition -eq 'Desktop' -or $IsWindows
    foreach ($arch in $Architecture) {
        if (-not $SkipInstaller -and $onWindows) {
            & (Join-Path $repoRoot 'agent/Installer/build-installer.ps1') -Version $Version -Architecture $arch -RequireSigning:$RequireSigning
        }
        else {
            $rid = "win-$arch"
            Write-Host "==> Publishing $rid (MSI requires Windows)"
            dotnet publish agent/EasyPatch.Agent/EasyPatch.Agent.csproj -c Release -r $rid -o "artifacts/agent/$rid" "-p:Version=$Version"
            if ($LASTEXITCODE -ne 0) { throw "Publish failed: $rid" }
        }
    }
}
finally {
    Pop-Location
}
