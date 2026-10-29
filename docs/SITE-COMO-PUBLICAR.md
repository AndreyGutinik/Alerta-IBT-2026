# Site Alerta IBT: como publicar (e refazer)

O projeto **`alertaibt`** já existe no Firebase (plano Spark, grátis, sem cartão). Hoje o site está em
<https://alertaibt.web.app>, as regras do banco estão publicadas, a conta do PC existe e o dono já é administrador.
Este guia serve para **refazer do zero** (outro projeto, outro PC, algo apagado) e para ligar o app do PC ao site.
Tudo roda no **seu** terminal; nada aqui é feito sozinho.

> Segurança: a senha da conta do PC fica **só** em `artifacts\credenciais-pc.txt` (fora do git) e, no app do PC, criptografada.
> Não cole essa senha em chat, e-mail ou commit.

## Resumo rápido

| Quando | Comando (na raiz do repositório, no PowerShell) |
|---|---|
| Ver o que seria feito, sem tocar em nada | acrescente `-DryRun` a qualquer um abaixo |
| Projeto, app da Web, banco e `web/.env` | `powershell -ExecutionPolicy Bypass -File scripts\publicar-site.ps1 -Etapa 1` |
| Build, publicar site e regras, conta do PC | `powershell -ExecutionPolicy Bypass -File scripts\publicar-site.ps1 -Etapa 2` |
| Tornar uma conta administradora | `powershell -ExecutionPolicy Bypass -File scripts\publicar-site.ps1 -Etapa 3 -Admin voce@gmail.com` |

O script **pula o que já existe** (pode rodar de novo sem medo) e pergunta
`Isto vai alterar o projeto alertaibt. Continuar? (s/N)` antes de mudar qualquer coisa na nuvem. Se estiver tudo pronto,
não pergunta nada.

## 1. Antes de começar

1. Instale o **Node.js** (<https://nodejs.org>) e abra um terminal novo (`node -v` tem de responder).
2. Entre na sua conta Google no Firebase: `npx firebase-tools@14.27.0 login`.
3. Na raiz do repositório: `git status` limpo (o script grava `web/.env` e `artifacts\`, ambos ignorados pelo git).

## 2. Etapa 1: projeto, app da Web, banco

`scripts\publicar-site.ps1 -Etapa 1` faz, pulando o que já existe:

1. `projects:addfirebase alertaibt` (só se o projeto não estiver em `projects:list`).
2. `apps:create web "Alerta IBT"` (só se não houver app da Web).
3. `database:instances:create alertaibt-default-rtdb --location us-central1` (só se o banco não existir).
   (`firebase init database` só funciona de forma interativa, por isso o script usa este comando.)
4. Lê a configuração do app (`apps:sdkconfig`) e grava `web/.env` com as cinco chaves `VITE_FIREBASE_*`. As outras chaves
   que já estiverem no arquivo (reCAPTCHA, modelo de IA) são mantidas.

No fim ele lista os dois cliques do console da seção 3.

## 3. Ligar o login (2 cliques no console, só na primeira vez)

A CLI não liga os provedores de login. Faça:

1. Abra <https://console.firebase.google.com/project/alertaibt/authentication/providers>.
2. Se aparecer **Vamos começar** / **Primeiros passos**, clique. Fique na aba **Método de login**.
3. Clique em **Google** → ligue **Ativar** → em *Nome de exibição público do projeto* deixe **Alerta IBT** → em
   *E-mail de suporte do projeto* escolha **o seu e-mail** → **Salvar**.
4. Clique em **E-mail/senha** → ligue só o primeiro interruptor (*E-mail/senha*; *Link por e-mail* fica desligado) →
   **Salvar**.
5. **Configurações** → **Domínios autorizados**: tem de existir `alertaibt.web.app` (e `localhost` para testes).

### Login pela CLI (alternativa)

Com a CLI mais nova dá para ligar os dois provedores sem o console. Acrescente ao `firebase.json` um bloco `auth` e rode
`npx firebase-tools@latest deploy --only auth --project alertaibt`:

```json
"auth": {
  "providers": {
    "emailPassword": true,
    "googleSignIn": { "oAuthBrandDisplayName": "Alerta IBT", "supportEmail": "voce@gmail.com" }
  }
}
```

(Foi assim que o projeto atual foi preparado. A `firebase-tools@14.27.0` do script não faz essa publicação.)

## 4. Etapa 2: publicar e criar a conta do PC

`scripts\publicar-site.ps1 -Etapa 2`:

1. `npm ci` e `npm run build` em `web/`.
2. `firebase deploy --only hosting,database` (site em <https://alertaibt.web.app> e `database.rules.json`).
3. Conta do PC (`pc-telao@alertaibt.web.app`): se `artifacts\credenciais-pc.txt` existir e a senha funcionar, **pula**; senão
   cria a conta (senha aleatória de 24 caracteres), salva em `artifacts\credenciais-pc.txt` e **mostra os dados uma única
   vez**.
4. Grava o uid dela em `/devices/{uid}` (é o que as regras exigem para o PC ler pedidos e escrever status).

Se a conta já existe mas não há senha guardada neste computador, o script para e explica: no console, **Authentication** →
**Usuários** → **⋮** → **Redefinir senha** (ou apague o usuário) e rode a etapa 2 de novo.

## 5. Ligar o app do PC ao site

No app do PC (**IBT Controller**): **Configurações** → bloco **Site** (abaixo do Telegram).

1. **URL do banco**: `https://alertaibt-default-rtdb.firebaseio.com`.
2. **Chave da API (web)**: a de `artifacts\credenciais-pc.txt` (ou de `web/.env`, `VITE_FIREBASE_API_KEY`).
3. **E-mail do PC**: `pc-telao@alertaibt.web.app`.
4. **Senha do PC**: a de `artifacts\credenciais-pc.txt`. Depois de salva, aparece só como bolinhas; **Trocar** abre para outra.
5. Ligue a chave **Ligado**. Tudo salva sozinho 600 ms depois.

A bolinha **Site** (na barra lateral e no próprio bloco): cinza = não configurado ou desligado; amarela = conectando ou
reconectando; **verde = conectado**; vermelha = erro de login, ou sem conexão há mais de 1 minuto. Passe o mouse para ver o
motivo.

## 6. Etapa 3: primeiro administrador

1. Abra <https://alertaibt.web.app> e **Entrar com Google** com a sua conta (uma vez; isso cria o seu registro como membro).
2. `scripts\publicar-site.ps1 -Etapa 3 -Admin voce@gmail.com`. Ele acha o seu uid em `/users`, troca `role` para `admin` e
   escreve **pronto**. Feche e abra o site: a aba **Equipe** aparece. Dali em diante você promove os outros por lá.

## 7. IA da prévia (App Check só nela)

O site usa o **reCAPTCHA Enterprise** no App Check (a versão v3 **não é mais aceita** pelo Firebase). Sem isso a prévia usa só o
corretor local (funciona, sem a IA).

1. Em <https://console.cloud.google.com/security/recaptcha?project=alertaibt> (**reCAPTCHA Enterprise**) → **Criar chave** →
   tipo **Site** → plataforma **Web** → domínios `alertaibt.web.app` e `localhost`. Copie o **ID da chave**.
2. No console do Firebase: **App Check** → **Apps** → o app da Web → **reCAPTCHA Enterprise** → cole o ID → **Salvar**.
3. Ainda em **App Check** → **APIs**: imponha **somente "Firebase AI Logic"**. **Não** imponha para Realtime Database nem
   Authentication.
4. **Firebase AI Logic** → ligue o provedor **Gemini Developer API** (cota grátis).
5. Coloque o ID em `VITE_RECAPTCHA_SITE_KEY` no `web/.env` e rode a **etapa 2** de novo.

## 8. Conferir no celular

Roteiro completo em `docs/CHECKLIST-IGREJA.md`, seção 14. Resumo: instalar o app (menu do Chrome → *Instalar app*), entrar com
Google, mandar um aviso e ver o status mudar (*enviado* → *Nº na fila* → *passando* → hora). Para voltar uma publicação ruim:
console → **Hosting** → histórico → **Reverter**.

## Se der erro

| O que aparece | O que fazer |
|---|---|
| Ao entrar com Google: **"domínio não autorizado"** (`auth/unauthorized-domain`) | Console → **Authentication** → **Configurações** → **Domínios autorizados** → **Adicionar domínio** → `alertaibt.web.app` (e `localhost` se for teste). Se estiver abrindo por outro endereço (por exemplo o `.firebaseapp.com`), adicione esse também. |
| Ao entrar: o pop-up fecha sozinho ou diz "operação não permitida" | O provedor **Google** não está ligado: refaça a seção 3. Permita pop-ups para o site. |
| **"Permission denied"** ao enviar ou ao abrir o site | As regras não estão publicadas ou estão velhas: rode a etapa 2. Também acontece se a sua conta foi **bloqueada** (aba Equipe), se o registro em `/users/{uid}` não existe (entre de novo com Google) ou se você mandou outro aviso há menos de 20 s. |
| Site mostra **"Site ainda não configurado"** | Falta `web/.env` no build: rode a etapa 1 e depois a 2. |
| Pílula **"PC offline"** no site | O PC não escreve o batimento há mais de 90 s. Veja no app do PC: o app está aberto? A bolinha **Site** está verde? Vermelha "Erro de login": e-mail, senha ou chave da API errados em Configurações → Site (ou a conta do PC não está em `/devices`: rode a etapa 2). Vermelha "Sem conexão": internet do PC. Cinza: a chave **Ligado** está desligada ou falta um campo. |
| No app do PC, bolinha **Site** vermelha "Erro de login" logo depois de colar os dados | Confira os 4 campos (a URL termina em `firebaseio.com`; a senha é a de `artifacts\credenciais-pc.txt`). Se perdeu a senha, redefina no console (seção 4). |
| Avisos ficam em **"enviado"** e nunca andam | O PC está offline (acima) ou não leu o pedido: veja **Configurações → Diagnóstico → Copiar logs**. Pedidos com mais de 30 min são marcados como *expirou* quando o PC volta. |
| O script diz que o Firebase pediu login | `npx firebase-tools@14.27.0 login` e rode de novo. |
| `-Etapa 3` não acha o seu e-mail | Entre uma vez no site com essa conta Google e rode de novo. |

## Para quem desenvolve (não mexe na nuvem)

- `npm run emulators` e, em outro terminal, `npm run dev:emu` (em `web/`): site local contra os emuladores (projeto
  `demo-alertaibt`); o gancho `window.__alertaE2E.signIn(email, nome)` entra sem a tela do Google.
- `npm test` (unidade), `npm run test:emu` (regras e adaptadores nos emuladores), `npm run test:e2e` (Chrome + emuladores).
- App do PC contra os emuladores: em **Configurações → Site** use a URL `http://127.0.0.1:9000?ns=demo-alertaibt-default-rtdb`
  (o app usa o emulador do Auth em `127.0.0.1:9099` quando a URL é local).
- Testes do conector do PC no emulador real, com `database.rules.json`:
  `$env:ALERTAIBT_RTDB_EMULATOR = "1"` e
  `npx firebase-tools@14.27.0 emulators:exec --only auth,database --project demo-alertaibt "dotnet test --project desktop/tests/AlertaIBT.Core.Tests --filter-class AlertaIBT.Core.Tests.Firebase.EmulatorIntegrationTests"`.
  Sem a variável esses testes são pulados.
