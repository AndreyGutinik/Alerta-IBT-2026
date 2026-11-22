<#
  Hook "Stop" do Claude Code: quando o Claude vai terminar a resposta, confere o git.
  Se houver alteracao sem commit ou commit sem push (ignorando a pasta "API Holyrics", que nao
  vai para o git), ele AVISA o Claude (exit 2) para commitar e dar push antes de parar.
  So avisa uma vez por rodada (stop_hook_active evita laco). Nao grava nada.
#>
$ErrorActionPreference = 'SilentlyContinue'
$entrada = [Console]::In.ReadToEnd()
try { $json = $entrada | ConvertFrom-Json } catch { $json = $null }
if ($json -and $json.stop_hook_active) { exit 0 }

$raiz = if ($json -and $json.cwd) { $json.cwd } else { (Get-Location).Path }
$null = git -C $raiz rev-parse --is-inside-work-tree 2>$null
if ($LASTEXITCODE -ne 0) { exit 0 }

$sujo = @(git -C $raiz status --porcelain 2>$null | Where-Object { $_ -notmatch 'API Holyrics' })
$adiante = 0
$contagem = git -C $raiz rev-list --count '@{u}..HEAD' 2>$null
if ($LASTEXITCODE -eq 0 -and $contagem) { $adiante = [int]$contagem }

if ($sujo.Count -eq 0 -and $adiante -eq 0) { exit 0 }

$msg = "GIT: ha trabalho que NAO esta salvo no GitHub. "
if ($sujo.Count -gt 0) { $msg += "$($sujo.Count) arquivo(s) alterado(s) sem commit: " + (($sujo | Select-Object -First 8) -join '; ') + ". " }
if ($adiante -gt 0) { $msg += "$adiante commit(s) sem push. " }
$msg += "Faca commit e push agora (ou diga ao dono, em uma frase, por que nao). Lembre tambem de copiar para o pen drive o que nao vai para o git (API Holyrics, memorias)."
[Console]::Error.WriteLine($msg)
exit 2
