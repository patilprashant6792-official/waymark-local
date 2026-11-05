<#
.SYNOPSIS
  Design-system guard for the admin UI. Run before every UI change is committed.
.DESCRIPTION
  Fails (exit 1) when markup bypasses the design system:
    1. inline style= / Style=            (allow-list below)
    2. raw Mud controls outside Components/Ui
    3. raw <button outside Components/Ui (allow-list below)
    4. the retired wm-chip class
    5. hex colours outside DesignSystem.cs and terminal.css
    6. wm-* classes used in markup/code but defined in no wwwroot/css file
  Usage:  powershell -ExecutionPolicy Bypass -File scripts/ui-check.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$host_ = Join-Path $root 'src/Waymark.Host'
$comp = Join-Path $host_ 'Components'
$cssDir = Join-Path $host_ 'wwwroot/css'

$markup = Get-ChildItem $comp -Recurse -Include *.razor, *.razor.cs, *.cs |
  Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
$inUi = { param($f) $f.FullName -match '[\\/]Components[\\/]Ui[\\/]' }

$allowInline = @('style="@CellStyle', 'style="@run.Style.InlineStyle')
$allowButton = @('MainLayout.razor', 'CollapsibleSection.razor', 'CommandPalette.razor', 'ReconnectModal.razor')
$rawMud = '<(MudButton|MudIconButton|MudTextField|MudSelect|MudSwitch|MudCheckBox|MudChip|MudTabs)\b'

$violations = [System.Collections.Generic.List[string]]::new()
function Add-Violation($rule, $file, $n, $text) {
  $rel = $file.FullName.Substring($root.Path.Length + 1)
  $violations.Add(("[{0}] {1}:{2}  {3}" -f $rule, $rel, $n, $text.Trim()))
}

foreach ($f in $markup) {
  $lines = Get-Content $f.FullName
  $isRazor = $f.Name.EndsWith('.razor')
  for ($i = 0; $i -lt $lines.Count; $i++) {
    $l = $lines[$i]; $n = $i + 1
    if ($isRazor -and $l -match '(?i)\bstyle\s*=\s*"' -and -not ($allowInline | Where-Object { $l.Contains($_) })) {
      Add-Violation 'inline-style' $f $n $l
    }
    if (-not (& $inUi $f)) {
      if ($l -match $rawMud) { Add-Violation 'raw-mud' $f $n $l }
      if ($isRazor -and $l -match '<button\b' -and $allowButton -notcontains $f.Name) { Add-Violation 'raw-button' $f $n $l }
    }
    if ($l -match '\bwm-chip\b') { Add-Violation 'retired-class' $f $n $l }
    if ($f.Name -notin @('DesignSystem.cs', 'WaymarkTheme.cs') -and $l -match '#[0-9A-Fa-f]{6}\b' -and $l -notmatch 'href="#|&#') {
      Add-Violation 'hex-colour' $f $n $l
    }
  }
}

# Rule 6: every wm-* class used must be defined in CSS.
$defined = [System.Collections.Generic.HashSet[string]]::new()
foreach ($css in Get-ChildItem $cssDir -Filter *.css) {
  foreach ($m in [regex]::Matches((Get-Content $css.FullName -Raw), '\.(wm-[a-z0-9_-]+)')) { [void]$defined.Add($m.Groups[1].Value) }
}
# Hook classes, element ids and storage keys that are never styled on their own:
# wm-term-* are element ids read by waymark.js, wm-theme is a storage key, the rest are test/layout markers.
$hooks = @('wm-edit-open', 'wm-theme', 'wm-term-prev', 'wm-term-next', 'wm-term-error', 'wm-term-wrap', 'wm-term-block',
  'wm-crumbs', 'wm-bundle-file', 'wm-home-live', 'wm-grid-status', 'wm-dashboard-grid', 'wm-ex-results', 'wm-live-row')
foreach ($f in $markup) {
  $lines = Get-Content $f.FullName
  for ($i = 0; $i -lt $lines.Count; $i++) {
    foreach ($m in [regex]::Matches($lines[$i], '(?<![\w-])(wm-[a-z0-9_-]+)')) {
      $c = $m.Groups[1].Value
      if ($c.EndsWith('-') -or $hooks -contains $c -or $defined.Contains($c)) { continue }
      if ($lines[$i] -match "@page|href=`"[^`"]*$c") { continue }
      Add-Violation 'undefined-class' $f ($i + 1) $c
    }
  }
}

if ($violations.Count -gt 0) {
  $violations | Sort-Object -Unique | ForEach-Object { Write-Host $_ }
  Write-Host ("`n{0} design-system violation(s)." -f $violations.Count) -ForegroundColor Red
  exit 1
}
Write-Host 'ui-check: clean.' -ForegroundColor Green
