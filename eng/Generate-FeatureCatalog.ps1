[CmdletBinding()]
param(
    [switch] $Check
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root 'docs/feature-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -Depth 20

if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported feature manifest schema version '$($manifest.schemaVersion)'."
}

$statusInfo = @{
    covered = @{ Markdown = '✅'; Css = 'done'; Label = 'Covered'; Summary = 'covered' }
    partial = @{ Markdown = '🟡'; Css = 'partial'; Label = 'Partial'; Summary = 'partial' }
    outOfScope = @{ Markdown = '⛔'; Css = 'scope'; Label = 'By design'; Summary = 'out of scope by design' }
    notBuilt = @{ Markdown = '⬜'; Css = 'miss'; Label = 'Not built'; Summary = 'not yet built' }
}

$features = @($manifest.comparison.sections | ForEach-Object { @($_.features) })
foreach ($feature in $features) {
    if (-not $statusInfo.ContainsKey([string] $feature.status)) {
        throw "Feature '$($feature.name)' has unknown status '$($feature.status)'."
    }
}

function HtmlEncode([string] $value) {
    [System.Net.WebUtility]::HtmlEncode($value)
}

function InlineMarkdownToHtml([string] $value) {
    $parts = $value -split '`', -1
    $output = for ($index = 0; $index -lt $parts.Count; $index++) {
        $encoded = HtmlEncode $parts[$index]
        if ($index % 2 -eq 1) { "<code>$encoded</code>" } else { $encoded }
    }
    $output -join ''
}

function EscapeMarkdownTable([string] $value) {
    $value.Replace('|', '\|').Replace("`r", '').Replace("`n", ' ')
}

function CSharpString([string] $value) {
    '"' + $value.Replace('\', '\\').Replace('"', '\"').Replace("`r", '\r').Replace("`n", '\n') + '"'
}

function CountStatus([string] $status) {
    @($features | Where-Object status -eq $status).Count
}

function FeatureNameMarkdown($feature) {
    $name = EscapeMarkdownTable ([string] $feature.name)
    if ($feature.qualifier) { "$name *($($feature.qualifier))*" } else { $name }
}

function FeatureNameHtml($feature) {
    $name = HtmlEncode ([string] $feature.name)
    if ($feature.qualifier) {
        '{0} <span style="color:var(--ink-3);font-size:.82em">({1})</span>' -f
            $name, (HtmlEncode ([string] $feature.qualifier))
    } else {
        $name
    }
}

function BuildReadmeCoverage {
    $covered = CountStatus 'covered'
    $partial = CountStatus 'partial'
    $scope = CountStatus 'outOfScope'
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("$($manifest.comparison.summary) **$covered covered · $partial partial · $scope out of scope by design.** $($manifest.comparison.outOfScopeNote)")
    $lines.Add('See the full status and TrueAncestor comparison in [`docs/coverage.html`](docs/coverage.html).')
    $lines -join "`n"
}

function BuildHtmlTiles {
    $order = @('covered', 'partial', 'outOfScope', 'notBuilt')
    ($order | ForEach-Object {
        $status = $statusInfo[$_]
        $count = CountStatus $_
        $label = if ($_ -eq 'outOfScope') { 'Out of scope <em>by design</em>' } else { (HtmlEncode ([string] $status.Summary)) }
        '    <div class="tile {0}"><div class="n">{1}</div><div class="l">{2}</div></div>' -f
            $status.Css, $count, $label
    }) -join "`n"
}

function BuildHtmlRows($section) {
    (@($section.features) | ForEach-Object {
        $status = $statusInfo[[string] $_.status]
        '        <tr><td class="num">{0}</td><td class="feat">{1}</td><td><span class="pill {2}"><i class="dot {2}"></i>{3}</span></td><td class="how">{4}</td></tr>' -f
            (HtmlEncode ([string] $_.code)), (FeatureNameHtml $_), $status.Css, $status.Label,
            (InlineMarkdownToHtml ([string] $_.description))
    }) -join "`n"
}

function BuildHtmlBeyond {
    (@($manifest.beyond) | ForEach-Object {
        '      <div class="xcard"><h3><span class="chk">✓</span> {0}</h3><p>{1}</p></div>' -f
            (InlineMarkdownToHtml ([string] $_.title)), (InlineMarkdownToHtml ([string] $_.description))
    }) -join "`n"
}

function BuildHtmlFooter {
    (@($manifest.footer) | ForEach-Object {
        "    <p><b>$(HtmlEncode ([string] $_.lead))</b> $(InlineMarkdownToHtml ([string] $_.text))</p>"
    }) -join "`n"
}

function BuildFeatureText {
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('namespace PkgLens.Core.Shared;')
    $lines.Add('')
    $lines.Add('public static class FeatureText')
    $lines.Add('{')
    foreach ($property in $manifest.guiDescriptions.PSObject.Properties) {
        if ($property.Name -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
            throw "GUI description key '$($property.Name)' is not a valid C# identifier."
        }
        $lines.Add("    public const string $($property.Name) = $(CSharpString ([string] $property.Value));")
    }
    $lines.Add("    public const string CliSummary = $(CSharpString ([string] $manifest.cliSummary));")
    $lines.Add('}')
    $lines -join "`n"
}

$pending = [ordered] @{}

function SetGeneratedRegion([string] $relativePath, [string] $id, [string] $content) {
    $path = Join-Path $root $relativePath
    $text = if ($pending.Contains($path)) { $pending[$path] } else { [IO.File]::ReadAllText($path) }
    $newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $normalizedContent = $content.Replace("`r`n", "`n").Replace("`n", $newline)
    $start = "<!-- BEGIN GENERATED:$id -->"
    $end = "<!-- END GENERATED:$id -->"
    $pattern = [regex]::Escape($start) + '.*?' + [regex]::Escape($end)
    if (-not [regex]::IsMatch($text, $pattern, [Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw "Generated region '$id' was not found in '$relativePath'."
    }
    $startPosition = $text.IndexOf($start, [StringComparison]::Ordinal)
    $lineStart = $text.LastIndexOf("`n", [Math]::Max(0, $startPosition - 1)) + 1
    $indent = $text.Substring($lineStart, $startPosition - $lineStart)
    $replacement = $start + $newline + $normalizedContent + $newline + $indent + $end
    $pending[$path] = [regex]::Replace($text, $pattern, [Text.RegularExpressions.MatchEvaluator] { param($match) $replacement }, [Text.RegularExpressions.RegexOptions]::Singleline)
}

SetGeneratedRegion 'README.md' 'README-COVERAGE' (BuildReadmeCoverage)
SetGeneratedRegion 'docs/coverage.html' 'HTML-TILES' (BuildHtmlTiles)
foreach ($section in $manifest.comparison.sections) {
    SetGeneratedRegion 'docs/coverage.html' ("HTML-ROWS-" + $section.id.ToUpperInvariant()) (BuildHtmlRows $section)
}
SetGeneratedRegion 'docs/coverage.html' 'HTML-BEYOND' (BuildHtmlBeyond)
SetGeneratedRegion 'docs/coverage.html' 'HTML-FOOTER' (BuildHtmlFooter)

$featureTextPath = Join-Path $root 'src/PkgLens.Core/Shared/FeatureText.g.cs'
$pending[$featureTextPath] = (BuildFeatureText) + "`n"

$stale = [System.Collections.Generic.List[string]]::new()
foreach ($entry in $pending.GetEnumerator()) {
    $current = if ([IO.File]::Exists($entry.Key)) { [IO.File]::ReadAllText($entry.Key) } else { '' }
    if ($current -ceq $entry.Value) { continue }
    $relative = [IO.Path]::GetRelativePath($root, $entry.Key)
    if ($Check) {
        $stale.Add($relative)
    } else {
        $directory = Split-Path -Parent $entry.Key
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        [IO.File]::WriteAllText($entry.Key, $entry.Value, [Text.UTF8Encoding]::new($false))
        Write-Host "Generated $relative"
    }
}

if ($stale.Count -gt 0) {
    throw "Generated feature files are stale: $($stale -join ', '). Run ./eng/Generate-FeatureCatalog.ps1."
}

if ($Check) {
    Write-Host 'Generated feature files are up to date.'
}
