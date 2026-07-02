# Offline deterministic test of Guard.MatchPlcName (the tolerant softwarePath matcher).
# No TIA Portal needed: reflects over the built V21 assembly and invokes the pure static.
# The assembly is copied to an ASCII %TEMP% path first so Windows PowerShell 5.1 can
# LoadFrom it even when the repo lives under a non-ASCII (e.g. Chinese) path.
#
# Usage:  build the V21 exe, then:  powershell -File scripts\Test-MatchPlcName.ps1
# Exit code 0 = all pass, 1 = a case failed or the exe is missing.
$ErrorActionPreference = "Stop"

$srcExe = Join-Path $PSScriptRoot "..\tools\tiaportal-mcp\src\TiaMcpServer\bin\Release\net48\TiaMcpServer.exe"
if (-not (Test-Path -LiteralPath $srcExe)) {
  Write-Host "FAIL: build the V21 exe first (not found: $srcExe)"; exit 1
}
$tmp = Join-Path $env:TEMP ("tia_matchtest_{0}.exe" -f [guid]::NewGuid().ToString("N"))
Copy-Item -LiteralPath $srcExe -Destination $tmp -Force

try {
  $asm = [Reflection.Assembly]::LoadFrom($tmp)
  $guard = $asm.GetType("TiaMcpServer.Siemens.Guard")
  if (-not $guard) { Write-Host "FAIL: Guard type not found"; exit 1 }
  $mi = $guard.GetMethod("MatchPlcName", [Reflection.BindingFlags]"Public,Static")
  if (-not $mi) { Write-Host "FAIL: MatchPlcName method not found"; exit 1 }

  function Match([string[]]$avail, $token) {
    $list = New-Object 'System.Collections.Generic.List[string]'
    foreach ($x in $avail) { $list.Add($x) }
    $argv = New-Object 'System.Object[]' 2
    $argv[0] = [System.Collections.Generic.IReadOnlyList[string]]$list
    $argv[1] = [string]$token
    return $mi.Invoke($null, $argv)
  }

  $pass = 0; $fail = 0
  function Check($desc, $avail, $token, $expected) {
    $got = Match $avail $token
    if ($got -eq $expected) {
      $script:pass++; Write-Host ("  PASS  {0,-22} '{1}' -> {2}" -f $desc, $token, $(if ($null -eq $got) {'<null>'} else {$got}))
    } else {
      $script:fail++; Write-Host ("  FAIL  {0,-22} '{1}' -> got '{2}' expected '{3}'" -f $desc, $token, $got, $expected)
    }
  }

  $single = @("PLC_1")
  Check "single exact"      $single "PLC_1"   "PLC_1"
  Check "single case"       $single "plc_1"   "PLC_1"
  Check "single trim"       $single " PLC_1 " "PLC_1"
  Check "single sole-auto"  $single "garbage" "PLC_1"
  Check "single substr"     $single "plc"     "PLC_1"

  $multi = @("MainPLC","SafetyPLC","PLC_1")
  Check "multi exact"       $multi "SafetyPLC"   "SafetyPLC"
  Check "multi case"        $multi "safetyplc"   "SafetyPLC"
  Check "multi trim"        $multi " SafetyPLC " "SafetyPLC"
  Check "multi uniq-substr" $multi "Safety"      "SafetyPLC"
  Check "multi ambiguous"   $multi "PLC"         $null
  Check "multi not-found"   $multi "NoSuch"      $null
  Check "multi empty"       $multi ""            $null

  $two = @("PLC_1","PLC_2")
  Check "two exact"         $two "PLC_2"  "PLC_2"
  Check "two uniq-digit"    $two "1"      "PLC_1"
  Check "two ambiguous"     $two "PLC"    $null

  Check "empty list"        @()    "PLC_1" $null

  Write-Host ""
  Write-Host "RESULT: $pass passed, $fail failed"
  if ($fail -gt 0) { exit 1 } else { exit 0 }
}
finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
