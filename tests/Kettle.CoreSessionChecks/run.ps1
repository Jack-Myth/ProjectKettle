$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
dotnet build "$PSScriptRoot/Kettle.CoreSessionChecks.csproj" -c Debug -p:NuGetAudit=false
if ($LASTEXITCODE) { throw 'Test build failed' }
$out = Join-Path $PSScriptRoot 'bin/Debug/net10.0'
& "$repo/src/Kettle.CoreSession/build.ps1" -OutputDirectory $out
$installation = & "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe" -latest -property installationPath
$dev = Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
New-Item -ItemType Directory -Force "$out/fixture", "$out/cloud-host" | Out-Null
$command = "call `"$dev`" -arch=x64 -host_arch=x64 >nul && cl /nologo /std:c++20 /EHsc /O2 /MT `"$PSScriptRoot/fixture.cpp`" /Fo`"$out/fixture.obj`" /Fe`"$out/fixture/steamcmd.exe`""
& $env:ComSpec /d /s /c $command
if ($LASTEXITCODE) { throw 'Fixture build failed' }
Copy-Item "$out/fixture/steamcmd.exe" "$out/Kettle.exe" -Force
dotnet publish "$repo/src/Kettle.CloudHost/Kettle.CloudHost.csproj" -c Debug -r win-x86 -p:Optimize=true -p:NuGetAudit=false -p:IlcMaxDegreeOfParallelism=1 -m:1 -o "$repo/src/Kettle.CloudHost/bin/Debug/aot-publish"
if ($LASTEXITCODE) { throw 'CloudHost publish failed' }
Copy-Item "$repo/src/Kettle.CloudHost/bin/Debug/aot-publish/*" "$out/cloud-host" -Force
$native = Start-Process "$out/Kettle.CoreSession.exe" -ArgumentList '--self-test' -WindowStyle Hidden -Wait -PassThru
if ($native.ExitCode) { throw 'Native self-test failed' }
& "$out/Kettle.CoreSessionChecks.exe"
if ($LASTEXITCODE) { throw 'Native integration check failed' }
& "$out/Kettle.CoreSessionChecks.exe" --stop-check
if ($LASTEXITCODE) { throw 'PID-based Stop integration check failed' }

& "$out/Kettle.CoreSessionChecks.exe" --launch-failure
if ($LASTEXITCODE) { throw 'Launch-failure recovery check failed' }
