<#
.SYNOPSIS
    Publica o site do Alerta IBT no Firebase em etapas (para o dono rodar no proprio terminal).

.DESCRIPTION
    Etapa 1: projeto, app da Web, banco de dados e web/.env (e mostra os cliques que a CLI nao faz).
    Etapa 2: build, deploy do Hosting e das regras, conta do PC e credenciais.
    Etapa 3: torna uma conta do site administradora.

    Cada etapa pode ser repetida: o que ja existe e detectado e pulado. Antes de mudar qualquer coisa na nuvem o script
    pergunta "Isto vai alterar o projeto ... Continuar? (s/N)". Nenhum token e impresso e nenhum segredo vai para arquivo
    versionado (web/.env e artifacts/ ficam fora do git).

.PARAMETER Etapa
    1, 2 ou 3.

.PARAMETER Admin
    Etapa 3: o e-mail (da conta Google) que vira administrador.

.PARAMETER DryRun
    Mostra os comandos e o que seria escrito, sem rodar nada e sem acessar a rede.

.PARAMETER Projeto
    Id do projeto do Firebase (padrao: alertaibt).

.PARAMETER EmailPc
    E-mail da conta do PC (padrao: pc-telao@alertaibt.web.app).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\publicar-site.ps1 -Etapa 1 -DryRun
    powershell -ExecutionPolicy Bypass -File scripts\publicar-site.ps1 -Etapa 2
    powershell -ExecutionPolicy Bypass -File scripts\publicar-site.ps1 -Etapa 3 -Admin dono@gmail.com
#>
[CmdletBinding()]
param(
    [ValidateSet(1, 2, 3)]
    [int]$Etapa = 0,

    [string]$Admin = '',

    [switch]$DryRun,

    [string]$Projeto = 'alertaibt',

    [string]$EmailPc = 'pc-telao@alertaibt.web.app'
)

$ErrorActionPreference = 'Continue'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$FirebaseTools = 'firebase-tools@14.27.0'
$RaizRepo = Split-Path -Parent $PSScriptRoot
$PastaWeb = Join-Path $RaizRepo 'web'
$ArquivoEnv = Join-Path $PastaWeb '.env'
$PastaArtefatos = Join-Path $RaizRepo 'artifacts'
$ArquivoCredenciais = Join-Path $PastaArtefatos 'credenciais-pc.txt'
$Instancia = "$Projeto-default-rtdb"
$script:Confirmado = $false

# ---------------------------------------------------------------- utilidades

function Escrever-Titulo([string]$texto) {
    Write-Host ''
    Write-Host "== $texto" -ForegroundColor Cyan
}

function Escrever-Passo([string]$texto) { Write-Host "  - $texto" }

function Escrever-Ok([string]$texto) { Write-Host "  ok: $texto" -ForegroundColor Green }

function Escrever-Pulado([string]$texto) { Write-Host "  ja feito, pulando: $texto" -ForegroundColor DarkGray }

function Abortar([string]$mensagem) {
    Write-Host ''
    Write-Host "ERRO: $mensagem" -ForegroundColor Red
    Write-Host 'Nada mais foi feito. Corrija e rode a etapa de novo (ela pula o que ja esta pronto).' -ForegroundColor Red
    exit 1
}

# Pergunta uma vez por execucao, logo antes da primeira mudanca na nuvem.
function Confirmar-Mudanca {
    if ($script:Confirmado) { return }
    $pergunta = "Isto vai alterar o projeto $Projeto. Continuar? (s/N)"
    if ($DryRun) {
        Write-Host "  [simulação] $pergunta  (responderia: s)" -ForegroundColor DarkGray
        $script:Confirmado = $true
        return
    }
    $resposta = Read-Host $pergunta
    if ($resposta -notmatch '^[sSyY]') {
        Write-Host 'Cancelado. Nada foi alterado.' -ForegroundColor Yellow
        exit 0
    }
    $script:Confirmado = $true
}

function Obter-Npx {
    if (Get-Command 'npx.cmd' -ErrorAction SilentlyContinue) { return 'npx.cmd' }
    if (Get-Command 'npx' -ErrorAction SilentlyContinue) { return 'npx' }
    return $null
}

# Roda `npx firebase-tools@14.27.0 ...`. Devolve @{ Codigo; Texto }. Em -DryRun so mostra o comando.
function Invoke-Firebase {
    param(
        [Parameter(Mandatory = $true)][string[]]$Argumentos,
        [switch]$PodeFalhar
    )
    $linha = "npx $FirebaseTools " + (($Argumentos | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
    if ($DryRun) {
        Write-Host "  [simulação] $linha" -ForegroundColor DarkGray
        return @{ Codigo = 0; Texto = ''; Simulado = $true }
    }
    $npx = Obter-Npx
    if (-not $npx) { Abortar 'O Node.js nao foi encontrado (precisa do npx). Instale em https://nodejs.org e abra um terminal novo.' }
    $saida = & $npx $FirebaseTools @Argumentos 2>&1 | ForEach-Object { $_.ToString() }
    $codigo = $LASTEXITCODE
    $texto = ($saida | Out-String)
    if ($codigo -ne 0 -and -not $PodeFalhar) {
        $resumo = ($texto -split "`r?`n" | Where-Object { $_.Trim() -ne '' } | Select-Object -Last 4) -join ' | '
        if ($texto -match 'login|authenticate|credentials') {
            Abortar "O Firebase pediu login. Rode antes: npx $FirebaseTools login   (detalhe: $resumo)"
        }
        Abortar "O comando falhou (codigo $codigo): $linha`n  $resumo"
    }
    return @{ Codigo = $codigo; Texto = $texto; Simulado = $false }
}

function Extrair-Json([string]$texto) {
    $inicio = $texto.IndexOf('{')
    $fim = $texto.LastIndexOf('}')
    $inicioLista = $texto.IndexOf('[')
    if ($inicioLista -ge 0 -and ($inicio -lt 0 -or $inicioLista -lt $inicio)) {
        $fimLista = $texto.LastIndexOf(']')
        if ($fimLista -gt $inicioLista) { return ($texto.Substring($inicioLista, $fimLista - $inicioLista + 1) | ConvertFrom-Json) }
    }
    if ($inicio -ge 0 -and $fim -gt $inicio) {
        return ($texto.Substring($inicio, $fim - $inicio + 1) | ConvertFrom-Json)
    }
    return $null
}

# Procura `chave: "valor"` em JSON ou em JavaScript (a CLI muda o formato conforme a versao).
function Extrair-Valor([string]$texto, [string]$chave) {
    $m = [regex]::Match($texto, [regex]::Escape($chave) + '\W+([A-Za-z0-9_\-\.:/]+)')
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

function Testar-Ignorado([string]$caminhoRelativo) {
    if (Get-Command git -ErrorAction SilentlyContinue) {
        Push-Location $RaizRepo
        try {
            & git check-ignore -q -- $caminhoRelativo 2>$null
            return ($LASTEXITCODE -eq 0)
        } finally { Pop-Location }
    }
    $gitignore = Join-Path $RaizRepo '.gitignore'
    if (Test-Path $gitignore) {
        $leaf = Split-Path -Leaf $caminhoRelativo
        return [bool](Select-String -Path $gitignore -Pattern ("^\s*/?(" + [regex]::Escape($leaf) + "|" + [regex]::Escape(($caminhoRelativo -split '[\\/]')[0]) + "/?)\s*$") -Quiet)
    }
    return $false
}

function Gravar-Texto([string]$caminho, [string]$texto) {
    $utf8SemBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($caminho, $texto, $utf8SemBom)
}

function Gerar-Senha([int]$tamanho = 24) {
    $alfabeto = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789'
    $limite = 256 - (256 % $alfabeto.Length)
    $rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
    $resultado = New-Object System.Text.StringBuilder
    $byte = New-Object byte[] 1
    try {
        while ($resultado.Length -lt $tamanho) {
            $rng.GetBytes($byte)
            if ($byte[0] -lt $limite) { [void]$resultado.Append($alfabeto[$byte[0] % $alfabeto.Length]) }
        }
    } finally { $rng.Dispose() }
    return $resultado.ToString()
}

function Escrever-Json-Temporario([string]$conteudo) {
    $arquivo = Join-Path ([System.IO.Path]::GetTempPath()) ('alertaibt-' + [guid]::NewGuid().ToString('N') + '.json')
    Gravar-Texto $arquivo $conteudo
    return $arquivo
}

# Chamada REST do Identity Toolkit (usa a chave da API da Web; nunca imprime senha nem token).
function Invoke-IdentityToolkit([string]$metodo, [string]$chaveApi, [hashtable]$corpo) {
    $uri = "https://identitytoolkit.googleapis.com/v1/accounts:$metodo" + '?key=' + $chaveApi
    try {
        $resposta = Invoke-RestMethod -Method Post -Uri $uri -ContentType 'application/json' -Body ($corpo | ConvertTo-Json -Compress)
        return @{ Ok = $true; Uid = $resposta.localId; Erro = $null }
    } catch {
        $codigo = 'ERRO_DE_REDE'
        try {
            $detalhe = $_.ErrorDetails.Message | ConvertFrom-Json
            if ($detalhe.error.message) { $codigo = ($detalhe.error.message -split '[ :]')[0] }
        } catch { }
        return @{ Ok = $false; Uid = $null; Erro = $codigo }
    }
}

function Ler-Env {
    $valores = @{}
    if (Test-Path $ArquivoEnv) {
        foreach ($linha in (Get-Content -Path $ArquivoEnv -Encoding UTF8)) {
            if ($linha -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') { $valores[$Matches[1]] = $Matches[2].Trim() }
        }
    }
    return $valores
}

# ---------------------------------------------------------------- etapa 1

function Etapa-1 {
    Escrever-Titulo "Etapa 1: projeto, app da Web, banco de dados e web/.env  (projeto $Projeto)"
    if (-not $DryRun -and -not (Obter-Npx)) { Abortar 'O Node.js nao foi encontrado (precisa do npx). Instale em https://nodejs.org.' }

    if (-not (Testar-Ignorado 'web/.env')) { Abortar 'web/.env nao esta no .gitignore; por seguranca o script nao grava segredos assim. Adicione a linha ".env" ao .gitignore.' }

    # 1. Projeto
    Escrever-Passo "Procurando o projeto $Projeto"
    $lista = Invoke-Firebase -Argumentos @('projects:list', '--json')
    $existe = $false
    if (-not $lista.Simulado) {
        $json = Extrair-Json $lista.Texto
        if ($json -and $json.result) { $existe = [bool]($json.result | Where-Object { $_.projectId -eq $Projeto }) }
    }
    if ($existe) {
        Escrever-Pulado "o projeto $Projeto ja existe"
    } else {
        Confirmar-Mudanca
        [void](Invoke-Firebase -Argumentos @('projects:addfirebase', $Projeto))
        Escrever-Ok "Firebase adicionado ao projeto $Projeto"
    }

    # 2. App da Web
    Escrever-Passo 'Procurando o app da Web'
    $apps = Invoke-Firebase -Argumentos @('apps:list', 'WEB', '--project', $Projeto, '--json')
    $appId = $null
    if (-not $apps.Simulado) {
        $json = Extrair-Json $apps.Texto
        if ($json -and $json.result) {
            $primeiro = @($json.result) | Select-Object -First 1
            if ($primeiro) { $appId = $primeiro.appId }
        }
    }
    if ($appId) {
        Escrever-Pulado "ja existe um app da Web ($appId)"
    } else {
        Confirmar-Mudanca
        [void](Invoke-Firebase -Argumentos @('apps:create', 'web', 'Alerta IBT', '--project', $Projeto))
        Escrever-Ok 'app da Web "Alerta IBT" criado'
        if (-not $DryRun) {
            $apps = Invoke-Firebase -Argumentos @('apps:list', 'WEB', '--project', $Projeto, '--json')
            $json = Extrair-Json $apps.Texto
            if ($json -and $json.result) { $appId = (@($json.result) | Select-Object -First 1).appId }
        }
    }

    # 3. Banco de dados (instancia padrao)
    Escrever-Passo "Procurando o banco $Instancia"
    $instancias = Invoke-Firebase -Argumentos @('database:instances:list', '--project', $Projeto) -PodeFalhar
    if (-not $instancias.Simulado -and $instancias.Codigo -eq 0 -and $instancias.Texto -match [regex]::Escape($Instancia)) {
        Escrever-Pulado "o banco $Instancia ja existe"
    } else {
        Confirmar-Mudanca
        [void](Invoke-Firebase -Argumentos @('database:instances:create', $Instancia, '--location', 'us-central1', '--project', $Projeto))
        Escrever-Ok "banco $Instancia criado (us-central1)"
    }

    # 4. web/.env a partir da configuracao do app
    Escrever-Passo 'Lendo a configuracao do app da Web e gravando web/.env'
    if (-not $DryRun -and -not $appId) { Abortar 'Nao achei o id do app da Web para ler a configuracao.' }
    $config = Invoke-Firebase -Argumentos @('apps:sdkconfig', 'WEB', $(if ($appId) { $appId } else { '<id-do-app>' }), '--project', $Projeto, '--json')
    if ($DryRun) {
        Write-Host "  [simulação] gravaria web/.env com VITE_FIREBASE_API_KEY, _AUTH_DOMAIN, _PROJECT_ID, _DATABASE_URL e _APP_ID (as outras chaves do arquivo ficam como estao)" -ForegroundColor DarkGray
    } else {
        $chaves = [ordered]@{
            VITE_FIREBASE_API_KEY      = Extrair-Valor $config.Texto 'apiKey'
            VITE_FIREBASE_AUTH_DOMAIN  = Extrair-Valor $config.Texto 'authDomain'
            VITE_FIREBASE_PROJECT_ID   = Extrair-Valor $config.Texto 'projectId'
            VITE_FIREBASE_DATABASE_URL = Extrair-Valor $config.Texto 'databaseURL'
            VITE_FIREBASE_APP_ID       = Extrair-Valor $config.Texto 'appId'
        }
        if (-not $chaves.VITE_FIREBASE_DATABASE_URL) { $chaves.VITE_FIREBASE_DATABASE_URL = "https://$Instancia.firebaseio.com" }
        if (-not $chaves.VITE_FIREBASE_AUTH_DOMAIN) { $chaves.VITE_FIREBASE_AUTH_DOMAIN = "$Projeto.firebaseapp.com" }
        if (-not $chaves.VITE_FIREBASE_PROJECT_ID) { $chaves.VITE_FIREBASE_PROJECT_ID = $Projeto }
        foreach ($nome in @($chaves.Keys)) {
            if (-not $chaves[$nome]) { Abortar "A configuracao do app nao trouxe $nome. Rode: npx $FirebaseTools apps:sdkconfig WEB $appId --project $Projeto" }
        }
        $atual = Ler-Env
        foreach ($nome in $chaves.Keys) { $atual[$nome] = $chaves[$nome] }
        $texto = ($atual.Keys | ForEach-Object { "$_=$($atual[$_])" }) -join "`n"
        Gravar-Texto $ArquivoEnv ($texto + "`n")
        Escrever-Ok 'web/.env gravado (fica fora do git; as chaves que ja estavam nele foram mantidas)'
    }

    Mostrar-Cliques-Etapa-1
    Write-Host ''
    Write-Host 'Etapa 1 pronta. Faca os cliques acima e depois rode a etapa 2.' -ForegroundColor Green
}

function Mostrar-Cliques-Etapa-1 {
    Escrever-Titulo 'O que a CLI nao faz: 2 coisas no console (so na primeira vez)'
    Write-Host "  Abra: https://console.firebase.google.com/project/$Projeto/authentication/providers"
    Write-Host '  1. Se aparecer "Vamos começar"/"Primeiros passos", clique nele. Fique na aba "Método de login".'
    Write-Host '  2. Clique em "Google" > ligue o interruptor "Ativar" > em "Nome de exibição público do projeto" deixe "Alerta IBT"'
    Write-Host '     > em "E-mail de suporte do projeto" escolha o SEU e-mail > "Salvar".'
    Write-Host '  3. Volte e clique em "E-mail/senha" > ligue so o primeiro interruptor ("E-mail/senha"; deixe "Link por e-mail" desligado) > "Salvar".'
    Write-Host ('  4. Conferir: em "Configurações" > "Domínios autorizados" tem de existir ' + $Projeto + '.web.app.')
    Write-Host '  (Se preferir pela CLI mais nova: veja a secao "Login pela CLI" de docs/SITE-COMO-PUBLICAR.md.)'
}

# ---------------------------------------------------------------- etapa 2

function Etapa-2 {
    Escrever-Titulo "Etapa 2: build, publicacao, conta do PC  (projeto $Projeto)"
    if (-not $DryRun -and -not (Obter-Npx)) { Abortar 'O Node.js nao foi encontrado (precisa do npx).' }
    if (-not (Testar-Ignorado 'web/.env')) { Abortar 'web/.env nao esta no .gitignore.' }
    if (-not (Testar-Ignorado 'artifacts/credenciais-pc.txt')) { Abortar 'artifacts/ nao esta no .gitignore; por seguranca o script nao grava a senha do PC assim.' }

    $cfg = Ler-Env
    $chaveApi = $cfg['VITE_FIREBASE_API_KEY']
    $urlBanco = $cfg['VITE_FIREBASE_DATABASE_URL']
    if (-not $DryRun -and (-not $chaveApi -or -not $urlBanco)) { Abortar 'web/.env nao existe ou esta incompleto. Rode antes: scripts\publicar-site.ps1 -Etapa 1' }
    if ($DryRun) {
        if (-not $chaveApi) { $chaveApi = '<chave-da-api>' }
        if (-not $urlBanco) { $urlBanco = "https://$Instancia.firebaseio.com" }
    }

    # 1. Build do site (nao mexe na nuvem)
    Escrever-Passo 'npm ci e npm run build em web/'
    if ($DryRun) {
        Write-Host '  [simulação] npm ci   (em web/)' -ForegroundColor DarkGray
        Write-Host '  [simulação] npm run build   (em web/)' -ForegroundColor DarkGray
    } else {
        Push-Location $PastaWeb
        try {
            & npm ci
            if ($LASTEXITCODE -ne 0) { Abortar 'npm ci falhou.' }
            & npm run build
            if ($LASTEXITCODE -ne 0) { Abortar 'npm run build falhou.' }
        } finally { Pop-Location }
        Escrever-Ok 'site compilado em web/dist'
    }

    # 2. Hosting e regras (publicar de novo e sempre seguro: so troca pela mesma versao)
    Escrever-Passo "Publicando o Hosting e as regras do banco (firebase deploy --only hosting,database)"
    Confirmar-Mudanca
    Push-Location $RaizRepo
    try {
        [void](Invoke-Firebase -Argumentos @('deploy', '--only', 'hosting,database', '--project', $Projeto))
    } finally { Pop-Location }
    Escrever-Ok "publicado em https://$Projeto.web.app"

    # 3. Conta do PC
    Escrever-Passo "Conta do PC ($EmailPc)"
    $uid = $null
    $senhaNova = $null
    $senhaGuardada = $null
    if (Test-Path $ArquivoCredenciais) {
        $conteudo = Get-Content -Path $ArquivoCredenciais -Encoding UTF8
        $linhaSenha = $conteudo | Where-Object { $_ -match '^Senha( do PC)?\s*[:=]\s*(.+)$' } | Select-Object -First 1
        if ($linhaSenha -and ($linhaSenha -match '^Senha( do PC)?\s*[:=]\s*(.+)$')) { $senhaGuardada = $Matches[2].Trim() }
    }

    if ($DryRun) {
        Write-Host "  [simulação] se artifacts\credenciais-pc.txt existir, testaria o login com a senha guardada (REST signInWithPassword)" -ForegroundColor DarkGray
        Write-Host "  [simulação] senao, criaria a conta $EmailPc por REST (accounts:signUp) com senha aleatoria de 24 letras e numeros" -ForegroundColor DarkGray
        Write-Host '  [simulação] gravaria artifacts\credenciais-pc.txt (fora do git) e mostraria os dados uma unica vez' -ForegroundColor DarkGray
        $uid = '<uid-do-pc>'
    } else {
        if ($senhaGuardada) {
            $teste = Invoke-IdentityToolkit 'signInWithPassword' $chaveApi @{ email = $EmailPc; password = $senhaGuardada; returnSecureToken = $true }
            if ($teste.Ok) {
                $uid = $teste.Uid
                Escrever-Pulado 'a conta do PC ja existe e a senha de artifacts\credenciais-pc.txt funciona'
            } elseif ($teste.Erro -in @('EMAIL_NOT_FOUND')) {
                $senhaGuardada = $null
            } else {
                Abortar "A conta $EmailPc existe mas a senha guardada em artifacts\credenciais-pc.txt nao funciona ($($teste.Erro)). No console: Authentication > Usuarios > ... > Redefinir senha (ou apague o usuario) e rode de novo."
            }
        }
        if (-not $uid) {
            Confirmar-Mudanca
            $senhaNova = Gerar-Senha 24
            $criar = Invoke-IdentityToolkit 'signUp' $chaveApi @{ email = $EmailPc; password = $senhaNova; returnSecureToken = $true }
            if (-not $criar.Ok) {
                if ($criar.Erro -eq 'EMAIL_EXISTS') {
                    Abortar "A conta $EmailPc ja existe, mas nao ha senha guardada neste computador. No console: Authentication > Usuarios > ... > Redefinir senha (ou apague o usuario) e rode a etapa 2 de novo."
                }
                if ($criar.Erro -eq 'OPERATION_NOT_ALLOWED') {
                    Abortar 'O login por E-mail/senha esta desligado no Firebase. Faca os cliques da etapa 1 (E-mail/senha) e rode de novo.'
                }
                Abortar "Nao consegui criar a conta do PC ($($criar.Erro))."
            }
            $uid = $criar.Uid
            if (-not (Test-Path $PastaArtefatos)) { New-Item -ItemType Directory -Path $PastaArtefatos | Out-Null }
            $texto = @(
                'Alerta-IBT - conta do PC no site (Configurações > Site). NAO COMPARTILHE e NAO COMMITE este arquivo.',
                "URL do banco: $urlBanco",
                "Chave da API (web): $chaveApi",
                "E-mail do PC: $EmailPc",
                "Senha do PC: $senhaNova",
                "UID: $uid",
                ('Criado em: ' + (Get-Date -Format 'yyyy-MM-dd HH:mm'))
            ) -join "`n"
            Gravar-Texto $ArquivoCredenciais ($texto + "`n")
            Escrever-Ok 'conta do PC criada; dados salvos so em artifacts\credenciais-pc.txt'
        }
    }

    # 4. /devices/{uid} = true
    Escrever-Passo "Registrando a conta do PC em /devices/$uid"
    $atual = Invoke-Firebase -Argumentos @('database:get', "/devices/$uid", '--project', $Projeto) -PodeFalhar
    if (-not $atual.Simulado -and $atual.Codigo -eq 0 -and $atual.Texto.Trim() -match '(^|\s)true(\s|$)') {
        Escrever-Pulado 'a conta ja esta em /devices'
    } else {
        Confirmar-Mudanca
        $arquivo = if ($DryRun) { '<arquivo-temporario-com-true>' } else { Escrever-Json-Temporario 'true' }
        try {
            [void](Invoke-Firebase -Argumentos @('database:set', "/devices/$uid", $arquivo, '--project', $Projeto, '--force'))
        } finally {
            if (-not $DryRun -and (Test-Path $arquivo)) { Remove-Item $arquivo -Force }
        }
        Escrever-Ok 'conta do PC registrada em /devices'
    }

    # 5. Dados para o app do PC (so quando a senha acabou de ser criada)
    if ($senhaNova) {
        Escrever-Titulo 'Dados do PC (mostrados UMA vez; tambem estao em artifacts\credenciais-pc.txt, fora do git)'
        Write-Host "  E-mail:           $EmailPc"
        Write-Host "  Senha:            $senhaNova"
        Write-Host "  Chave da API:     $chaveApi"
        Write-Host "  URL do banco:     $urlBanco"
    }
    Write-Host ''
    Write-Host 'No app do PC: Configuracoes > Site: cole a URL do banco, a chave da API, o e-mail e a senha, e ligue a chave "Ligado".' -ForegroundColor Green
    Write-Host 'Depois entre uma vez no site com a sua conta Google e rode a etapa 3 para virar administrador.' -ForegroundColor Green
}

# ---------------------------------------------------------------- etapa 3

function Etapa-3 {
    Escrever-Titulo "Etapa 3: administrador  (projeto $Projeto)"
    if (-not $Admin -or $Admin -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') { Abortar 'Informe o e-mail da conta Google: scripts\publicar-site.ps1 -Etapa 3 -Admin voce@gmail.com' }
    if (-not $DryRun -and -not (Obter-Npx)) { Abortar 'O Node.js nao foi encontrado (precisa do npx).' }

    Escrever-Passo "Procurando $Admin em /users"
    $usuarios = Invoke-Firebase -Argumentos @('database:get', '/users', '--project', $Projeto)
    $uid = $null
    $papel = $null
    if ($DryRun) {
        $uid = '<uid-do-dono>'
    } else {
        $json = Extrair-Json $usuarios.Texto
        if ($json) {
            foreach ($propriedade in $json.PSObject.Properties) {
                $registro = $propriedade.Value
                if ($registro.PSObject.Properties['email'] -and ([string]$registro.email).ToLowerInvariant() -eq $Admin.ToLowerInvariant()) {
                    $uid = $propriedade.Name
                    if ($registro.PSObject.Properties['role']) { $papel = [string]$registro.role }
                    break
                }
            }
        }
        if (-not $uid) {
            Abortar "Nao achei $Admin em /users. Abra https://$Projeto.web.app, clique em Entrar com Google com essa conta (uma vez) e rode de novo."
        }
    }

    if ($papel -eq 'admin') {
        Escrever-Pulado "$Admin ja e administrador"
    } else {
        Confirmar-Mudanca
        $arquivo = if ($DryRun) { '<arquivo-temporario-com-role-admin>' } else { Escrever-Json-Temporario '{"role":"admin"}' }
        try {
            [void](Invoke-Firebase -Argumentos @('database:update', "/users/$uid", $arquivo, '--project', $Projeto, '--force'))
        } finally {
            if (-not $DryRun -and (Test-Path $arquivo)) { Remove-Item $arquivo -Force }
        }
        Escrever-Ok "$Admin agora e administrador"
    }
    Write-Host ''
    Write-Host 'pronto' -ForegroundColor Green
    Write-Host 'Feche e abra o site: a aba Equipe aparece para voce.' -ForegroundColor Green
}

# ---------------------------------------------------------------- inicio

if ($Etapa -eq 0) {
    Write-Host 'Uso: powershell -ExecutionPolicy Bypass -File scripts\publicar-site.ps1 -Etapa <1|2|3> [-Admin <email>] [-DryRun]'
    Write-Host '  -Etapa 1   projeto, app da Web, banco e web/.env (e os 2 cliques do console)'
    Write-Host '  -Etapa 2   build, publicacao (Hosting e regras), conta do PC e credenciais'
    Write-Host '  -Etapa 3   -Admin voce@gmail.com   torna essa conta administradora'
    Write-Host '  -DryRun    mostra os comandos sem rodar nada'
    exit 0
}

if ($DryRun) { Write-Host 'MODO SIMULACAO: nenhum comando sera executado e nada sera alterado.' -ForegroundColor Yellow }
switch ($Etapa) {
    1 { Etapa-1 }
    2 { Etapa-2 }
    3 { Etapa-3 }
}
