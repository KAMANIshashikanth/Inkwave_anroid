# Smoke runner: one Windows player run per sub; the verdict is the last PASS/FAIL line of summary.txt.
param([string]$Weapon = 'shooter', [string[]]$Subs = @(''), [string]$Special = '', [string]$Map = '', [string]$Mode = '',
      [string]$Roster = '', [string]$Time = '', [string]$Tag = 'run', [int]$Seconds = 90)
$proj = 'D:\color_splash\Inkwave_anroid'
$exe = "$proj\Builds\Windows\ColorSplash.exe"
foreach ($s in $Subs) {
  $name = if ($s) { "$Tag-$s" } else { $Tag }
  $out = "$proj\Builds\Smoke\$name"
  if (Test-Path $out) { Remove-Item -Recurse -Force $out }
  New-Item -ItemType Directory -Force $out | Out-Null
  $a = @('-smoketest', '-smoketest-out', $out, '-smoketest-seconds', "$Seconds", '-smoketest-timescale', '2', '-smoketest-weapon', $Weapon,
         '-screen-width', '1280', '-screen-height', '720', '-screen-fullscreen', '0', '-logFile', "$out\player.log")
  if ($s) { $a += @('-smoketest-sub', $s) }
  if ($Special) { $a += @('-smoketest-special', $Special) }
  if ($Map) { $a += @('-smoketest-map', $Map) }
  if ($Mode) { $a += @('-smoketest-mode', $Mode) }
  if ($Roster) { $a += @('-smoketest-roster', $Roster) }
  if ($Time) { $a += @('-smoketest-time', $Time) }
  $p = Start-Process -FilePath $exe -ArgumentList $a -PassThru
  if (-not $p.WaitForExit(240000)) { $p.Kill(); "$name TIMEOUT"; continue }
  $sum = "$out\summary.txt"
  $verdict = if (Test-Path $sum) { (Get-Content $sum | Select-String -Pattern '^(PASS|FAIL)' | Select-Object -Last 1).Line } else { 'NO SUMMARY' }
  "$name exit $($p.ExitCode) $verdict"
}
