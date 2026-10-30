#Requires -Version 5.1
<#
.SYNOPSIS
    Regenerate docs/tool-capability-matrix.md from the [McpServerTool] attributes in McpServer.cs.
.DESCRIPTION
    Static extraction, offline. Replaces the previous (broken) generator that emitted the literal
    PowerShell expression "$(@{name=...}.name)" into every Tool cell instead of the tool name.
    Handles both single-line and multi-line (string-concatenated) Description attributes.
    Run from any directory; defaults resolve against the delivery package root (parent of this script).
.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Generate-ToolCapabilityMatrix.ps1
#>
param(
    [string]$SourceFile = "",
    [string]$OutFile = ""
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
if (-not $OutFile)    { $OutFile    = Join-Path $root "docs\tool-capability-matrix.md" }

# [McpServerTool] attributes live across the McpServer partial files (McpServer.cs,
# McpServer.Runtime.cs, ...). Scan all of them so partial-file tools are not missed.
if (-not $SourceFile) {
    $dir = Join-Path $root "tools\tiaportal-mcp\src\TiaMcpServer\ModelContextProtocol"
    $files = Get-ChildItem -LiteralPath $dir -Filter "McpServer*.cs" | Sort-Object Name
    if (-not $files) { throw "No McpServer*.cs found in: $dir" }
    $text = ($files | ForEach-Object { [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8) }) -join "`n"
} else {
    if (-not (Test-Path -LiteralPath $SourceFile)) { throw "Source not found: $SourceFile" }
    $text = [System.IO.File]::ReadAllText($SourceFile, [System.Text.Encoding]::UTF8)
}

# Match each attribute block: [McpServerTool(Name = "X"), Description( <body> )]
# Body may span lines as "..." + "..." concatenation; Singleline lets . cross newlines.
$blockRx = [regex]::new(
    '\[McpServerTool\(Name\s*=\s*"(?<name>[^"]+)"\)\s*,\s*Description\((?<body>.*?)\)\]',
    [System.Text.RegularExpressions.RegexOptions]::Singleline)
# A segment is a C# string literal: escaped quotes (\") must not end it. The old pattern
# "[^"]*" stopped at the first \" and garbled every description that shows a JSON example
# (CallTool rendered as argumentsJson='{\:\,\:\}').
$segRx = [regex]'"(?<seg>(?:[^"\\]|\\.)*)"'

$tools = New-Object System.Collections.Generic.List[object]
foreach ($m in $blockRx.Matches($text)) {
    $name = $m.Groups['name'].Value
    # Rebuild the description by concatenating every quoted segment in the body.
    $desc = (($segRx.Matches($m.Groups['body'].Value) | ForEach-Object { $_.Groups['seg'].Value }) -join '').Trim()
    # Undo the C# escapes so the matrix shows the text the model actually sees.
    $desc = [regex]::Replace($desc, '\\(["\\])', '$1')
    $layer = "L?"; $domain = "Misc"
    $tag = ([regex]'^\[(?<layer>L[0-9])\]\[(?:Category:)?(?<domain>[^\]]+)\]').Match($desc)
    if ($tag.Success) { $layer = $tag.Groups['layer'].Value; $domain = $tag.Groups['domain'].Value }
    $descCell = $desc -replace '\|', '\|'
    $tools.Add([pscustomobject]@{ Name = $name; Layer = $layer; Domain = $domain; Desc = $descCell; Order = $tools.Count })
}
if ($tools.Count -eq 0) { throw "No [McpServerTool] entries parsed from $SourceFile" }

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# MCP 工具能力矩阵")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("本文件由源码中的 ``[McpServerTool]`` 静态抽取生成，运行时仍以 ``tools/list`` 为准。")
[void]$sb.AppendLine("")
[void]$sb.AppendLine(("- 生成时间：{0}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss")))
[void]$sb.AppendLine(("- 工具数量：{0}" -f $tools.Count))
[void]$sb.AppendLine("")

foreach ($layer in @("L0", "L1", "L2", "L?")) {
    $inLayer = @($tools | Where-Object { $_.Layer -eq $layer })
    if ($inLayer.Count -eq 0) { continue }
    [void]$sb.AppendLine("## $layer")
    [void]$sb.AppendLine("")
    foreach ($domain in ($inLayer | Select-Object -ExpandProperty Domain -Unique | Sort-Object)) {
        [void]$sb.AppendLine("### $domain")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("| Tool | Description |")
        [void]$sb.AppendLine("|---|---|")
        foreach ($t in ($inLayer | Where-Object { $_.Domain -eq $domain } | Sort-Object Order)) {
            [void]$sb.AppendLine(("| {0} | {1} |" -f $t.Name, $t.Desc))
        }
        [void]$sb.AppendLine("")
    }
}

[System.IO.File]::WriteAllText($OutFile, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("Wrote {0} tools to {1}" -f $tools.Count, $OutFile) -ForegroundColor Green
