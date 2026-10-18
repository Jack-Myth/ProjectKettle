#requires -Version 5.1
<#
.SYNOPSIS
Build the Windows x64 Native AOT release of Kettle, optimized for size and memory.
.DESCRIPTION
Requires the .NET 10 SDK, Visual Studio C++ desktop build tools and Windows SDK.
Includes the terminal and cloud helpers. Existing application data is preserved.
.EXAMPLE
.\Build-Release.ps1
.EXAMPLE
.\Build-Release.ps1 -OutputDirectory 'D:\Builds\Kettle'
#>
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory = 'artifacts\Kettle-Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    if ($env:OS -ne 'Windows_NT') {
        throw 'This build requires Windows.'
    }
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
    $sdks = & $dotnet --list-sdks
    if ($LASTEXITCODE -ne 0 -or !($sdks -match '^10\.')) {
        throw 'Install the .NET 10 SDK before building.'
    }

    # Resolve paths relative to this script, regardless of the caller's directory.
    if (![IO.Path]::IsPathRooted($OutputDirectory)) {
        $OutputDirectory = Join-Path $PSScriptRoot $OutputDirectory
    }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    $executable = Join-Path $output 'Kettle.exe'
    $running = Get-Process Kettle -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }
    if ($running) {
        throw 'Close the Kettle instance in the output directory before building.'
    }

    $arguments = @(
        'publish', (Join-Path $PSScriptRoot 'src\Kettle\Kettle.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishAot=true', '-p:OptimizationPreference=Size', '-p:Optimize=true',
        '-p:DebugType=None', '-p:DebugSymbols=false',
        '-p:ServerGarbageCollection=false', '-p:ConcurrentGarbageCollection=false',
        '-o', $output
    )
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Release publish failed (exit code $LASTEXITCODE)."
    }
    # Incremental native builds can still copy old symbols into the publish folder.
    # Remove only these generated files, never the data directory or other files.
    foreach ($name in @('Kettle.pdb', 'Kettle.TerminalHost.pdb', 'Kettle.TerminalHost.runtimeconfig.json')) {
        $generatedFile = Join-Path $output $name
        if (Test-Path -LiteralPath $generatedFile -PathType Leaf) {
            Remove-Item -LiteralPath $generatedFile -Force
        }
    }
    foreach ($file in @('Kettle.exe', 'Kettle.TerminalHost.exe', 'cloud-host\Kettle.CloudHost.exe', 'cloud-host\Steamworks.NET.dll', 'cloud-host\CSteamworks.dll', 'cloud-host\steam_api.dll', 'cloud-host\NOTICE.txt')) {
        if (!(Test-Path -LiteralPath (Join-Path $output $file) -PathType Leaf)) {
            throw "The release is incomplete: missing $file"
        }
    }
    Write-Host "Release ready: $executable"
    exit 0
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
