# Offline deterministic test of the PG/PC download-route selection (issue #14: DownloadToPlc
# picked the wrong adapter on a multi-NIC PC — WLAN/VPN enumerate before the PLCSIM adapter).
# No TIA Portal needed: EnumerateDownloadRoutes / ScoreDownloadRoutes / SelectDownloadRoute are
# pure reflection over duck-typed properties, so a fake object graph exercises them end to end.
# ApplyConfiguration does not exist on the fake, so SelectDownloadRoute falls through to the
# best-ranked candidate — which is exactly the ordering under test.
# The assembly is copied to an ASCII %TEMP% path first so Windows PowerShell 5.1 can LoadFrom it
# even when the repo lives under a non-ASCII (e.g. Chinese) path.
#
# Usage:  build the V21 exe, then:  powershell -File scripts\Test-DownloadRouteSelection.ps1
# Exit code 0 = all pass, 1 = a case failed or the exe is missing.
$ErrorActionPreference = "Stop"

$srcDir = Join-Path $PSScriptRoot "..\tools\tiaportal-mcp\src\TiaMcpServer\bin\Release\net48"
if (-not (Test-Path -LiteralPath (Join-Path $srcDir "TiaMcpServer.exe"))) {
  Write-Host "FAIL: build the V21 exe first (not found: $srcDir\TiaMcpServer.exe)"; exit 1
}
$tmp = Join-Path $env:TEMP ("tia_routetest_{0}" -f [guid]::NewGuid().ToString("N"))
Copy-Item -LiteralPath $srcDir -Destination $tmp -Recurse -Force

# Loading the Portal type pulls in Siemens.Engineering, which lives in the TIA install (the build
# references it, it is never copied local). Probe the copied output first, then the PublicAPI
# directory recorded in the Openness registry key.
$publicApi = $null
$key = Get-ItemProperty -Path "HKLM:\SOFTWARE\Siemens\Automation\Openness\21.0\PublicAPI\21.0.0.0\net48" -ErrorAction SilentlyContinue
if ($key -and $key."Siemens.Engineering.Base") { $publicApi = Split-Path -Parent $key."Siemens.Engineering.Base" }

$global:ProbeDirs = @($tmp); if ($publicApi) { $global:ProbeDirs += $publicApi }
$global:ProbeTried = @{}
[AppDomain]::CurrentDomain.add_AssemblyResolve({
  param($sender, $e)
  $name = ($e.Name -split ',')[0]
  if ($global:ProbeTried.ContainsKey($name)) { return $null }   # guard against resolve recursion
  $global:ProbeTried[$name] = $true
  foreach ($dir in $global:ProbeDirs) {
    $dll = Join-Path $dir "$name.dll"
    if (Test-Path -LiteralPath $dll) { return [Reflection.Assembly]::LoadFrom($dll) }
  }
  return $null
})

# Duck-typed stand-ins for ConnectionConfiguration -> Modes -> PcInterfaces -> TargetInterfaces.
# Property names (and ConfigurationAddress.Address) match the V21 Openness PublicAPI.
# Add-Type here compiles with the in-box C# 5 compiler: no auto-property initializers.
Add-Type -TypeDefinition @"
using System.Collections.Generic;
public class FakeAddress { public string Address { get; set; } }
public class FakeTarget {
    public FakeTarget() { Addresses = new List<FakeAddress>(); }
    public string Name { get; set; }
    public List<FakeAddress> Addresses { get; set; }
}
public class FakePcInterface {
    public FakePcInterface() { Addresses = new List<FakeAddress>(); TargetInterfaces = new List<FakeTarget>(); }
    public string Name { get; set; }
    public int Number { get; set; }
    public List<FakeAddress> Addresses { get; set; }
    public List<FakeTarget> TargetInterfaces { get; set; }
}
public class FakeMode {
    public FakeMode() { PcInterfaces = new List<FakePcInterface>(); }
    public string Name { get; set; }
    public List<FakePcInterface> PcInterfaces { get; set; }
}
public class FakeConnectionConfiguration {
    public FakeConnectionConfiguration() { Modes = new List<FakeMode>(); }
    public List<FakeMode> Modes { get; set; }
}
public static class FakeBuilder {
    public static FakeTarget Target(string name, string ip) {
        var t = new FakeTarget();
        t.Name = name;
        t.Addresses.Add(new FakeAddress { Address = ip });
        return t;
    }
    public static FakePcInterface Pc(string name, int number, string ip, FakeTarget target) {
        var p = new FakePcInterface();
        p.Name = name; p.Number = number;
        p.Addresses.Add(new FakeAddress { Address = ip });
        p.TargetInterfaces.Add(target);
        return p;
    }
    public static FakeConnectionConfiguration Config(params FakePcInterface[] pcs) {
        var mode = new FakeMode();
        mode.Name = "PN/IE";
        foreach (var pc in pcs) mode.PcInterfaces.Add(pc);
        var cfg = new FakeConnectionConfiguration();
        cfg.Modes.Add(mode);
        return cfg;
    }
}
"@

try {
  $asm = [Reflection.Assembly]::LoadFrom((Join-Path $tmp "TiaMcpServer.exe"))
  $portal = $asm.GetType("TiaMcpServer.Siemens.Portal")
  if (-not $portal) { Write-Host "FAIL: Portal type not found"; exit 1 }
  $flags = [Reflection.BindingFlags]"NonPublic,Static"
  $enumM   = $portal.GetMethod("EnumerateDownloadRoutes", $flags)
  $scoreM  = $portal.GetMethod("ScoreDownloadRoutes", $flags)
  $selectM = $portal.GetMethod("SelectDownloadRoute", $flags)
  if (-not $enumM -or -not $scoreM -or -not $selectM) {
    Write-Host "FAIL: route selection methods not found on Portal"; exit 1
  }

  $pass = 0; $fail = 0
  function Check($desc, $expectPattern, $config, $pgPcFilter, $targetIp) {
    # Print the scores so a failure shows why the ranking came out the way it did.
    $routes = $enumM.Invoke($null, [object[]]@($config))
    $scoreM.Invoke($null, [object[]]@($routes, $targetIp)) | Out-Null
    foreach ($r in $routes) {
      Write-Host ("        score={0,-2} {1}" -f $r.GetType().GetField("Score").GetValue($r),
                                               $r.GetType().GetMethod("Describe").Invoke($r, @()))
    }

    $sel = $selectM.Invoke($null, [object[]]@($config, $pgPcFilter, $targetIp))
    $t = $sel.GetType()
    $err = $t.GetField("Error").GetValue($sel)
    $got = if ($err) { "ERROR: $err" } else { $t.GetField("Description").GetValue($sel) }

    if ($got -like $expectPattern) {
      $script:pass++; Write-Host ("  PASS  {0}" -f $desc)
    } else {
      $script:fail++; Write-Host ("  FAIL  {0}`n        expected: {1}`n        got:      {2}" -f $desc, $expectPattern, $got)
    }
    return $got
  }

  # The reporter's environment: WLAN and a VPN tunnel enumerate BEFORE the PLCSIM virtual adapter,
  # and ApplyConfiguration "succeeds" on all three — so first-wins picked an unreachable adapter.
  $cpu = [FakeBuilder]::Target("PROFINET interface_1", "192.168.0.1")
  $multiNic = [FakeBuilder]::Config(
    [FakeBuilder]::Pc("Realtek WiFi 6",                  1, "192.168.31.77", $cpu),
    [FakeBuilder]::Pc("Meta Tunnel (FlClash)",           2, "198.18.0.1",    $cpu),
    [FakeBuilder]::Pc("PLCSIM Virtual Ethernet Adapter", 3, "192.168.0.241", $cpu))

  Check "auto-pick takes the adapter in the CPU subnet" "*PLCSIM*" $multiNic $null $null | Out-Null
  Check "explicit targetIpAddress keeps that pick"      "*PLCSIM*" $multiNic $null "192.168.0.1" | Out-Null
  Check "explicit pgPcInterface overrides the ranking"  "*Realtek*" $multiNic "realtek" $null | Out-Null
  Check "unknown pgPcInterface lists what IS available" "ERROR: No PG/PC interface matches 'eth42'.*PLCSIM*" $multiNic "eth42" $null | Out-Null
  Check "unreachable target IP lists what IS available" "ERROR: No download route reaches target IP '10.0.0.9'.*PLCSIM*" $multiNic $null "10.0.0.9" | Out-Null

  # No adapter shares the CPU subnet -> every score is 0, ranking is a no-op, and the original
  # enumeration order (the old first-wins behaviour) must survive untouched.
  $noMatch = [FakeBuilder]::Config(
    [FakeBuilder]::Pc("Realtek WiFi 6", 1, "192.168.31.77", $cpu),
    [FakeBuilder]::Pc("Meta Tunnel",    2, "198.18.0.1",    $cpu))
  Check "nothing to distinguish -> first-enumerated wins" "*Realtek*" $noMatch $null $null | Out-Null

  # Sentinel: this expectation is deliberately impossible. If it ever PASSes, the harness is
  # broken (reading the wrong field, swallowing errors) and every result above is meaningless.
  Write-Host "  -- sentinel (must FAIL) --"
  $before = $fail
  Check "sentinel: impossible expectation" "*NoSuchAdapter*" $multiNic $null $null | Out-Null
  if ($fail -eq $before + 1) { $pass++; $fail = $before; Write-Host "  PASS  sentinel failed as required" }
  else { $fail = $before + 1; Write-Host "  FAIL  sentinel did not fail — harness is broken" }

  Write-Host ""
  Write-Host ("{0} passed, {1} failed" -f $pass, $fail)
  if ($fail -gt 0) { exit 1 }
}
finally {
  Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
