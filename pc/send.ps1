# Collects what Claude needs to see from this PC and pushes it to GitHub (from_pc\<time>\):
#  - Unity Editor.log (tail + errors), installed versions, which deployed files differ from the repo
#  - copies of tool files that were changed on the PC (e.g. by a Cowork session) so they can be merged
#  - the COD2EFT <-> Unity contract (COD2EFT_TEXTURE_SPEC.md) if the repo doesn't have it yet
#  - anything dragged onto SEND_RESULTS_TO_CLAUDE.bat (reports, screenshots, logs, folders)
$Extra = $args   # files / folders dragged onto the .bat
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
                $_.FullName -notmatch '\\(_dev|__pycache__|vendor)\\'
            } | ForEach-Object {
                $rel = $_.FullName.Substring($t.Dst.Length).TrimStart('\')
                if (-not (Test-Path (Join-Path $t.Src $rel)) -and $PcOwned -notcontains $_.Name) {
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

    # the contract between the two sides: bring it into the repo if it's missing or newer on the PC
    $spec = Join-Path $cfg.COD2EFT_DIR 'unity\COD2EFT_TEXTURE_SPEC.md'
    $repoSpec = Join-Path $Repo 'blender\COD2EFT\unity\COD2EFT_TEXTURE_SPEC.md'
    if ((Test-Path $spec) -and -not (Same-File $spec $repoSpec)) {
        Copy-Item -LiteralPath $spec $repoSpec -Force
        $info += 'COD2EFT_TEXTURE_SPEC.md copied into the repo (blender\COD2EFT\unity\)'
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
    Say "`nSent. Tell Claude: 'check from_pc/$stamp'" Green
    exit 0
} catch {
    Say "`nSEND FAILED: $($_.Exception.Message)" Red
    exit 1
}
