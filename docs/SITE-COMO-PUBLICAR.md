# Site Alerta IBT — como publicar

O site (`web/`) está pronto e testado nos emuladores. **Nada disso foi feito na nuvem**: não existe projeto no
Firebase ainda. Todos os passos abaixo mexem em conta, projeto ou publicação e **precisam da aprovação e da mão
do dono**. Tudo cabe no plano **Spark (grátis), sem cartão e sem faturamento**.

## 1. Criar o projeto (console do Firebase) — precisa do dono

1. Em <https://console.firebase.google.com> crie (ou escolha) o projeto **`alertaibt`** no plano **Spark**.
   Não ative faturamento. O site padrão será `alertaibt.web.app`.
2. **Authentication → Método de login:** ative **Google** (nome público "Alerta IBT", e-mail de suporte do
   dono) e **E-mail/senha** (é a conta do PC).
3. **Realtime Database → Criar banco de dados** (modo bloqueado; as regras vêm no passo 5).
4. **Configurações do projeto → Seus apps → Web (`</>`):** registre o app "Alerta IBT" e copie a configuração.
5. **Authentication → Configurações → Domínios autorizados:** confirme que `alertaibt.web.app` está na lista
   (os domínios `*.firebaseapp.com` e `*.web.app` do projeto já costumam vir).

## 2. Configurar o site no PC — precisa do dono

1. Copie `web/.env.example` para `web/.env.production` e preencha as cinco chaves `VITE_FIREBASE_*` com a
   configuração do passo 1.4 (o arquivo não é versionado). Sem isso o site mostra "Site ainda não configurado".
2. Troque o projeto padrão do repositório: o `.firebaserc` traz o marcador `demo-alertaibt` (que não publica).
   Rode `firebase use alertaibt` (ou edite o arquivo) com o id real do projeto.

## 3. Conta do PC — precisa do dono

1. **Authentication → Usuários → Adicionar:** e-mail e senha da conta do PC (qualquer e-mail, só para o app do PC).
2. Copie o **UID** dessa conta e, em **Realtime Database → Dados**, crie `devices/{UID}` com o valor `true`.
   (O instalador do PC, Plano 2B, fará isso pela CLI depois; até lá é manual.)

## 4. Publicar — precisa do dono

```
cd web
npm ci
npm test
npm run build
npm run check:pwa
npx firebase login                 # o dono entra com a conta Google dele
npx firebase deploy --only hosting,database
```

`deploy --only database` publica `database.rules.json`; `--only hosting` publica `web/dist` (o `predeploy` roda o
build de novo). Depois confira os cabeçalhos: `curl -I https://alertaibt.web.app/` deve trazer
`Cache-Control: no-cache` e um arquivo de `/assets/` deve trazer `public, max-age=31536000, immutable`
(se `/assets/` também vier `no-cache`, avise: é só reordenar os blocos de `headers` em `firebase.json`).

## 5. IA da prévia (App Check só nela) — precisa do dono

1. Em <https://www.google.com/recaptcha/admin> crie uma chave **reCAPTCHA v3** para o domínio
   `alertaibt.web.app` (e `localhost` para testes).
2. **App Check → Apps:** registre o app Web com essa chave. **Em "APIs", imponha o App Check somente para
   "Firebase AI Logic"** (Gemini Developer API). **Não** ative para Realtime Database nem para Authentication.
3. **Firebase AI Logic:** ative o provedor **Gemini Developer API** (cota grátis do Spark).
4. Cole a chave do site em `VITE_RECAPTCHA_SITE_KEY` no `web/.env.production`, rode o build e o deploy de novo.
   Sem a chave a prévia usa só o parser local (funciona, mas sem a correção da IA).

## 6. Primeiro admin — precisa do dono

1. O dono abre o site e **entra com Google uma vez** (isso cria o registro dele como `member`).
2. No console, **Realtime Database → Dados → `users/{UID do dono}/role`:** troque `member` por `admin`.
   (A CLI do instalador do PC fará esse passo no Plano 2B.) A aba **Equipe** aparece na hora; dali em diante ele
   promove os outros.

## 7. Conferir no celular

Instalar o app (menu do Chrome → "Instalar app"), abrir sem sinal (abre do cache), enviar um aviso com e sem
sinal, e ver o status mudar quando o PC publicar. Para voltar uma publicação ruim: Hosting → histórico →
"Reverter".

## Para quem desenvolve (não mexe na nuvem)

- `npm run emulators` e, em outro terminal, `npm run dev:emu`: site local contra os emuladores (projeto
  `demo-alertaibt`); o gancho `window.__alertaE2E.signIn(email, nome)` entra sem a tela do Google.
- `npm test` (unidade), `npm run test:emu` (regras + adaptadores nos emuladores), `npm run test:e2e` (Chrome +
  emuladores; sem Chrome rode `npx playwright install chromium` e depois `PW_CHANNEL= npm run test:e2e`).
