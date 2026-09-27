#Requires -Version 5.1
<#
.SYNOPSIS
    Offline validation: required files, JSON parse, blueprint bundle list, tool roster count.
.DESCRIPTION
    Run from any directory. Default bundle root = parent of this script's folder (the delivery package root).
    Does not start TiaMcpServer or TIA Portal.
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Validate-Bundle.ps1
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Validate-Bundle.ps1 -BundleRoot "D:\kits\TIA_MCP_交付包"
#>
param(
    [Parameter(Mandatory = $false)]
    [string]$BundleRoot = "",
    [switch]$Strict
)

$ErrorActionPreference = "Stop"

function Resolve-BundleRoot {
    if ($BundleRoot -and (Test-Path -LiteralPath $BundleRoot)) {
        return (Resolve-Path -LiteralPath $BundleRoot).Path
    }
    return (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
}

$root = Resolve-BundleRoot
$failures = New-Object System.Collections.Generic.List[string]

function Fail([string]$msg) {
    [void]$failures.Add($msg)
    Write-Host "[FAIL] $msg" -ForegroundColor Red
}

function Ok([string]$msg) {
    Write-Host "[ OK ] $msg" -ForegroundColor Green
}

Write-Host "Bundle root: $root"

# 交付 zip 布局在 tools\...\bin\Release\net48；git clone 布局在 runtime\v21。两处任一存在即可。
$exeCandidates = @(
    (Join-Path $root "tools\tiaportal-mcp\src\TiaMcpServer\bin\Release\net48\TiaMcpServer.exe"),
    (Join-Path $root "runtime\v21\TiaMcpServer.exe")
)
$exe = $exeCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $exe) { Fail "Missing server exe (checked: $($exeCandidates -join ' ; '))" } else { Ok "TiaMcpServer.exe present ($exe)" }

# Sentinel: every launcher must point at an engine that actually exists in this checkout.
# The .cmd/.bat files and this script drifted apart once already — the validator checked one
# path while every user ran another — so the launchers are now parsed and verified here.
$launchers = @('tia.cmd','tia-v20.cmd','配置MCP.bat','配置MCP-v20.bat',
               'scripts\预热.bat','scripts\生成工程.bat')
foreach ($rel in $launchers) {
    $lp = Join-Path $root $rel
    if (-not (Test-Path -LiteralPath $lp)) { Fail ("Missing launcher: " + $rel); continue }
    $ldir = Split-Path -Parent $lp
    $text = Get-Content -LiteralPath $lp -Raw -Encoding UTF8
    $refs = @([regex]::Matches($text, '%~dp0([^"%]*TiaMcpServer\.exe)') |
              ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    if ($refs.Count -eq 0) { Fail ($rel + ': references no TiaMcpServer.exe path'); continue }
    $anyPresent = $false
    foreach ($r in $refs) { if (Test-Path -LiteralPath (Join-Path $ldir $r)) { $anyPresent = $true } }
    if ($anyPresent) { Ok ($rel + ' -> an engine present in this checkout') }
    elseif ($rel -match 'v20') {
        # A git clone ships the V21 runtime only; the V20 launchers say so themselves and point
        # at the release zip, so a missing V20 engine here is expected, not a defect.
        Write-Host ("[WARN] " + $rel + ": no V20 engine in this checkout (expected for a git clone; the launcher tells the user to fetch the release zip)") -ForegroundColor Yellow
    }
    else { Fail ($rel + ' points at no engine present here: ' + ($refs -join ' ; ')) }
}

# The engine ships the trimmed lite roster by default, so it MUST carry the FindTools/CallTool
# bridge — an engine with the small roster but without the bridge is the one genuinely broken
# combination: ~155 tools become unreachable with no way to discover them.
# Binary marker scan on purpose, so the gate never depends on being able to start the engine.
# Key on 'FindTools': 'CallTool' also occurs inside the MCP SDK ("CallToolRequest") and would
# pass on an engine that never defined the bridge at all.
if ($exe) {
    $bytes = [System.IO.File]::ReadAllBytes($exe)
    if ([System.Text.Encoding]::ASCII.GetString($bytes).Contains('FindTools') -or
        [System.Text.Encoding]::Unicode.GetString($bytes).Contains('FindTools')) {
        Ok 'Engine carries the FindTools/CallTool bridge'
    }
    else {
        Fail ('Engine has NO FindTools bridge - it predates the lite-by-default change and ' +
              'would hide ~155 tools with no way to reach them. Rebuild it.')
    }
}

$readme = Join-Path $root "README.md"
if (-not (Test-Path -LiteralPath $readme)) { Fail "Missing README.md" } else { Ok "README.md present" }

$skill = Join-Path $root "tools\tiaportal-mcp\skill\SKILL.md"
if (-not (Test-Path -LiteralPath $skill)) { Fail "Missing SKILL.md" } else { Ok "SKILL.md present" }

$blueprintPath = Join-Path $root "templates\project-blueprints\full_plc_hmi_project.json"
if (-not (Test-Path -LiteralPath $blueprintPath)) {
    Fail "Missing blueprint JSON"
}
else {
    try {
        $blueprint = Get-Content -LiteralPath $blueprintPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Ok "Blueprint JSON parses"
        if ($blueprint.requiredBundleFiles) {
            foreach ($rel in $blueprint.requiredBundleFiles) {
                $p = Join-Path $root ($rel -replace "/", [IO.Path]::DirectorySeparatorChar)
                if (-not (Test-Path -LiteralPath $p)) {
                    Fail "Blueprint requiredBundleFiles missing: $rel"
                }
            }
            Ok ("Blueprint requiredBundleFiles all exist ({0} paths)" -f $blueprint.requiredBundleFiles.Count)
        }
    }
    catch {
        Fail ("Blueprint JSON invalid: " + $_.Exception.Message)
    }
}

$manifestPath = Join-Path $root "manifest\package-manifest.json"
if (Test-Path -LiteralPath $manifestPath) {
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Ok "package-manifest.json parses"
        $expectedTools = $manifest.capabilities.mcpToolCount
        $toolsPath = Join-Path $root "manifest\tools-list.json"
        if (Test-Path -LiteralPath $toolsPath) {
            $toolsDoc = Get-Content -LiteralPath $toolsPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $n = @($toolsDoc.tools).Count
            if ($expectedTools -and ($n -ne $expectedTools)) {
                $msg = "tools-list tool count ($n) != manifest mcpToolCount ($expectedTools)"
                if ($Strict) { Fail $msg } else { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
            }
            else {
                Ok ("tools-list count matches manifest ({0})" -f $n)
            }
        }
    }
    catch {
        Fail ("manifest JSON invalid: " + $_.Exception.Message)
    }
}
else {
    Fail "Missing manifest\package-manifest.json"
}

$plcJsonDir = Join-Path $root "templates\plc\plcbuild-json"
if (Test-Path -LiteralPath $plcJsonDir) {
    Get-ChildItem -LiteralPath $plcJsonDir -Filter "*.json" -File | ForEach-Object {
        try {
            $null = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            Fail ("plcbuild-json invalid: $($_.Name) — " + $_.Exception.Message)
        }
    }
    Ok ("All plcbuild-json files parse ({0} files)" -f @((Get-ChildItem -LiteralPath $plcJsonDir -Filter "*.json" -File)).Count)
}

$hmiDir = Join-Path $root "templates\hmi"
if (Test-Path -LiteralPath $hmiDir) {
    Get-ChildItem -LiteralPath $hmiDir -Filter "*.json" -File | ForEach-Object {
        try {
            $null = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        }
        catch {
            Fail ("HMI template JSON invalid: $($_.Name) — " + $_.Exception.Message)
        }
    }
    Ok ("All templates/hmi JSON files parse ({0} files)" -f @((Get-ChildItem -LiteralPath $hmiDir -Filter "*.json" -File)).Count)
}

# ── Version consistency ───────────────────────────────────────────────────────
# The release version used to live in four places that nobody diffed against each
# other, so they drifted: csproj said 2.5.0, the manifest said 2.5.1, and the
# 2.5.1 fix reached the v21 branch but never master, a tag, or a release. The
# CHANGELOG's newest entry is the source of truth; everything else must match it.
$changelog = Join-Path $root "CHANGELOG.md"
$csproj    = Join-Path $root "tools\tiaportal-mcp\src\TiaMcpServer\TiaMcpServer.csproj"
$manifest  = Join-Path $root "manifest\package-manifest.json"

if ((Test-Path -LiteralPath $changelog) -and (Test-Path -LiteralPath $csproj) -and (Test-Path -LiteralPath $manifest)) {
    $clText = Get-Content -LiteralPath $changelog -Raw -Encoding UTF8
    $clMatch = [regex]::Match($clText, '(?m)^##\s*\[(?<v>\d+\.\d+\.\d+)\]')
    if (-not $clMatch.Success) {
        Fail "CHANGELOG.md: no '## [x.y.z]' entry found — cannot determine the release version"
    }
    else {
        $version = $clMatch.Groups['v'].Value
        $versionFailures = $failures.Count

        $csText = Get-Content -LiteralPath $csproj -Raw -Encoding UTF8
        $csMatch = [regex]::Match($csText, '<AssemblyVersion>(?<v>[^<]+)</AssemblyVersion>')
        if (-not $csMatch.Success) {
            Fail "TiaMcpServer.csproj: no <AssemblyVersion> element"
        }
        elseif ($csMatch.Groups['v'].Value -ne $version) {
            Fail ("Version mismatch: CHANGELOG says {0}, TiaMcpServer.csproj AssemblyVersion says {1}" -f $version, $csMatch.Groups['v'].Value)
        }

        $mf = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($mf.bundleVersion -ne $version) {
            Fail ("Version mismatch: CHANGELOG says {0}, manifest bundleVersion says {1}" -f $version, $mf.bundleVersion)
        }
        if ($mf.packageName -notlike ("*v{0}_*" -f $version)) {
            Fail ("Version mismatch: manifest packageName '{0}' does not carry v{1}" -f $mf.packageName, $version)
        }

        # The Claude Code plugin manifest carries its own version; a version that never moves
        # can stop plugin updates from being picked up (it sat at 2.2.5 through 2.7.3).
        $pluginJson = Join-Path $root ".claude-plugin\plugin.json"
        if (Test-Path -LiteralPath $pluginJson) {
            $pj = Get-Content -LiteralPath $pluginJson -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($pj.version -ne $version) {
                Fail ("Version mismatch: CHANGELOG says {0}, .claude-plugin\plugin.json says {1}" -f $version, $pj.version)
            }
        }

        # The shipped engine is a binary, so a stale runtime/ is invisible in a diff.
        $exe = Join-Path $root "runtime\v21\TiaMcpServer.exe"
        if (Test-Path -LiteralPath $exe) {
            $fileVersion = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
            if ($fileVersion -notlike ("{0}*" -f $version)) {
                Fail ("Version mismatch: CHANGELOG says {0}, runtime\v21\TiaMcpServer.exe reports {1} — rebuild the public engine" -f $version, $fileVersion)
            }

            # Same version number is not the same build: a fix can land in the source after the
            # release build (the #42 readback fix did). The exe records its source commit in
            # ProductVersion ("x.y.z+<sha>"); warn when engine source changed after that commit.
            # Only a warning, and only on a full clone - a shallow CI checkout has no history.
            $productVersion = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
            $shaMatch = [regex]::Match([string]$productVersion, '\+(?<sha>[0-9a-f]{7,40})')
            $git = Get-Command git -ErrorAction SilentlyContinue
            if ($shaMatch.Success -and $git) {
                $isShallow = (& git -C $root rev-parse --is-shallow-repository 2>$null)
                if ($isShallow -eq 'false') {
                    $sha = $shaMatch.Groups['sha'].Value
                    $newer = @(& git -C $root log --format=%h "$sha..HEAD" -- "tools/tiaportal-mcp/src" 2>$null)
                    if ($LASTEXITCODE -eq 0 -and $newer.Count -gt 0) {
                        Write-Host ("[WARN] runtime\v21\TiaMcpServer.exe was built from {0}, but {1} later commit(s) changed the engine source ({2}) - rebuild the public engine before release" -f $sha.Substring(0, [Math]::Min(8, $sha.Length)), $newer.Count, ($newer -join ', ')) -ForegroundColor Yellow
                    }
                }
            }
        }

        if ($failures.Count -eq $versionFailures) {
            Ok ("Version is consistent across CHANGELOG / csproj / manifest / runtime engine ({0})" -f $version)
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "Validation FAILED ($($failures.Count) issue(s))." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "Validation PASSED." -ForegroundColor Green
exit 0
