#requires -Version 5.1
<#
.SYNOPSIS
Build the Windows x64 Native AOT release of Kettle, optimized for size and memory.
.DESCRIPTION
Requires a .NET SDK supporting net10.0, Visual Studio C++ tools and Windows SDK.
Builds Kettle, native CoreSession and CloudHost in Release. Preserves application data.
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
    $sdkVersion = & $dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdkVersion -notmatch '^(\d+)\.' -or [int]$Matches[1] -lt 10) {
        throw 'Select a .NET SDK supporting net10.0 (version 10 or newer).'
    }

    # Resolve paths relative to this script, regardless of the caller's directory.
    if (![IO.Path]::IsPathRooted($OutputDirectory)) {
        $OutputDirectory = Join-Path $PSScriptRoot $OutputDirectory
    }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    $executable = Join-Path $output 'Kettle.exe'
    $ownedExecutables = @($executable, (Join-Path $output 'Kettle.CoreSession.exe'), (Join-Path $output 'cloud-host\Kettle.CloudHost.exe'))
    $running = Get-Process Kettle,Kettle.CoreSession,Kettle.CloudHost -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -in $ownedExecutables }
    if ($running) {
        throw 'Close Kettle and its CoreSession/CloudHost in the output directory before building.'
    }

    & (Join-Path $PSScriptRoot 'src\Kettle.CoreSession\build.ps1') -Configuration Release
    $project = Join-Path $PSScriptRoot 'src/Kettle/Kettle.csproj'
    $publishOutput = $output.TrimEnd('\', '/') + '/'
    $previousProcessorCount = $env:DOTNET_PROCESSOR_COUNT
    try {
        $env:DOTNET_PROCESSOR_COUNT = '2'
        $restoreArguments = @(
            'restore', $project, '-r', 'win-x64',
            '-p:PublishAot=true', '-p:NuGetAudit=false', '-p:UsedAvaloniaProducts=',
            '--source', 'https://api.nuget.org/v3/index.json'
        )
        & dotnet @restoreArguments
        if ($LASTEXITCODE) {
            throw 'Restore failed.'
        }

        # Explicit XAML compilation is necessary before AOT with the preview SDK.
        $publishArguments = @(
            'msbuild', $project, '-t:Build;CompileAvaloniaXaml;Publish',
            '-p:Configuration=Release', '-p:RuntimeIdentifier=win-x64',
            '-p:SelfContained=true', '-p:PublishAot=true', '-p:Optimize=true',
            '-p:KettleNoGcDiagnostic=false', '-p:UsedAvaloniaProducts=', '-p:NuGetAudit=false',
            '-p:IlcMaxDegreeOfParallelism=1', '-m:1', "-p:PublishDir=$publishOutput", '-nologo'
        )
        & dotnet @publishArguments
        if ($LASTEXITCODE) {
            throw 'Publish failed.'
        }
        Write-Output "Published: $output"
    }
    finally {
        $env:DOTNET_PROCESSOR_COUNT = $previousProcessorCount
    }
    # Incremental native builds can still copy old symbols into the publish folder.
    # Remove only these generated files, never the data directory or other files.
    foreach ($name in @('Kettle.pdb', 'Kettle.Core.pdb', 'Kettle.Shared.pdb', 'Kettle.CoreSession.pdb', 'cloud-host\Kettle.CloudHost.pdb', 'Kettle.TerminalHost.exe', 'Kettle.TerminalHost.dll', 'Kettle.TerminalHost.deps.json', 'Kettle.TerminalHost.pdb', 'Kettle.TerminalHost.runtimeconfig.json')) {
        $generatedFile = Join-Path $output $name
        if (Test-Path -LiteralPath $generatedFile -PathType Leaf) {
            Remove-Item -LiteralPath $generatedFile -Force
        }
    }
    & (Join-Path $output 'Kettle.CoreSession.exe') --self-test
    if ($LASTEXITCODE -ne 0) {
        throw 'The published Release CoreSession failed its self-test.'
    }
    foreach ($file in @('Kettle.exe', 'Kettle.CoreSession.exe', 'cloud-host\Kettle.CloudHost.exe', 'cloud-host\CSteamworks.dll', 'cloud-host\steam_api.dll', 'cloud-host\NOTICE.txt')) {
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
