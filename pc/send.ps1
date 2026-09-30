# Collects what Claude needs to see from this PC and pushes it to GitHub (from_pc\<time>\):
#  - Unity Editor.log (tail + errors), installed versions, which deployed files differ from the repo
#  - copies of tool files that were changed on the PC (e.g. by a Cowork session) so they can be merged
#  - anything dragged onto SEND_RESULTS_TO_CLAUDE.bat (reports, screenshots, logs, folders)
#  - COD2EFT conversion reports (<COD2EFT folder>\reports\*.txt) that are new since the last send
#  - SPT (if SPT_GAME is known): BepInEx LogOutput.log, the Inspector build log, and every file in
#    <SPT game>\COD2EFT_Screenshots that is new since the last send
$Extra = $args   # files / folders dragged onto the .bat

# JPEG copy of a big PNG, at most 2560 px wide (System.Drawing, part of Windows PowerShell 5.1). $false if it can't.
function Save-JpegCopy($src, $dst) {
    try {
        Add-Type -AssemblyName System.Drawing
        $img = [System.Drawing.Image]::FromFile($src)
        try {
            $w = [Math]::Min(2560, $img.Width); $h = [int][Math]::Round($img.Height * $w / $img.Width)
            $bmp = New-Object System.Drawing.Bitmap($w, $h)
            $g = [System.Drawing.Graphics]::FromImage($bmp)
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.DrawImage($img, 0, 0, $w, $h)
            $g.Dispose()
            $codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
            $ep = New-Object System.Drawing.Imaging.EncoderParameters(1)
            $ep.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long]90)
            $bmp.Save($dst, $codec, $ep)
            $bmp.Dispose()
        } finally { $img.Dispose() }
        return $true
    } catch {
        Say "   (JPEG copy failed for $(Split-Path $src -Leaf): $($_.Exception.Message); sending the PNG)" Yellow
        return $false
    }
}
. (Join-Path $PSScriptRoot 'common.ps1')
$MaxBytes = 90MB

try {
    $cfg   = Load-Config
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $out   = Join-Path $Repo "from_pc\$stamp"
    New-Item -ItemType Directory -Force $out | Out-Null
    $v = Repo-Versions
    $info = @("Sent: $(Get-Date -Format s)", "Repo commit: $(& git -C $Repo log -1 --format='%h %s')",
              "Repo versions: COD2EFT v$($v.Blender), EFT Tools v$($v.Unity)", '')

    Say '== Unity Editor.log ==' Cyan
    $log = $cfg.EDITOR_LOG
    if (Test-Path $log) {
        $fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
        try { $lines = (New-Object IO.StreamReader($fs)).ReadToEnd() -split "`r?`n" } finally { $fs.Close() }
        $lines | Select-Object -Last 4000 | Set-Content -Encoding UTF8 (Join-Path $out 'Editor_log_tail.txt')
        $lines | Select-String -Pattern 'error CS|Exception|\[EFT Tools\]|\[EFT|NullReference|Error building' |
            ForEach-Object { $_.Line } | Select-Object -Last 400 | Set-Content -Encoding UTF8 (Join-Path $out 'Editor_log_highlights.txt')
        $comp = $lines | Select-String -Pattern '\[EFT Tools\] v\S+ ' | Select-Object -Last 1
        $info += "Unity last loaded: $(if ($comp) { $comp.Line.Trim() } else { 'no [EFT Tools] line in Editor.log' })"
        Say '   collected'
    } else { $info += "Editor.log not found at $log" }

    Say '== SPT game: BepInEx log + COD2EFT Inspector screenshots ==' Cyan
    $sentAt = Get-Date
    $marker = Join-Path $PSScriptRoot '_state\screenshots_last_send.txt'
    $game = Get-SptGame $cfg $false
    if (-not $game) { $info += 'SPT game folder not known yet (BUILD_SPT_INSPECTOR.bat asks for it once)' }
    else {
        $info += "SPT game: $game"
        $bl = Join-Path $game 'BepInEx\LogOutput.log'
        if (Test-Path $bl) {
            $fs = [IO.File]::Open($bl, 'Open', 'Read', 'ReadWrite')
            try { $lines = (New-Object IO.StreamReader($fs)).ReadToEnd() -split "`r?`n" } finally { $fs.Close() }
            $lines | Select-Object -Last 20000 | Set-Content -Encoding UTF8 (Join-Path $out 'BepInEx_LogOutput.log')
            $ins = $lines | Select-String -SimpleMatch 'COD2EFT Inspector' | Select-Object -Last 1
            $info += "BepInEx log: $((Get-Item $bl).LastWriteTime.ToString('s')), Inspector: $(if ($ins) { 'present' } else { 'no COD2EFT Inspector lines' })"
            Say '   BepInEx LogOutput.log collected'
        } else { $info += "BepInEx log not found: $bl" }
        $dll = Join-Path $game 'BepInEx\plugins\COD2EFTInspector\COD2EFTInspector.dll'
        $info += "Inspector installed: $(if (Test-Path $dll) { 'v' + (Get-Item $dll).VersionInfo.FileVersion + ', built ' + (Get-Item $dll).LastWriteTime.ToString('s') } else { 'no' }) (repo v$(Inspector-Version))"
        $bld = Join-Path $PSScriptRoot '_build\inspector_build.log'
        if (Test-Path $bld) { Copy-Item -LiteralPath $bld (Join-Path $out 'inspector_build.log') }
        $shots = Join-Path $game 'COD2EFT_Screenshots'
        if (Test-Path $shots) {
            $since = if (Test-Path $marker) { [datetime]::Parse((Get-Content $marker -TotalCount 1).Trim(), [Globalization.CultureInfo]::InvariantCulture) } else { [datetime]::MinValue }
            $new = @(Get-ChildItem $shots -File | Where-Object { $_.LastWriteTime -gt $since })
            $dstS = Join-Path $out 'COD2EFT_Screenshots'
            $jpgs = 0
            foreach ($f in $new) {
                New-Item -ItemType Directory -Force $dstS | Out-Null
                # big screenshots go up as a JPEG copy (<= 2560 px wide): keeps the repo small; the PNG stays on this PC
                if ($f.Extension -eq '.png' -and $f.Length -gt 4MB -and (Save-JpegCopy $f.FullName (Join-Path $dstS ($f.BaseName + '.jpg')))) { $jpgs++; continue }
                if ($f.Length -gt $MaxBytes) { $info += "SKIPPED (over 90 MB, use a lower supersize): $($f.Name)"; continue }
                Copy-Item -LiteralPath $f.FullName $dstS
            }
            if ($jpgs) { $info += "$jpgs screenshot(s) sent as JPEG copies (max 2560 px wide, quality 90); the PNGs stay in $shots" }
            $info += "COD2EFT_Screenshots: $($new.Count) new file(s) since $(if ($since -eq [datetime]::MinValue) { 'ever' } else { $since.ToString('s') })"
            Say "   $($new.Count) new screenshot / report file(s)"
        }
    }

    # COD2EFT reports (2.6.7+: every panel conversion saves <COD2EFT folder>\reports\<name>_report.txt)
    $rmark = Join-Path $PSScriptRoot '_state\reports_last_send.txt'
    $rdir = Join-Path $cfg.COD2EFT_DIR 'reports'
    if (Test-Path $rdir) {
        $rsince = if (Test-Path $rmark) { [datetime]::Parse((Get-Content $rmark -TotalCount 1).Trim(), [Globalization.CultureInfo]::InvariantCulture) } else { [datetime]::MinValue }
        $newR = @(Get-ChildItem $rdir -File -Filter '*.txt' | Where-Object { $_.LastWriteTime -gt $rsince -and $_.Length -lt $MaxBytes })
        if ($newR.Count) {
            $dstR = Join-Path $out 'COD2EFT_reports'
            New-Item -ItemType Directory -Force $dstR | Out-Null
            foreach ($f in $newR) { Copy-Item -LiteralPath $f.FullName $dstR }
        }
        $info += "COD2EFT reports: $($newR.Count) new since $(if ($rsince -eq [datetime]::MinValue) { 'ever' } else { $rsince.ToString('s') })"
        Say "   $($newR.Count) new COD2EFT report(s)"
    }

    Say '== Deployed files vs repo ==' Cyan
    $pcDir = Join-Path $out 'changed_on_pc'
    foreach ($t in Get-Targets $cfg) {
        foreach ($rel in Get-TargetFiles $t) {
            $src = Join-Path $t.Src $rel; $dst = Join-Path $t.Dst $rel
            if (-not (Test-Path $dst)) { $info += "MISSING on PC  [$($t.Label)] $rel"; continue }
            if (Same-File $src $dst) { continue }
            if (Is-OldVersion $src $dst) { $info += "OLDER on PC    [$($t.Label)] $rel  (not synced yet - run SYNC_TO_MY_PC.bat)"; continue }
            $info += "DIFFERS on PC  [$($t.Label)] $rel  (PC copy modified $((Get-Item $dst).LastWriteTime.ToString('s')))"
            $c = Join-Path $pcDir (Join-Path ($t.Label -replace '[^A-Za-z0-9]', '_') $rel)
            New-Item -ItemType Directory -Force (Split-Path $c) | Out-Null
            Copy-Item -LiteralPath $dst $c
        }
        # files on the PC that the repo doesn't have (skip big / generated stuff)
        if (-not $t.Only -and (Test-Path $t.Dst)) {
            Get-ChildItem $t.Dst -Recurse -File -ErrorAction SilentlyContinue | Where-Object {
                $_.Length -lt 2MB -and $_.Extension -in '.py', '.cs', '.md', '.bat', '.txt', '.json', '.meta' -and
                $_.FullName -notmatch '\\(_dev|__pycache__|vendor|reports)\\'
            } | ForEach-Object {
                $rel = $_.FullName.Substring($t.Dst.Length).TrimStart('\')
                if (-not (Test-Path (Join-Path $t.Src $rel)) -and $PcOwned -notcontains $_.Name) {
                    if (Is-OldVersion (Join-Path $t.Src $rel) $_.FullName) {
                        $info += "RETIRED on PC  [$($t.Label)] $rel  (the repo dropped it; SYNC_TO_MY_PC.bat moves it to the backup)"
                        return
                    }
                    $info += "ONLY on PC     [$($t.Label)] $rel"
                    $c = Join-Path $pcDir (Join-Path ($t.Label -replace '[^A-Za-z0-9]', '_') $rel)
                    New-Item -ItemType Directory -Force (Split-Path $c) | Out-Null
                    Copy-Item -LiteralPath $_.FullName $c
                }
            }
        }
    }
    foreach ($f in 'blender_path.txt', 'template_path.txt', 'wtt_path.txt') {
        $p = Join-Path $cfg.COD2EFT_DIR $f
        if (Test-Path $p) { $info += "$f = $((Get-Content $p -TotalCount 1).Trim())" }
    }

    if ($Extra) {
        Say '== Your files ==' Cyan
        $dst = Join-Path $out 'attached'
        New-Item -ItemType Directory -Force $dst | Out-Null
        foreach ($e in $Extra) {
            $items = if (Test-Path $e -PathType Container) { Get-ChildItem $e -Recurse -File } else { Get-Item -LiteralPath $e }
            $base  = if (Test-Path $e -PathType Container) { Split-Path (Resolve-Path $e) -Parent } else { Split-Path $e -Parent }
            foreach ($i in $items) {
                if ($i.Length -gt $MaxBytes) { $info += "SKIPPED (over 90 MB): $($i.FullName)"; continue }
                if ($i.Extension -in '.blend', '.blend1') { $info += "SKIPPED (.blend): $($i.FullName)"; continue }
                $c = Join-Path $dst $i.FullName.Substring($base.Length).TrimStart('\')
                New-Item -ItemType Directory -Force (Split-Path $c) | Out-Null
                Copy-Item -LiteralPath $i.FullName $c
                Say "   $($i.Name)"
            }
        }
    }

    $note = Read-Host "`nA message for Claude (what you tried / what you saw). Enter to skip"
    if ($note) { $note | Set-Content -Encoding UTF8 (Join-Path $out 'message.txt') }
    $info | Set-Content -Encoding UTF8 (Join-Path $out 'pc_state.txt')
    $info | Select-Object -Skip 3 | ForEach-Object { Say "   $_" }
    $total = (Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum
    if ($total -gt 300MB) { Say "   warning: $([math]::Round($total / 1MB)) MB to upload" Yellow }

    Say "`n== Uploading to GitHub ==" Cyan
    & git -C $Repo config user.email | Out-Null
    if ($LASTEXITCODE -ne 0) { Invoke-Git @('config', 'user.name', 'trappuss'); Invoke-Git @('config', 'user.email', 'notsomlgally@gmail.com') }
    $null = Repair-Repo
    Invoke-Git @('add', '-A')          # from_pc, the spec, and any real edits made in the repo folder
    Invoke-Git @('commit', '-q', '-m', "PC results $stamp")
    Invoke-Git @('fetch', '-q', 'origin', $Branch)
    $ErrorActionPreference = 'Continue'
    & git -C $Repo rebase -q "origin/$Branch"
    if ($LASTEXITCODE -eq 0) {
        Invoke-Git @('push', '-q', 'origin', $Branch)
    } else {
        # Claude changed the same file meanwhile: don't fight it - park the results on their own branch
        & git -C $Repo rebase --abort | Out-Null
        $side = "pc-results/$stamp"
        Invoke-Git @('push', '-q', 'origin', "HEAD:refs/heads/$side")
        Invoke-Git @('reset', '-q', '--hard', "origin/$Branch")
        Say "   (conflict with Claude's newer work - results were pushed to branch $side instead)" Yellow
    }
    New-Item -ItemType Directory -Force (Split-Path $marker) | Out-Null
    $sentAt.ToString('o') | Set-Content -Encoding ASCII $rmark
    $sentAt.ToString('o') | Set-Content -Encoding ASCII $marker    # next send only takes screenshots newer than this
    Say "`nSent. Tell Claude: 'check from_pc/$stamp'" Green
    exit 0
} catch {
    Say "`nSEND FAILED: $($_.Exception.Message)" Red
    exit 1
}
