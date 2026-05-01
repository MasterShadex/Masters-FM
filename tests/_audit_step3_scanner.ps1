$ErrorActionPreference = 'Stop'
$srcRoot = "F:\Claude AI\Master FM"

# Read the frozen file list
$files = @()
Get-Content "F:\Claude AI\Master FM\AUDIT_FILELIST.md" | ForEach-Object {
    if ($_ -match '^\| \d+ \| (.+?) \| (\.\w+) \|') {
        $files += [pscustomobject]@{ Path = "$srcRoot\$($Matches[1])"; Ext = $Matches[2].ToLower(); Rel = $Matches[1] }
    }
}

function Count-Catches {
    param([string]$content, [string]$ext)
    $n = 0
    $hits = @()
    if ($ext -in @('.cs','.js')) {
        # C#/JS: any `catch (` or `catch {` or `.catch(` — not preceded by a letter
        # (to avoid matching 'fetch' inside a word). Use word boundaries.
        $m = [regex]::Matches($content, '(?<![a-zA-Z0-9_])catch\s*[\(\{]')
        $n += $m.Count
        foreach ($mm in $m) { $hits += ($content.Substring(0, $mm.Index) -split "`n").Count }
        $m2 = [regex]::Matches($content, '\.catch\s*\(')
        $n += $m2.Count
        foreach ($mm in $m2) { $hits += ($content.Substring(0, $mm.Index) -split "`n").Count }
    }
    elseif ($ext -in @('.ps1','.psm1')) {
        # PowerShell: `catch {` or `catch [Type]` — broader match including `} catch`
        $m = [regex]::Matches($content, '(?<![a-zA-Z0-9_])catch\s*[\[\{]')
        $n += $m.Count
        foreach ($mm in $m) { $hits += ($content.Substring(0, $mm.Index) -split "`n").Count }
        $m2 = [regex]::Matches($content, '(?m)^\s*trap\s*\{')
        $n += $m2.Count
        foreach ($mm in $m2) { $hits += ($content.Substring(0, $mm.Index) -split "`n").Count }
    }
    elseif ($ext -eq '.py') {
        $m = [regex]::Matches($content, '(?m)^\s*except\b')
        $n += $m.Count
        foreach ($mm in $m) { $hits += ($content.Substring(0, $mm.Index) -split "`n").Count }
    }
    elseif ($ext -eq '.html') {
        $m = [regex]::Matches($content, '(?<![a-zA-Z0-9_])catch\s*[\(\{]')
        $n += $m.Count
        foreach ($mm in $m) { $hits += ($content.Substring(0, $mm.Index) -split "`n").Count }
        $m2 = [regex]::Matches($content, '\.catch\s*\(')
        $n += $m2.Count
        foreach ($mm in $m2) { $hits += ($content.Substring(0, $mm.Index) -split "`n").Count }
    }
    elseif ($ext -in @('.bat','.cmd')) {
        $m = [regex]::Matches($content, '(?mi)^\s*if\s+errorlevel')
        $n += $m.Count
        foreach ($mm in $m) { $hits += ($content.Substring(0, $mm.Index) -split "`n").Count }
    }
    return @{ Count = $n; Lines = $hits }
}

function Count-SilentlyContinue {
    param([string]$content, [string]$ext)
    if ($ext -notin @('.ps1','.psm1')) { return 0 }
    return ([regex]::Matches($content, 'SilentlyContinue')).Count
}

$results = @()
$i = 0
foreach ($f in $files) {
    $i++
    if (-not (Test-Path $f.Path)) {
        $results += [pscustomobject]@{ Num=$i; Rel=$f.Rel; Ext=$f.Ext; Catches=0; Lines=''; Silent=0; Missing=$true }
        continue
    }
    $content = try { Get-Content -Path $f.Path -Raw -ErrorAction Stop } catch { '' }
    if (-not $content) {
        $results += [pscustomobject]@{ Num=$i; Rel=$f.Rel; Ext=$f.Ext; Catches=0; Lines=''; Silent=0; Missing=$false }
        continue
    }
    $r = Count-Catches -content $content -ext $f.Ext
    $s = Count-SilentlyContinue -content $content -ext $f.Ext
    $lineList = $r.Lines | Sort-Object -Unique
    $lines = if ($lineList.Count -gt 30) {
        (($lineList | Select-Object -First 30) -join ',') + ",...(" + $lineList.Count + " total)"
    } elseif ($lineList.Count -gt 0) {
        $lineList -join ','
    } else { '' }
    $results += [pscustomobject]@{ Num=$i; Rel=$f.Rel; Ext=$f.Ext; Catches=$r.Count; Lines=$lines; Silent=$s; Missing=$false }
}

$withCatches   = $results | Where-Object { $_.Catches -gt 0 }
$withSilent    = $results | Where-Object { $_.Silent -gt 0 }
$zero          = $results | Where-Object { $_.Catches -eq 0 -and $_.Silent -eq 0 }

Write-Host ("Files scanned: " + $results.Count)
Write-Host ("  with catch blocks: " + $withCatches.Count + " (total catches = " + (($withCatches | Measure-Object Catches -Sum).Sum) + ")")
Write-Host ("  with -ErrorAction SilentlyContinue: " + $withSilent.Count + " (total = " + (($withSilent | Measure-Object Silent -Sum).Sum) + ")")
Write-Host ("  with zero: " + $zero.Count)

$out = "F:\Claude AI\Master FM\AUDIT_CATCH_SCAN.md"
$outLines = @()
$outLines += "# Audit Catch-Block Scan"
$outLines += ""
$outLines += "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$outLines += "Total files: $($results.Count)"
$outLines += "Files with catches: $($withCatches.Count) (total = $(($withCatches | Measure-Object Catches -Sum).Sum))"
$outLines += "Files with -ErrorAction SilentlyContinue: $($withSilent.Count) (total = $(($withSilent | Measure-Object Silent -Sum).Sum))"
$outLines += "Files with neither: $($zero.Count)"
$outLines += ""
$outLines += "## Files WITH catches or silent error handling (sorted by catch count desc)"
$outLines += ""
$outLines += "| # | File | Ext | Catches | Catch lines | SilentlyContinue |"
$outLines += "|---|------|-----|---------|-------------|------------------|"
$nonZero = $results | Where-Object { $_.Catches -gt 0 -or $_.Silent -gt 0 } |
           Sort-Object @{E='Catches';Descending=$true}, @{E='Silent';Descending=$true}, Num
foreach ($r in $nonZero) {
    $outLines += ("| " + $r.Num + " | ``" + $r.Rel + "`` | " + $r.Ext + " | " + $r.Catches + " | " + $r.Lines + " | " + $r.Silent + " |")
}
$outLines += ""
$outLines += "## Files with zero catch blocks AND zero SilentlyContinue"
$outLines += ""
$outLines += "| # | File | Ext |"
$outLines += "|---|------|-----|"
foreach ($r in ($zero | Sort-Object Num)) {
    $outLines += ("| " + $r.Num + " | ``" + $r.Rel + "`` | " + $r.Ext + " |")
}
$outLines -join "`r`n" | Set-Content -Path $out -Encoding UTF8
Write-Host ("Wrote " + $out)
