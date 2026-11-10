<#
.SYNOPSIS
  Monta (ou atualiza) a pasta "levar-para-a-igreja", para copiar num pen drive e continuar o trabalho no PC da igreja.

.DESCRIPTION
  A pasta fica FORA do repositorio (ao lado dele), porque tem IPs da igreja e a senha da MA2: nunca vai para o git.
  Dentro dela:
    LEIA-ME.md        o guia (docs\LEVAR-PARA-A-IGREJA.md, com data, versao e commit preenchidos)
    1-memoria\        os arquivos de memoria do Claude deste projeto
    2-api-holyrics\   a pasta "API Holyrics" (scripts do Holyrics)
    3-instalador\     o instalador do IBT Controller da versao mais nova que estiver em artifacts\Releases
  Rodar de novo apaga o conteudo anterior da pasta e monta tudo outra vez (so dentro da pasta de destino).

.PARAMETER Destino
  Onde montar. Padrao: a pasta "levar-para-a-igreja" ao lado da pasta do repositorio. O nome da pasta precisa ser "levar-para-a-igreja".

.EXAMPLE
  powershell -File desktop\build\levar-para-a-igreja.ps1
#>
param(
  [string]$Destino
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Destino) {
  $Destino = Join-Path (Split-Path $repoRoot -Parent) 'levar-para-a-igreja'
}
$Destino = [System.IO.Path]::GetFullPath($Destino)

# Trava de seguranca: so apaga/recria dentro de uma pasta com este nome exato.
if ((Split-Path $Destino -Leaf) -ne 'levar-para-a-igreja') {
  throw "O destino precisa se chamar 'levar-para-a-igreja' (recebido: $Destino)."
}
if ($Destino.StartsWith($repoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "O destino nao pode ficar dentro do repositorio (vai ter segredos): $Destino"
}

# ---- versao e commit --------------------------------------------------------
$releases = Join-Path $repoRoot 'artifacts\Releases'
$versao = '(nenhum instalador encontrado)'
$pacote = $null
if (Test-Path $releases) {
  $pacote = Get-ChildItem $releases -Filter 'AlertaIBT-*-full.nupkg' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($pacote -and $pacote.Name -match '^AlertaIBT-(.+)-full\.nupkg$') { $versao = $Matches[1] }
}
$commit = 'desconhecido'
try { $commit = (git -C $repoRoot rev-parse --short HEAD).Trim() } catch { }
$data = Get-Date -Format 'dd/MM/yyyy HH:mm'

# ---- recria as pastas -------------------------------------------------------
foreach ($sub in '1-memoria', '2-api-holyrics', '3-instalador') {
  $p = Join-Path $Destino $sub
  if (Test-Path $p) { Remove-Item $p -Recurse -Force }
  New-Item -ItemType Directory -Path $p -Force | Out-Null
}

$avisos = @()

# ---- 1. memoria -------------------------------------------------------------
$nomeMemoria = ($repoRoot -replace '[^A-Za-z0-9]', '-')
$projetos = Join-Path $env:USERPROFILE '.claude\projects'
$pastaMemoria = $null
if (Test-Path $projetos) {
  $achada = Get-ChildItem $projetos -Directory | Where-Object { $_.Name -ieq $nomeMemoria } | Select-Object -First 1
  if ($achada) { $pastaMemoria = Join-Path $achada.FullName 'memory' }
}
if ($pastaMemoria -and (Test-Path $pastaMemoria)) {
  Copy-Item (Join-Path $pastaMemoria '*') (Join-Path $Destino '1-memoria') -Recurse -Force
} else {
  $avisos += 'Pasta de memoria do Claude nao encontrada (1-memoria ficou vazia).'
}

# ---- 2. API Holyrics ---------------------------------------------------------
$holyrics = Join-Path $repoRoot 'API Holyrics'
if (Test-Path $holyrics) {
  Copy-Item (Join-Path $holyrics '*') (Join-Path $Destino '2-api-holyrics') -Recurse -Force
} else {
  $avisos += "Pasta 'API Holyrics' nao encontrada (2-api-holyrics ficou vazia)."
}

# ---- 3. instalador -----------------------------------------------------------
$setup = Join-Path $releases 'AlertaIBT-win-Setup.exe'
if ($pacote -and (Test-Path $setup)) {
  Copy-Item $setup (Join-Path $Destino '3-instalador') -Force
} else {
  $avisos += 'Instalador nao encontrado em artifacts\Releases (3-instalador ficou vazia).'
}

# ---- LEIA-ME -----------------------------------------------------------------
$modelo = Join-Path $repoRoot 'docs\LEVAR-PARA-A-IGREJA.md'
$texto = [System.IO.File]::ReadAllText($modelo, [System.Text.Encoding]::UTF8)
$texto = $texto.Replace('{{DATA}}', $data).Replace('{{VERSAO}}', $versao).Replace('{{COMMIT}}', $commit)
[System.IO.File]::WriteAllText((Join-Path $Destino 'LEIA-ME.md'), $texto, (New-Object System.Text.UTF8Encoding($true)))

# ---- resumo ------------------------------------------------------------------
Write-Host ''
Write-Host "[levar-para-a-igreja] pronto: $Destino"
Write-Host "  versao do app : $versao   commit: $commit   gerado em: $data"
Get-ChildItem $Destino -Recurse -File | ForEach-Object {
  '  {0,12:N0} bytes  {1}' -f $_.Length, $_.FullName.Substring($Destino.Length + 1)
} | Write-Host
foreach ($a in $avisos) { Write-Host "  AVISO: $a" }
