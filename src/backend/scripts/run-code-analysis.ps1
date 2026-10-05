<#
.SYNOPSIS
    Rebuilds the backend solution with Roslyn analyzers (built-in .NET analyzers +
    SonarAnalyzer.CSharp, enabled repo-wide via src/backend/Directory.Build.props) and
    collects the per-project SARIF output into one Markdown report.

.PARAMETER OutFile
    Where to write the Markdown report. Defaults to .notes/code-analysis/report.md.
#>

param(
    [string]$OutFile = (Join-Path $PSScriptRoot "../../../.notes/code-analysis/report.md")
)

$ErrorActionPreference = "Stop"

$backendRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$repoRootPath = (Resolve-Path (Join-Path $backendRoot "../..")).Path.Replace('\', '/')
$sln = Join-Path $backendRoot "LinkShortener.sln"

# --no-incremental forces every project to recompile, so every project's analysis.sarif
# is regenerated this run instead of possibly being stale from a previous build.
Write-Host "Building $sln with analyzers (this can take a while)..."
& dotnet build $sln --no-incremental /nologo
if ($LASTEXITCODE -ne 0) {
    Write-Warning "dotnet build exited with code $LASTEXITCODE - analyzer diagnostics are still collected below, but the report may be incomplete for projects that failed to compile."
}

$sarifFiles = Get-ChildItem -Path $backendRoot -Recurse -Filter "analysis.sarif" -File
if (-not $sarifFiles) {
    Write-Error "No analysis.sarif files found under $backendRoot - did the build run at all?"
    exit 1
}

# ruleId -> { Description, Severity, Count }
$ruleStats = @{}
# projectName -> fileRelativePath -> list of findings
$byProject = [ordered]@{}

foreach ($sarifFile in $sarifFiles) {
    $projectName = $sarifFile.Directory.Parent.Name
    $sarif = Get-Content $sarifFile.FullName -Raw | ConvertFrom-Json

    foreach ($run in $sarif.runs) {
        $ruleDescriptions = @{}
        foreach ($rule in $run.tool.driver.rules) {
            $desc = $rule.shortDescription.text
            if (-not $desc) { $desc = $rule.fullDescription.text }
            $ruleDescriptions[$rule.id] = $desc
        }

        foreach ($result in $run.results) {
            $ruleId = $result.ruleId
            $severity = $result.level
            if (-not $severity) { $severity = "warning" }

            if (-not $ruleStats.ContainsKey($ruleId)) {
                $ruleStats[$ruleId] = [PSCustomObject]@{
                    Description = $ruleDescriptions[$ruleId]
                    Severity    = $severity
                    Count       = 0
                }
            }
            $ruleStats[$ruleId].Count++

            if (-not $byProject.Contains($projectName)) {
                $byProject[$projectName] = [ordered]@{}
            }

            $location = $result.locations | Select-Object -First 1
            $filePath = $location.physicalLocation.artifactLocation.uri
            $line = $location.physicalLocation.region.startLine
            if ($filePath) {
                # SARIF stores an absolute file:// URI - trim it down to a repo-relative path for readability.
                $filePath = [System.Uri]::UnescapeDataString($filePath) -replace '^file:///', ''
                $filePath = $filePath.Replace('\', '/').Substring($repoRootPath.Length + 1)
            }
            else {
                $filePath = "(unknown file)"
            }

            if (-not $byProject[$projectName].Contains($filePath)) {
                $byProject[$projectName][$filePath] = New-Object System.Collections.Generic.List[object]
            }
            $byProject[$projectName][$filePath].Add([PSCustomObject]@{
                Line     = $line
                RuleId   = $ruleId
                Severity = $severity
                Message  = $result.message.text
            })
        }
    }
}

$totalCount = ($ruleStats.Values | Measure-Object -Property Count -Sum).Sum
$bySeverity = $ruleStats.Values | Group-Object Severity | ForEach-Object {
    [PSCustomObject]@{ Severity = $_.Name; Count = ($_.Group | Measure-Object -Property Count -Sum).Sum }
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# Backend code analysis report")
[void]$sb.AppendLine()
[void]$sb.AppendLine("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm')")
[void]$sb.AppendLine('Analyzers: built-in .NET analyzers (`AnalysisLevel=latest-recommended`) + SonarAnalyzer.CSharp')
[void]$sb.AppendLine('Regenerate: `pwsh src/backend/scripts/run-code-analysis.ps1`')
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Summary")
[void]$sb.AppendLine()
[void]$sb.AppendLine("Total findings: **$totalCount**")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| Severity | Count |")
[void]$sb.AppendLine("|---|---|")
foreach ($s in ($bySeverity | Sort-Object -Property Count -Descending)) {
    [void]$sb.AppendLine("| $($s.Severity) | $($s.Count) |")
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Findings by rule")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| Rule | Severity | Count | Description |")
[void]$sb.AppendLine("|---|---|---|---|")
foreach ($kv in ($ruleStats.GetEnumerator() | Sort-Object { $_.Value.Count } -Descending)) {
    $rule = $kv.Value
    [void]$sb.AppendLine("| $($kv.Key) | $($rule.Severity) | $($rule.Count) | $($rule.Description) |")
}
[void]$sb.AppendLine()
[void]$sb.AppendLine("## Findings by project")

foreach ($projectEntry in $byProject.GetEnumerator()) {
    $projectName = $projectEntry.Key
    $files = $projectEntry.Value
    $projectCount = ($files.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("### $projectName ($projectCount)")
    foreach ($fileEntry in $files.GetEnumerator()) {
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("**$($fileEntry.Key)**")
        foreach ($finding in ($fileEntry.Value | Sort-Object Line)) {
            [void]$sb.AppendLine("- L$($finding.Line) [$($finding.Severity)] ``$($finding.RuleId)``: $($finding.Message)")
        }
    }
}

$outDir = Split-Path -Parent $OutFile
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}
$sb.ToString() | Set-Content -Path $OutFile -Encoding utf8

Write-Host "Report written to $OutFile ($totalCount findings across $($sarifFiles.Count) projects)"
