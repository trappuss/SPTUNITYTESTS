# Builds the COD2EFT Inspector client plugin (spt_mod\COD2EFTInspector) against THIS PC's own SPT game DLLs
# and installs it to <SPT game>\BepInEx\plugins\COD2EFTInspector\. Nothing from the game is copied into the repo.
. (Join-Path $PSScriptRoot 'common.ps1')

try {
    $cfg  = Load-Config
    $ver  = Inspector-Version
    $proj = Join-Path $Repo 'spt_mod\COD2EFTInspector\COD2EFTInspector.csproj'

    Say "`n== 1/4  SPT game folder ==" Cyan
    $game = Get-SptGame $cfg
    Say "   $game"

    Say "`n== 2/4  .NET SDK ==" Cyan
    $dotnetDir = Join-Path $env:ProgramFiles 'dotnet'
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path (Join-Path $dotnetDir 'dotnet.exe'))) { $env:Path = "$dotnetDir;$env:Path" }
    $sdks = @()
    if (Get-Command dotnet -ErrorAction SilentlyContinue) { $sdks = @(& dotnet --list-sdks) }
    if ($sdks.Count -eq 0) {
        if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
            throw 'No .NET SDK and no winget. Install the .NET 8 SDK from https://dotnet.microsoft.com/download, then run this again.'
        }
        Say '   installing the .NET 8 SDK with winget (a one-time download, about 200 MB)' Yellow
        & winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
        $env:Path = "$dotnetDir;$env:Path"
        $sdks = @(& dotnet --list-sdks)
        if ($sdks.Count -eq 0) { throw '.NET SDK install did not work. Close this window, open a new one and run BUILD_SPT_INSPECTOR.bat again.' }
    }
    Say "   $($sdks[-1])"

    Say "`n== 3/4  Building COD2EFT Inspector v$ver ==" Cyan
    $out = Join-Path $PSScriptRoot '_build\inspector'
    $log = Join-Path $PSScriptRoot '_build\inspector_build.log'
    New-Item -ItemType Directory -Force $out | Out-Null
    $ErrorActionPreference = 'Continue'   # (PS 5.1: native stderr under Stop would abort before we can show it)
    & dotnet build $proj -c Release -nologo -v:minimal "-p:GamePath=$game" "-p:Version=$ver" -o $out |
        ForEach-Object { "$_" } | Tee-Object -FilePath $log | Where-Object { $_ -match 'error|warn|->' } | ForEach-Object { Say "   $_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $dll = Join-Path $out 'COD2EFTInspector.dll'
    if ($code -ne 0 -or -not (Test-Path $dll)) { throw "build failed (exit $code). Full log: $log - send it with SEND_RESULTS_TO_CLAUDE.bat" }
    Say '   build OK' Green

    Say "`n== 4/4  Installing into BepInEx\plugins ==" Cyan
    while (Get-Process EscapeFromTarkov -ErrorAction SilentlyContinue) {
        Read-Host '   The game is running and locks the plugin. Close the game, then press Enter' | Out-Null
    }
    $dstDir = Join-Path $game 'BepInEx\plugins\COD2EFTInspector'
    $dst = Join-Path $dstDir 'COD2EFTInspector.dll'
    New-Item -ItemType Directory -Force $dstDir | Out-Null
    if ((Test-Path $dst) -and -not (Same-File $dll $dst)) {
        $bk = Join-Path $PSScriptRoot "_backup\$(Get-Date -Format 'yyyyMMdd-HHmmss')\COD2EFTInspector"
        New-Item -ItemType Directory -Force $bk | Out-Null
        Copy-Item -LiteralPath $dst $bk -Force
        Say "   previous build backed up to $bk"
    }
    Copy-Item -LiteralPath $dll $dst -Force
    if (-not (Same-File $dll $dst)) { throw "copy to $dst did not verify" }
    Say "   $dst" Green

    $cm = Get-ChildItem (Join-Path $game 'BepInEx\plugins') -Recurse -Filter '*ConfigurationManager*.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
    Say "`n== Done ==  COD2EFT Inspector v$ver installed" Green
    Say '   In game: F9 = panel, F10 = screenshot (change them in the F12 ConfigurationManager, section "COD2EFT Inspector").'
    if (-not $cm) { Say '   (No ConfigurationManager plugin found in BepInEx\plugins; the defaults still work.)' Yellow }
    Say "   Screenshots and reports: $(Join-Path $game 'COD2EFT_Screenshots')"
    Say '   After playing, run SEND_RESULTS_TO_CLAUDE.bat: it sends the BepInEx log and the new screenshots.'
    exit 0
} catch {
    Say "`nBUILD FAILED: $($_.Exception.Message)" Red
    exit 1
}
