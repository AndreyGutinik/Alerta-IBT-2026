<#
.SYNOPSIS
  Builds the Alerta-IBT installer (Setup.exe) and the update packages with Velopack. Does NOT publish anything.

.DESCRIPTION
  1. dotnet publish: self-contained, ReadyToRun, win-x64 (the church PC needs no .NET installed).
  2. vpk pack: Setup.exe, the full package and (when an earlier release is in the folder) a delta package.
  3. Prints where everything is.

  Output (both ignored by git):
    artifacts\publish    the published program
    artifacts\Releases   AlertaIBT-win-Setup.exe, AlertaIBT-<version>-win-full.nupkg, releases.win.json, ...

  Uploading to GitHub Releases is a separate, deliberate step (the release workflow does it on a v*.*.* tag, or run
  `dotnet vpk upload github` by hand). This script never uploads.

.PARAMETER Version
  The version to pack (x.y.z, or x.y.z-pre.N for a pre-release). Default: the <Version> of AlertaIBT.App.csproj (8.0.0).

.PARAMETER DownloadPrevious
  Download the latest release of the repository into artifacts\Releases first, so vpk can build a small delta package.
  Needs internet. Off by default.

.EXAMPLE
  pwsh desktop/build/pack.ps1
#>
param(
  [string]$Version,
  [switch]$DownloadPrevious
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoUrl = 'https://github.com/AndreyGutinik/Alerta-IBT-2026'
$PackId = 'AlertaIBT'
$MainExe = 'AlertaIBT.exe'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $repoRoot 'desktop\src\AlertaIBT.App\AlertaIBT.App.csproj'
$publishDir = Join-Path $repoRoot 'artifacts\publish'
$releasesDir = Join-Path $repoRoot 'artifacts\Releases'
$icon = Join-Path $repoRoot 'assets\icons\app.ico'

function Invoke-Native {
  param([string]$What, [scriptblock]$Command)
  & $Command
  if ($LASTEXITCODE -ne 0) { throw "$What falhou (codigo $LASTEXITCODE)." }
}

Push-Location $repoRoot
try {
  $csprojVersion = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
  if (-not $Version) { $Version = $csprojVersion }
  if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$') { throw "Versao invalida: '$Version' (use x.y.z ou x.y.z-pre.N)." }
  Write-Host "[pack] Alerta-IBT $Version (csproj: $csprojVersion)" -ForegroundColor Cyan

  Invoke-Native 'dotnet tool restore' { dotnet tool restore }

  if (Test-Path -LiteralPath $publishDir) { Remove-Item -LiteralPath $publishDir -Recurse -Force }
  Write-Host '[pack] publicando (self-contained, ReadyToRun, win-x64)...' -ForegroundColor Cyan
  Invoke-Native 'dotnet publish' {
    dotnet publish $project -c Release -r win-x64 --self-contained true `
      -p:PublishReadyToRun=true -p:Version=$Version -o $publishDir
  }
  if (-not (Test-Path -LiteralPath (Join-Path $publishDir $MainExe))) { throw "$MainExe nao foi publicado em $publishDir." }

  # Start clean: vpk refuses to pack a version that is already in the folder.
  if (Test-Path -LiteralPath $releasesDir) { Get-ChildItem -LiteralPath $releasesDir -Force | Remove-Item -Recurse -Force }  # esvazia a pasta (apagar a pasta falha se algum terminal estiver aberto nela)
  New-Item -ItemType Directory -Force -Path $releasesDir | Out-Null
  if ($DownloadPrevious) {
    Write-Host '[pack] baixando a versao anterior para gerar o delta...' -ForegroundColor Cyan
    # The first v8 release has no earlier Velopack release to download (the v7 release is Electron): a full package is enough.
    dotnet vpk download github --repoUrl $RepoUrl --outputDir $releasesDir
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Nao foi possivel baixar a versao anterior; seguindo sem delta.' }
  }

  Write-Host '[pack] empacotando com vpk...' -ForegroundColor Cyan
  Invoke-Native 'vpk pack' {
    dotnet vpk pack --packId $PackId --packVersion $Version --packDir $publishDir --mainExe $MainExe `
      --packTitle 'Alerta-IBT' --packAuthors 'IBT' --icon $icon --runtime win-x64 --outputDir $releasesDir
  }

  Write-Host ''
  Write-Host '[pack] pronto. Arquivos:' -ForegroundColor Green
  Get-ChildItem -LiteralPath $releasesDir -File | ForEach-Object {
    '{0,12:N0} bytes  {1}' -f $_.Length, $_.FullName
  }
  Write-Host ''
  Write-Host "Instalador: $(Join-Path $releasesDir "$PackId-win-Setup.exe")"
  Write-Host 'Nada foi publicado. Para publicar: crie a tag vX.Y.Z (o workflow envia) ou use "dotnet vpk upload github".'
}
finally {
  Pop-Location
}
