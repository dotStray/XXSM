# Starts the Windows exe with an empty profile and waits for the window to be ready: the log's "Shell ready" line.
# Nothing outside a temporary folder is read or written.
#
# Usage: pwsh packaging/windows/smoke-test.ps1 <XXSM-…-x64.exe> [seconds to wait, default 90]
param(
    [Parameter(Mandatory = $true)][string] $Exe,
    [int] $WaitSeconds = 90
)

$ErrorActionPreference = 'Stop'

$profileRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("xxsm-smoke-" + [guid]::NewGuid())
New-Item -ItemType Directory -Path $profileRoot | Out-Null
$env:XXSM_CONFIG_HOME = Join-Path $profileRoot 'config'
$env:XXSM_DATA_HOME = Join-Path $profileRoot 'data'
$env:XXSM_CACHE_HOME = Join-Path $profileRoot 'cache'
$env:XXSM_STATE_HOME = Join-Path $profileRoot 'state'
$logs = Join-Path $env:XXSM_STATE_HOME 'logs'

$process = Start-Process -FilePath (Resolve-Path $Exe) -PassThru
try {
    for ($second = 0; $second -lt $WaitSeconds; $second++) {
        $ready = Get-ChildItem -Path $logs -Filter '*.log' -ErrorAction SilentlyContinue |
            Select-String -Pattern 'Shell ready' -SimpleMatch
        if ($ready) {
            $ready | ForEach-Object { $_.Line }
            exit 0
        }
        if ($process.HasExited) {
            break
        }
        Start-Sleep -Seconds 1
    }

    Write-Error "The window did not get ready within $WaitSeconds seconds (exit code: $(if ($process.HasExited) { $process.ExitCode } else { 'still running' }))." -ErrorAction Continue
    Get-ChildItem -Path $logs -Filter '*.log' -ErrorAction SilentlyContinue | Get-Content
    exit 1
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
}
