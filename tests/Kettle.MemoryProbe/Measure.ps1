param([int]$Runs = 3)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$exe = Join-Path $projectRoot 'artifacts/memory-probe/Kettle.MemoryProbe.exe'
$outputRoot = Join-Path $projectRoot 'artifacts/memory-investigation'
$null = New-Item -ItemType Directory -Path $outputRoot -Force
foreach ($mode in @('login', 'main', 'main-empty', 'main-small', 'transition')) {
    for ($run = 1; $run -le $Runs; $run++) {
        $output = Join-Path $outputRoot "$mode-$run.tsv"
        $process = Start-Process -FilePath $exe -ArgumentList @($mode, ('"' + $output + '"')) -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
        try {
            if (!$process.WaitForExit(25000)) {
                $process.Kill()
                $process.WaitForExit()
                throw "Memory probe timed out: $mode"
            }
            if ($process.ExitCode -ne 0) { throw "Memory probe failed: $mode ($($process.ExitCode))" }
        }
        finally { $process.Dispose() }
    }
}
Get-FileHash -LiteralPath $exe -Algorithm SHA256 | Format-List | Out-File (Join-Path $outputRoot 'binary.txt')
