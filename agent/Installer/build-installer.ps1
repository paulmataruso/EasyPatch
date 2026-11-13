#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes the EasyPatch agent and builds EasyPatchAgent-<version>-<arch>.msi (Windows only).

.DESCRIPTION
  1. dotnet publish (self-contained single file) to artifacts/agent/win-<arch>
  2. optional Authenticode signing of EasyPatch.Agent.exe
  3. WiX v5 build of the MSI
  4. optional Authenticode signing of the MSI
  5. writes <msi>.sha256

  Signing is enabled when one of these is set (never commit certificates or passwords):
    EASYPATCH_SIGN_CERT_THUMBPRINT   SHA-1 thumbprint of a code-signing certificate in Cert:\CurrentUser\My or LocalMachine\My
    EASYPATCH_SIGN_PFX_PATH          path to a PFX file, with EASYPATCH_SIGN_PFX_PASSWORD
  Optional:
    EASYPATCH_SIGN_TIMESTAMP_URL     RFC 3161 timestamp server (default http://timestamp.digicert.com)
    EASYPATCH_SIGNTOOL               full path to signtool.exe (default: found in PATH or the Windows 10/11 SDK)

.EXAMPLE
  ./agent/Installer/build-installer.ps1 -Version 1.2.0 -Architecture x64
#>
[CmdletBinding()]
param(
    [Parameter()][ValidatePattern('^\d{1,3}\.\d{1,3}\.\d{1,5}$')][string]$Version = '1.1.0',
    [Parameter()][ValidateSet('x64', 'arm64')][string]$Architecture = 'x64',
    [Parameter()][ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [Parameter()][string]$OutputDirectory,
    [switch]$RequireSigning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    throw 'The MSI can only be built on Windows (WiX uses the Windows Installer API).'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$rid = "win-$Architecture"
$publishDir = Join-Path $repoRoot "artifacts\agent\$rid"
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts\installer' }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$msiName = "EasyPatchAgent-$Version-$Architecture.msi"

function Invoke-Checked([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File failed with exit code $LASTEXITCODE" }
}

function Find-SignTool {
    if ($env:EASYPATCH_SIGNTOOL) { return $env:EASYPATCH_SIGNTOOL }
    $inPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($inPath) { return $inPath.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path $kits) {
        $candidate = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending | Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }
    throw 'signtool.exe not found. Install the Windows SDK or set EASYPATCH_SIGNTOOL.'
}

$signingConfigured = [bool]($env:EASYPATCH_SIGN_CERT_THUMBPRINT -or $env:EASYPATCH_SIGN_PFX_PATH)
if ($RequireSigning -and -not $signingConfigured) {
    throw 'Signing was required but neither EASYPATCH_SIGN_CERT_THUMBPRINT nor EASYPATCH_SIGN_PFX_PATH is set.'
}

function Invoke-Sign([string]$Path, [string]$Description) {
    if (-not $signingConfigured) {
        Write-Warning "Not signing $([System.IO.Path]::GetFileName($Path)) (no signing certificate configured)."
        return
    }

    $signtool = Find-SignTool
    $timestamp = if ($env:EASYPATCH_SIGN_TIMESTAMP_URL) { $env:EASYPATCH_SIGN_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }
    $arguments = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $timestamp, '/d', $Description)
    if ($env:EASYPATCH_SIGN_CERT_THUMBPRINT) {
        $arguments += @('/sha1', $env:EASYPATCH_SIGN_CERT_THUMBPRINT)
    }
    else {
        $arguments += @('/f', $env:EASYPATCH_SIGN_PFX_PATH)
        if ($env:EASYPATCH_SIGN_PFX_PASSWORD) { $arguments += @('/p', $env:EASYPATCH_SIGN_PFX_PASSWORD) }
    }
    $arguments += $Path

    # Arguments are not echoed: they may contain the PFX password.
    & $signtool @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for $Path (exit code $LASTEXITCODE)." }
    Invoke-Checked $signtool @('verify', '/pa', '/q', $Path)
    Write-Host "Signed $Path"
}

Write-Host "==> Publishing agent $Version ($rid)"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
Invoke-Checked 'dotnet' @(
    'publish', (Join-Path $repoRoot 'agent\EasyPatch.Agent\EasyPatch.Agent.csproj'),
    '-c', $Configuration, '-r', $rid, '-o', $publishDir,
    "-p:Version=$Version", '-p:ContinuousIntegrationBuild=true')

$exe = Join-Path $publishDir 'EasyPatch.Agent.exe'
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe" }
Invoke-Sign $exe 'EasyPatch Agent'

Write-Host "==> Building MSI"
$wixOut = Join-Path $repoRoot "artifacts\obj\installer\$Architecture"
Invoke-Checked 'dotnet' @(
    'build', (Join-Path $PSScriptRoot 'EasyPatch.Agent.Installer.wixproj'),
    '-c', $Configuration, '-o', $wixOut,
    "-p:ProductVersion=$Version", "-p:InstallerPlatform=$Architecture", "-p:PublishDir=$publishDir\")

$builtMsi = Join-Path $wixOut 'EasyPatchAgent.msi'
if (-not (Test-Path $builtMsi)) { throw "WiX did not produce $builtMsi" }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$msi = Join-Path $OutputDirectory $msiName
Copy-Item $builtMsi $msi -Force
Invoke-Sign $msi 'EasyPatch Agent Installer'

$hash = (Get-FileHash -Algorithm SHA256 -Path $msi).Hash.ToLowerInvariant()
Set-Content -Path "$msi.sha256" -Value "$hash  $msiName" -Encoding ascii -NoNewline
Write-Host "==> $msi"
Write-Host "    SHA-256 $hash"
