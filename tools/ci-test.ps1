param([Parameter(Mandatory)][string]$Exe, [string]$Out = "ci-out")
# Runs the engine checks (DesktopReplay.exe --ci-test) and prints the report; exits non-zero on failure.
$ErrorActionPreference = "Stop"
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
$p = Start-Process -FilePath $Exe -ArgumentList "--ci-test", "`"$((New-Item -ItemType Directory -Force $Out).FullName)`"" -Wait -PassThru -NoNewWindow
$report = Join-Path $Out "ci-report.txt"
if (Test-Path $report) { Get-Content $report } else { Write-Host "No report written" }
if ($p.ExitCode -ne 0) { Write-Error "Engine tests failed (exit code $($p.ExitCode))" }
