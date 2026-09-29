# Shared helpers for sync.ps1 / send.ps1 (Windows PowerShell 5.1 compatible).
$ErrorActionPreference = 'Stop'
$Repo   = Split-Path -Parent $PSScriptRoot
$Branch = 'claude/bold-mayer-11fzxj'
$ConfigFile = Join-Path $PSScriptRoot 'config.local.txt'   # per-PC, not in git

# Files that belong to the user's PC, never overwritten by a sync (and not reported as "changed on PC").
$PcOwned = @('blender_path.txt', 'template_path.txt', 'wtt_path.txt', 'cod2eft_bonemap.json',
             'cod2eft_pose_tweaks.json', 'COD2EFT_Blender_Addon.zip')

function Say($msg, $color = 'Gray') { Write-Host $msg -ForegroundColor $color }

function Load-Config {
    $cfg = [ordered]@{
        COD2EFT_DIR   = 'C:\Users\notso\Downloads\Claude Current\SPTModdingTools\COD2EFT'
        UNITY_PROJECT = 'C:\Users\notso\Desktop\RIP\EFT2\Tools\WTT-SDK-2022'
        EDITOR_LOG    = (Join-Path $env:LOCALAPPDATA 'Unity\Editor\Editor.log')
    }
    if (Test-Path $ConfigFile) {
        foreach ($line in Get-Content $ConfigFile) {
            if ($line -match '^\s*([A-Z0-9_]+)\s*=\s*(.+?)\s*$') { $cfg[$Matches[1]] = $Matches[2].Trim('"') }
        }
    }
    $asks = @{
        COD2EFT_DIR   = 'your COD2EFT folder (the one with cod2eft_porter.py)'
        UNITY_PROJECT = 'your WTT-SDK Unity project folder (the one with Assets inside)'
    }
    foreach ($k in $asks.Keys) {
        $ok = if ($k -eq 'UNITY_PROJECT') { Test-Path (Join-Path $cfg[$k] 'Assets') } else { Test-Path $cfg[$k] }
        while (-not $ok) {
            Say "Not found: $($cfg[$k])" Yellow
            $cfg[$k] = (Read-Host "Drag $($asks[$k]) here and press Enter").Trim('"', ' ')
            $ok = if ($k -eq 'UNITY_PROJECT') { Test-Path (Join-Path $cfg[$k] 'Assets') } else { Test-Path $cfg[$k] }
        }
    }
    ($cfg.Keys | ForEach-Object { "$_=$($cfg[$_])" }) | Set-Content -Encoding UTF8 $ConfigFile
    return $cfg
}

function Invoke-Git([string[]]$argv) {
    & git -C $Repo @argv
    if ($LASTEXITCODE -ne 0) { throw "git $($argv -join ' ') failed (exit $LASTEXITCODE)" }
}

# Pairs of (repo source folder, PC destination folder) that the sync deploys.
function Get-Targets($cfg) {
    $editor = Join-Path $cfg.UNITY_PROJECT 'Assets\Editor'
    @(
        @{ Label = 'COD2EFT (Blender)'; Src = (Join-Path $Repo 'blender\COD2EFT');
           Dst = $cfg.COD2EFT_DIR; Skip = @('unity\README.md') },
        @{ Label = 'EFT Tools (Unity)'; Src = (Join-Path $Repo 'unity\EFTAutoPrefabber');
           Dst = (Join-Path $editor 'EFTAutoPrefabber'); Skip = @() },
        @{ Label = 'EFT Tools folder .meta'; Src = (Join-Path $Repo 'unity');
           Dst = $editor; Skip = @(); Only = @('EFTAutoPrefabber.meta') }
    )
}

# Files of a target as relative paths (repo side).
function Get-TargetFiles($t) {
    if ($t.Only) { return $t.Only | Where-Object { Test-Path (Join-Path $t.Src $_) } }
    Get-ChildItem -Path $t.Src -Recurse -File | ForEach-Object {
        $_.FullName.Substring($t.Src.Length).TrimStart('\')
    } | Where-Object {
        ($PcOwned -notcontains (Split-Path $_ -Leaf)) -and ($t.Skip -notcontains $_) -and ($_ -notmatch '__pycache__')
    }
}

function Same-File($a, $b) {
    if (-not (Test-Path $b)) { return $false }
    (Get-FileHash $a -Algorithm SHA256).Hash -eq (Get-FileHash $b -Algorithm SHA256).Hash
}

function Repo-Versions {
    $porter = Get-Content (Join-Path $Repo 'blender\COD2EFT\cod2eft_porter.py') -Raw
    $tools  = Get-Content (Join-Path $Repo 'unity\EFTAutoPrefabber\EFTToolsVersion.cs') -Raw
    $b = if ($porter -match '(?m)^VERSION = \((\d+), (\d+), (\d+)\)') { "$($Matches[1]).$($Matches[2]).$($Matches[3])" } else { '?' }
    $u = if ($tools -match 'Version = "([^"]+)"') { $Matches[1] } else { '?' }
    @{ Blender = $b; Unity = $u }
}
