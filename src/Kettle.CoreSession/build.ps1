#requires -Version 5.1
# Builds only the native CoreSession helper. Use Build-Release.ps1 for the full application.
param(
    [string]$OutputDirectory = '',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)
$ErrorActionPreference = 'Stop'
$installation = & "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe" -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) {
    throw 'MSVC C++ tools are required.'
}
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot "bin/$Configuration"
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force $output | Out-Null
$dev = Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
$source = Join-Path $PSScriptRoot 'main.cpp'
$diagnostics = if ($Configuration -eq 'Debug') { '/DKETTLE_CORE_SESSION_DEBUG' } else { '/DNDEBUG' }
$command = "call `"$dev`" -arch=x64 -host_arch=x64 >nul && cl /nologo /std:c++20 /EHsc /O2 /MT /W4 /utf-8 /DUNICODE /D_UNICODE $diagnostics `"$source`" /Fo`"$output/main.obj`" /Fe`"$output/Kettle.CoreSession.exe`" /link /SUBSYSTEM:CONSOLE user32.lib gdi32.lib gdiplus.lib shell32.lib ole32.lib"
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE) {
    throw "CoreSession $Configuration build failed: $LASTEXITCODE"
}
