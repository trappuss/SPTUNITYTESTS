# Pulls the latest tools from GitHub and deploys them to the Blender (COD2EFT) and Unity folders.
# Every file it replaces is backed up first to pc\_backup\<time>\. It never deletes anything.
. (Join-Path $PSScriptRoot 'common.ps1')

try {
    $cfg = Load-Config

    Say "`n== 1/4  Getting the latest from GitHub ($Branch) ==" Cyan
    $dirty = Repair-Repo
    if ($dirty) {
        Say '   These files in the repo folder were edited on this PC:' Yellow
        $dirty | ForEach-Object { Say "     $_" Yellow }
        throw 'run SEND_RESULTS_TO_CLAUDE.bat first (it sends those edits to Claude), then sync again'
    }
    Invoke-Git @('fetch', 'origin', $Branch)
    $cur = (& git -C $Repo rev-parse --abbrev-ref HEAD).Trim()
    if ($cur -ne $Branch) { Invoke-Git @('checkout', $Branch) }
    Invoke-Git @('pull', '--rebase', 'origin', $Branch)
    Say ("   now at: " + (& git -C $Repo log -1 --format='%h %s (%cr)'))

    Say "`n== 2/4  Copying changed files ==" Cyan
    $stamp  = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backup = Join-Path $PSScriptRoot "_backup\$stamp"
    $changed = @()
    foreach ($t in Get-Targets $cfg) {
        if (-not (Test-Path $t.Dst)) { New-Item -ItemType Directory -Force $t.Dst | Out-Null }
        foreach ($rel in Get-TargetFiles $t) {
            $src = Join-Path $t.Src $rel; $dst = Join-Path $t.Dst $rel
            if (Same-File $src $dst) { continue }
            if (Test-Path $dst) {
                $bk = Join-Path $backup (Join-Path ($t.Label -replace '[^A-Za-z0-9]', '_') $rel)
                New-Item -ItemType Directory -Force (Split-Path $bk) | Out-Null
                Copy-Item -LiteralPath $dst $bk -Force
                Say "   updated  $($t.Label): $rel" Green
            } else {
                New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
                Say "   new      $($t.Label): $rel" Green
            }
            Copy-Item -LiteralPath $src $dst -Force
            $changed += $dst
        }
    }
    if ($changed.Count -eq 0) { Say '   everything was already up to date' }
    elseif (Test-Path $backup) { Say "   replaced files backed up to $backup" }

    Say "`n== 3/4  Verifying (SHA-256, repo vs PC) ==" Cyan
    $bad = 0
    foreach ($t in Get-Targets $cfg) {
        foreach ($rel in Get-TargetFiles $t) {
            if (-not (Same-File (Join-Path $t.Src $rel) (Join-Path $t.Dst $rel))) { $bad++; Say "   MISMATCH $rel" Red }
        }
    }
    if ($bad) { throw "$bad file(s) did not copy correctly" }
    Say '   all files match' Green

    Say "`n== 4/4  Blender add-on ==" Cyan
    $v = Repo-Versions
    $initChanged = $changed | Where-Object { $_ -like '*\addon_init.py' -or $_ -like '*\install_addon.py' }
    if ($initChanged) {
        if (Get-Process blender -ErrorAction SilentlyContinue) {
            Say '   The add-on itself changed, but Blender is open. Close Blender, then run SYNC_TO_MY_PC.bat again.' Yellow
        } else {
            $bp = Join-Path $cfg.COD2EFT_DIR 'blender_path.txt'
            $blender = if (Test-Path $bp) { (Get-Content $bp -TotalCount 1).Trim().Trim('"') } else { '' }
            if ($blender -and (Test-Path $blender)) {
                Say "   re-installing the add-on with $blender"
                & $blender --background --python-exit-code 1 --python (Join-Path $cfg.COD2EFT_DIR 'install_addon.py') |
                    Where-Object { $_ -match '\[COD2EFT\]' } | ForEach-Object { Say "   $_" }
                if ($LASTEXITCODE -ne 0) { throw 'add-on install failed' }
            } else {
                Say '   Run Install_COD2EFT_Addon.bat in your COD2EFT folder once (it finds Blender).' Yellow
            }
        }
    } else {
        Say '   live install: restart Blender (or press Reload in the COD2EFT panel) to use the new code'
    }

    Say "`n== Done ==  COD2EFT v$($v.Blender)   EFT Tools v$($v.Unity)" Green
    if ($changed | Where-Object { $_ -like '*EFTAutoPrefabber*' }) {
        Say "`nClick into the Unity window now so it recompiles. Waiting up to 2 minutes for it..." Cyan
        $log = $cfg.EDITOR_LOG; $t0 = Get-Date
        $start = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
        while (((Get-Date) - $t0).TotalSeconds -lt 120) {
            Start-Sleep -Seconds 3
            if (-not (Test-Path $log)) { continue }
            $fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
            try {
                if ($fs.Length -lt $start) { $start = 0 }
                $fs.Seek($start, 'Begin') | Out-Null
                $new = (New-Object IO.StreamReader($fs)).ReadToEnd()
            } finally { $fs.Close() }
            $errs = [regex]::Matches($new, '(?m)^.*error CS\d+.*$') | ForEach-Object { $_.Value } | Select-Object -Unique
            if ($errs) { Say "   Unity compile errors:" Red; $errs | ForEach-Object { Say "   $_" Red }; Say '   Run SEND_RESULTS_TO_CLAUDE.bat so Claude can see them.' Yellow; break }
            if ($new -match "\[EFT Tools\] v$([regex]::Escape($v.Unity)) ") { Say "   Unity compiled and loaded EFT Tools v$($v.Unity)" Green; break }
        }
    }
    exit 0
} catch {
    Say "`nSYNC FAILED: $($_.Exception.Message)" Red
    exit 1
}
