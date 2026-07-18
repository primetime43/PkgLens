[CmdletBinding()]
param(
    [string] $Name = 'Overall',

    [Parameter(Mandatory = $true)]
    [string] $ResultsDirectory,

    [ValidateRange(0, 100)]
    [double] $MinimumLinePercent = 70,

    [ValidateRange(0, 100)]
    [double] $MinimumBranchPercent = 52,

    [string] $SummaryPath
)

$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture
$coverageCandidates = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter 'coverage.cobertura.xml' -File)

if ($coverageCandidates.Count -eq 0) {
    throw "No coverage.cobertura.xml files were found under '$ResultsDirectory'."
}

$uniqueCoverageFiles = [ordered] @{}
foreach ($coverageCandidate in $coverageCandidates) {
    $contentHash = (Get-FileHash -LiteralPath $coverageCandidate.FullName -Algorithm SHA256).Hash
    if (-not $uniqueCoverageFiles.Contains($contentHash)) {
        $uniqueCoverageFiles[$contentHash] = $coverageCandidate
    }
}
$coverageFiles = @($uniqueCoverageFiles.Values)

[long] $linesCovered = 0
[long] $linesValid = 0
[long] $branchesCovered = 0
[long] $branchesValid = 0

foreach ($coverageFile in $coverageFiles) {
    [xml] $coverageDocument = Get-Content -LiteralPath $coverageFile.FullName -Raw
    $coverage = $coverageDocument.coverage
    $linesCovered += [long]::Parse($coverage.'lines-covered', $culture)
    $linesValid += [long]::Parse($coverage.'lines-valid', $culture)
    $branchesCovered += [long]::Parse($coverage.'branches-covered', $culture)
    $branchesValid += [long]::Parse($coverage.'branches-valid', $culture)
}

if ($linesValid -le 0) {
    throw 'Coverage report contains no valid lines.'
}

$linePercent = 100 * $linesCovered / $linesValid
$branchPercent = if ($branchesValid -eq 0) { 100 } else { 100 * $branchesCovered / $branchesValid }
$linePassed = $linePercent -ge $MinimumLinePercent
$branchPassed = $branchPercent -ge $MinimumBranchPercent
$status = if ($linePassed -and $branchPassed) { 'PASS' } else { 'FAIL' }

Write-Host ("{0} coverage {1}: lines {2:N2}% ({3:N0}/{4:N0}, minimum {5:N2}%); branches {6:N2}% ({7:N0}/{8:N0}, minimum {9:N2}%)." -f
    $Name, $status, $linePercent, $linesCovered, $linesValid, $MinimumLinePercent,
    $branchPercent, $branchesCovered, $branchesValid, $MinimumBranchPercent)

if ($SummaryPath) {
    $summaryDirectory = Split-Path -Parent $SummaryPath
    if ($summaryDirectory) {
        New-Item -ItemType Directory -Path $summaryDirectory -Force | Out-Null
    }

    @"
## $Name coverage gate

| Metric | Actual | Minimum | Result |
|---|---:|---:|:---:|
| Lines | $($linePercent.ToString('N2', $culture))% ($linesCovered / $linesValid) | $($MinimumLinePercent.ToString('N2', $culture))% | $(if ($linePassed) { 'Pass' } else { 'Fail' }) |
| Branches | $($branchPercent.ToString('N2', $culture))% ($branchesCovered / $branchesValid) | $($MinimumBranchPercent.ToString('N2', $culture))% | $(if ($branchPassed) { 'Pass' } else { 'Fail' }) |

Unique Cobertura files evaluated: $($coverageFiles.Count)
"@ | Add-Content -LiteralPath $SummaryPath
}

if (-not $linePassed -or -not $branchPassed) {
    throw 'Coverage is below the configured threshold.'
}
