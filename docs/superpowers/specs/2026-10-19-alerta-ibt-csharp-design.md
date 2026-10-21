# Alerta-IBT 8 (C#) + site AlertaIBT.web.app + APK: especificação de design

- **Data:** 2026-10-19
- **Status:** design aprovado em conversa (12 perguntas + 2 rodadas de 8 e 12 sugestões + mockups); revisada por um leitor independente e ajustada com as respostas do dono; aguarda aprovação final
- **Referência visual:** `docs/superpowers/specs/mockups/` — `painel-v7.html` (app do PC) e `site-v5.html` (site/APK). Os mockups usam fontes e ícones servidos pelo companion visual; abertos direto no navegador podem aparecer sem a fonte Marble.
- **Substitui:** o app Electron v7.0.0 deste repositório (a regra "não trocar a stack" de `CLAUDE_CONTEXT.md` e `PROJECT_CONTEXT.md` foi revogada pelo dono do projeto nesta data).

---

## 1. Objetivo

Reescrever o Alerta-IBT em **C# (.NET 10 + WPF)**, no mesmo padrão do App Flash, para ficar leve, rápido e confiável no PC do telão, e somar duas novas portas de entrada além do Telegram: o **site AlertaIBT.web.app** (PWA) e, numa segunda fase, um **APK Android independente do site**. Os avisos aparecem numa barra no topo do LED 2560x768 e **nunca** aparecem na transmissão (OBS/NDI).

### Critérios de sucesso

1. A barra **não aparece** em OBS (Captura de tela/janela), NDI Screen Capture nem em print do Windows. Esta é a dor principal do app atual.
2. O app abre sozinho com o Windows, direto na bandeja, sem ninguém mexer.
3. A barra só aparece no monitor 2560x768. Sem ele, nada aparece e a fila espera.
4. A rolagem do texto é lisa no LED (sem engasgo visível a 60 Hz).
5. Nenhum português errado vai para o telão: tudo passa pela correção (Gemini grátis, com corretor offline de reserva).
6. Avisos de estacionamento sempre saem no padrão `MODELO COR | PLACA: ABC-1234 | MOTIVO`, em MAIÚSCULAS.
7. Quem manda pelo site ou pelo Telegram fica sabendo o que aconteceu: na fila, passando, exibido, recusado (e por quê), cancelado ou expirado.
8. Custo zero de operação: Firebase no plano grátis (Spark), Gemini no plano grátis, sem API paga.
9. App do PC leve: abre para a bandeja em menos de 2 s, CPU parada perto de 0% e memória abaixo de 150 MB.

## 2. Contexto (setup da igreja e estado atual)

| Item | Situação |
|---|---|
| PC do telão | PC do Holyrics. Saída para o LED 2560x768. É o mesmo PC do App Flash (camada de flash por cima de tudo). |
| PC de desenvolvimento | Um monitor 2560x1440. O LED 2560x768 só existe na igreja. |
| Quem opera | Equipe de projeção (não digita nada no culto); o dono do projeto (operador de luz, senta ao lado e manda avisos manuais); equipe do estacionamento (manda do celular). |
| App atual | Electron v7.0.0 (`src/main.js` etc.): fila, urgente, histórico, logs, Telegram por polling com prefixo e correções, confirmação "✅ Mensagem exibida no telão.", aviso de pausa no Telegram, API HTTP local `127.0.0.1:8787`, auto-update por GitHub Releases (`AndreyGutinik/Estacionamento_IBT`). |
| Telegram | Grupo com bot. Os voluntários já usam o prefixo `#`. |
| Holyrics (pausa) | Quando entra o slide da pregação, o Holyrics chama a API HTTP local (`/overlay/off`; `/overlay/on` depois). O Companion não é mais usado. |
| LED desligado | Confirmado pelo dono: ao desligar o LED, a tela 2560x768 some do Windows (como tirar o cabo). |
| Firebase | CLI logado na conta do dono (6 projetos, nenhum do Alerta). `alertaibt.web.app` ainda não existe (404 em 2026-10-19). |
| Outros apps do dono | Ordem do Culto e Metrônomo: React + Vite + TypeScript + Firebase 12, login Google configurado em `firebase.json` (`auth.providers`). |

## 3. Decisões aprovadas

**Plataforma e instalação**
- App do PC em C# .NET 10 + WPF + WPF-UI, como o App Flash, com **tudo** o que o app atual faz, exceto o que foi removido abaixo.
- Instalador com atualização automática pelas Releases do GitHub.
- Começa do zero: **não** importa a configuração do Electron.
- "Iniciar com o Windows" fica **sempre ligado** e **não aparece** na interface.

**Barra no LED**
- Só no LED 2560x768. Sem ele: nada aparece, a fila espera e quem mandou é avisado ("⏳ Telão desligado").
- Posição padrão **topo** (rodapé continua possível nas Configurações).
- Padrão: tamanho **médio (56 px)** e **90 px/s**; os dois ajustáveis por controle deslizante com prévia ao vivo.
- Sempre escondida de captura (OBS/NDI/print), **sem** opção para desligar.
- Texto sempre em **MAIÚSCULAS**. O aviso passa **1 vez**. Urgente só fura a fila (visual igual aos outros).

**Painel no PC** (mockup `painel-v7.html`)
- Clique no ícone da bandeja → painel rápido; duplo clique → janela completa.
- Janela com 3 abas: **Início, Correção, Configurações**. Logs ficam em Configurações > Diagnóstico.
- Sem botão Salvar: tudo **salva sozinho** ao digitar.
- Dicas em ícones (i) com balão; ícones **Lucide**, todos da mesma cor.
- Status discreto na lateral (fonte light, pequena): Telegram, Site, LED, Barra, Gemini.
- Removidos: modelos rápidos, campo de prefixo (fixo `#`), opção de iniciar com o Windows.
- Atalho global **Ctrl+Alt+Espaço** abre o painel rápido com o campo pronto (configurável).
- Prévia ao vivo "Vai aparecer assim" enquanto digita.
- Recusados aparecem no Histórico com X vermelho e o motivo.
- Rejeitadas pelo dono: repetição automática, cara especial para urgente, tempo de passagem, fixar o painel, desfazer/confirmar ao apagar (apaga direto).

**Correção do texto**
- Correção com **Gemini (Google) no plano grátis**; reserva **offline** no PC (dicionário pt-BR + regras fixas), também escolhível como "Só offline".
- Chave do Gemini fica na aba **Correção**, com cadeado.
- **Regras fixas** de correção só existem no PC (nunca no celular).

**Regra do texto**
- Padrão: `MODELO COR | PLACA: ABC-1234 | MOTIVO`. Placas **antigas** (ABC-1234) e **Mercosul** (ABC1D23, mostrada como ABC-1D23).
- Modelo, cor e placa são **obrigatórios**; motivo é opcional. Faltando algo: não vai para o telão e volta mensagem de erro para quem mandou.
- **Exceções:** o PC e quem é **admin** no site/APK podem mandar **qualquer** texto. Sem placa, vira **aviso livre** (só correção de português + maiúsculas). Ex.: avisos do ministério infantil.

**Site e APK** (mockup `site-v5.html`)
- PWA instalável; login com Google; **qualquer conta entra**; o admin remove quem não for da equipe (fica bloqueado) e promove outros admins na aba **Equipe** (só admin vê).
- Abas: **Enviar**, **Status**, **Equipe** (admin).
- Enviar: uma linha só, prévia ao vivo **com IA no celular** ("Corrigindo com IA…"), botão **Enviar** + ícone **Urgente** na mesma linha; urgente só **segurando 1 s** (com vibração).
- Microfone **pequeno e discreto** dentro do campo (digitar é o caminho principal).
- Bloqueio de **carro repetido** pela placa (10 min, qualquer origem).
- Cada pessoa vê "Seus avisos" e pode **cancelar** os próprios enquanto estão na fila; admin cancela qualquer um.
- Status ao vivo com **mini-telão** (barra passando igual ao LED).
- Tema **segue o celular** (claro/escuro). Interface **10% menor** que o padrão para caber em telas pequenas.
- Tutorial de 3 telas no primeiro acesso.
- **Sem** notificação push (status só dentro do app), **sem** fila offline (sem sinal = erro e tenta de novo), **sem** câmera para placa, **sem** modo uma mão.
- Telegram continua; para avisos que vêm do site, o grupo recebe só o "✅ exibido".
- **APK independente do site**: tem que funcionar mesmo se o site for desligado no futuro.

## 4. Arquitetura

### 4.1 Visão geral

```
 Telegram (grupo, '#') ──long polling──▶ ┌──────────────────────────┐ ──▶ Barra no LED 2560x768
 Painel do PC (bandeja/janela) ────────▶ │  App do PC (C#, .NET 10)  │      (topmost, sem captura)
 Holyrics (HTTP 127.0.0.1:8787) ───────▶ │  fila · regra · correção  │ ──▶ Telegram (✅ / ❌ / ⏳ / ⚠️)
 Gemini REST (grátis) ◀──correção──────── │  · Firebase (REST + SSE)  │
                                         └────────────┬─────────────┘
                                                      │ Realtime Database (stream SSE)
                                         ┌────────────┴─────────────┐
                                         │ Firebase "Alerta IBT"     │
                                         │ Auth · RTDB · Hosting ·   │
                                         │ AI Logic (Gemini) ·       │
                                         │ App Check                 │
                                         └──────┬─────────────┬──────┘
                                  Site PWA (alertaibt.web.app)   APK Android (fase 2, mesmo código, sem depender do Hosting)
```

- O **PC é a única autoridade** sobre a fila e o LED: tudo (Telegram, site, APK, painel) entra no mesmo pipeline do PC.
- Site e APK **não falam com o PC diretamente**: escrevem pedidos no Realtime Database e leem o estado ao vivo que o PC publica lá.
- Sem servidor próprio e sem Cloud Functions (exigiriam o plano pago).

### 4.2 Repositório

Mesmo repositório (`AndreyGutinik/Estacionamento_IBT`), branch novo (ex.: `v8-csharp`). O código Electron sai do branch (fica no histórico e na tag `v7.0.0`).

```
desktop/                     solução C# (AlertaIBT.slnx)
  src/AlertaIBT.Core/        lógica pura: formato, placas, regra, fila, dedupe, Telegram, regras fixas
  src/AlertaIBT.App/         WPF: bandeja, painel, janela, barra, plataforma Win32, Firebase, Gemini
  tests/AlertaIBT.Core.Tests/
web/                         site PWA (React + Vite + TypeScript); a pasta android/ do Capacitor nasce aqui na fase 2
shared/parser-cases.json     casos de teste do formato/regra usados pelo C# e pelo TypeScript
firebase.json · .firebaserc · database.rules.json
docs/superpowers/specs/      esta spec e os mockups
```

`CLAUDE_CONTEXT.md`, `PROJECT_CONTEXT.md` e `README.md` são reescritos para a nova stack.

### 4.3 Bibliotecas

| Parte | Escolha |
|---|---|
| PC | WPF + WPF-UI, CommunityToolkit.Mvvm, H.NotifyIcon.Wpf, Microsoft.Extensions.Hosting, Serilog (arquivo, 7 dias), WeCantSpell.Hunspell + dicionário pt_BR (VERO/LibreOffice), Velopack, ícones Lucide como geometria XAML (licença ISC). Reaproveitar do App Flash: `NativeMethods`, `MonitorService`, `StartupRegistration`, `SingleInstance`, `HotkeyService`/`MessageWindow`, `SettingsService` (gravação atômica), padrão de bandeja/flyout. |
| PC → Firebase | `HttpClient` puro: Firebase Auth REST (e-mail/senha da conta do PC) + RTDB REST e streaming (SSE). Sem SDK do Google. |
| PC → Gemini | `HttpClient` na API REST do Gemini (chave do AI Studio), com saída JSON por schema. |
| Site | React + Vite + TypeScript, Firebase JS SDK 12 (Auth, Database, AI Logic, App Check), `lucide-react`, plugin de PWA (manifesto + service worker), fonte Marble. |
| APK (fase 2) | Capacitor empacotando o `web/dist`; plugins: autenticação Firebase nativa (Google), reconhecimento de voz, haptics. |
| Testes | xUnit v3 (igual ao App Flash) no C#; Vitest no site. Os dois leem `shared/parser-cases.json`. |

### 4.4 Ambiente

.NET SDK 10 (já instalado), Node LTS, Firebase CLI 15.x (já logado), Android Studio/SDK só na fase 2. O PC de desenvolvimento não tem o LED: o app precisa de um modo de teste que simule a tela 2560x768 (ex.: `--test-led` usando o monitor principal) para desenvolver e para o checklist.

## 5. O caminho de um aviso

### 5.1 Pipeline (no PC, para todas as origens)

1. **Entrada:** painel do PC, Telegram (mensagens do grupo que começam com `#`, ignorando bots e outros chats), site/APK (pedido no RTDB).
2. **Correção** (5.3) → resultado estruturado `{tipo: veículo|livre, modelo, cor, placa, motivo, texto}`.
3. **Formatação** (5.2) → texto final em MAIÚSCULAS.
4. **Regra** (5.2): obrigatórios conforme a origem e o papel.
5. **Repetido** (5.4) e **limite por pessoa** (5.4).
6. **Fila** (5.5) → **exibição** na barra (seção 6).
7. **Retorno** para quem mandou (5.6) e **histórico**.

A prévia que vem do celular é só uma sugestão: como qualquer conta Google entra e é o celular que escreve a prévia, o PC **sempre** refaz o pipeline a partir do texto original (`raw`), com regras fixas, trava de segurança e recusa de texto ofensivo. Como o PC usa o mesmo modelo e o mesmo pedido do celular, o resultado quase sempre é igual à prévia; quando muda, o texto final é o que aparece em "Seus avisos".

### 5.2 Formato e regra

- **Formato:** `MODELO COR | PLACA: ABC-1234 | MOTIVO`. Partes que faltam somem (o separador também). Tudo em MAIÚSCULAS.
- **Placa:** 7 caracteres depois de tirar espaço, hífen e pontuação. Antiga `LLL9999` → `LLL-9999`; Mercosul `LLL9L99` → `LLL-9L99` (com hífen, pedido do dono). Nas posições que só podem ser letra, `0→O, 1→I, 5→S, 8→B`; nas que só podem ser número, o inverso. A 5ª posição fica como veio (letra = Mercosul, número = antiga).
- **Cor:** palavra de uma lista de cores (preto/preta, branco/branca, prata, cinza, grafite, vermelho/vermelha, azul, verde, amarelo/amarela, bege, marrom, dourado/dourada, vinho, laranja, rosa, roxo/roxa, champanhe e variações que a IA reconhecer).
- **Modelo:** o que sobra da parte do veículo; a ordem digitada não importa (cor sempre vai depois do modelo).
- **Obrigatórios:** modelo, cor e placa. Motivo opcional.

| Origem | Faltou modelo/cor/placa |
|---|---|
| Telegram | Recusa e responde com o erro |
| Site/APK, membro | Recusa; o botão Enviar fica bloqueado na prévia; se chegar assim mesmo, o PC recusa |
| Site/APK, admin | Aceita; com placa vira o formato acima, sem placa vira **aviso livre** |
| Painel do PC | Aceita; mesma lógica do admin |

**Aviso livre:** só correção de português + MAIÚSCULAS, sem estrutura. Ex.: `PAIS DO PEDRO HENRIQUE, DA SALA KIDS, FAVOR COMPARECER À RECEPÇÃO`.

### 5.3 Correção

**Gemini (padrão):**
- PC: API REST do Gemini com chave do AI Studio (grátis), modelo da família Flash/Flash-Lite que estiver no plano grátis (configurável em Avançado; conferir a lista grátis na hora de construir — em 2026-10-19 a página de preços mostrava Gemini 3.8 Flash, 3.6 Flash, 3.5 Flash-Lite e 3.1 Flash-Lite no plano grátis).
- Site/APK: Firebase AI Logic (Gemini Developer API) com App Check; prévia com espera de ~700 ms depois que a pessoa para de digitar.
- Pedido em JSON com schema: o modelo corrige ortografia, acentos e concordância, extrai `modelo/cor/placa/motivo`, diz se é aviso livre e **não inventa nada**.
- **Trava de segurança:** se a placa da IA não bater com a placa achada no texto original (após normalizar), ou se o JSON vier inválido, ou se o texto da IA trouxer palavras que não existem no original além de correções, usa o resultado offline.
- Tempo máximo 5 s (o primeiro pedido depois de uns minutos parado demora mais; a conexão fica aquecida, sem pedidos extras, e o cliente HTTP espera 10 s); erro, cota estourada, sem chave ou sem internet → corretor offline, e a bolinha do Gemini fica **azul "Offline"**.
- No plano grátis, o Google pode usar os textos para melhorar os produtos dele (aceito pelo dono). Os textos são só carro, cor, placa e motivo.

**Offline (PC):** dicionário pt-BR (Hunspell) + lista do domínio (modelos de carro, cores, motivos comuns) + **regras fixas** do usuário (`texto errado => texto certo`, aplicadas por último e também quando o Gemini está ativo). Nunca mexe na placa. Só troca uma palavra quando a sugestão é segura (fora da lista do domínio, distância pequena).

**Offline (site/APK):** o mesmo parser de formato/regra em TypeScript (sem dicionário). Serve para a prévia quando a IA falha; o envio sai como `preview.by = "local"` e o PC corrige.

### 5.4 Repetido e limites

- **Repetido:** mesma placa já na fila, passando, ou exibida nos últimos 10 min (qualquer origem). Membro do site e Telegram: recusado ("Esse carro já está na fila" / "já passou há N min"). PC e admin: aviso na prévia, mas podem mandar mesmo assim.
- **Limite por pessoa (site/APK, membro):** 1 aviso a cada 20 s (garantido nas regras do banco) e no máximo 10 por hora (garantido pelo PC). Admin sem limite.
- **Expiração:** aviso que espera mais de 30 min na fila (ex.: LED desligado, PC fora do ar) expira e quem mandou é avisado. A pausa da pregação não entra nessa conta, porque cancela tudo (5.5).

### 5.5 Fila

- Um aviso por vez; urgente entra na frente. Ordem estável entre os normais.
- **Pausar = pregação** (vem do Holyrics pela API local quando entra o slide da pregação, ou do botão Pausar no PC): **cancela na hora** o aviso atual e toda a fila, porque depois da pregação as pessoas já estão indo embora. Enquanto a barra estiver pausada, avisos novos são recusados na hora com o motivo "pregação". Retomar libera os envios de novo, com a fila vazia.
- Próxima / Encerrar atual; subir/descer/remover itens; limpar a fila (apaga direto, sem confirmação).
- Histórico (últimos 50): exibidos ✅, recusados ❌ (com motivo), cancelados, expirados; "Exibir de novo" nos exibidos.
- Cancelamento pelo site/APK: dono do aviso (enquanto não começou a passar) ou admin.
- A fila do PC vive na memória e é salva em disco a cada mudança (sobrevive a um reinício do app).

### 5.6 Retorno para quem mandou

| Evento | Telegram (resposta no grupo) | Site/APK ("Seus avisos") |
|---|---|---|
| Na fila | — | "Nº na fila" |
| Começou a passar | — | "passando" |
| Exibido | `✅ Mensagem exibida no telão.` + texto (também para avisos do site, aprovado) | "✅ hh:mm" |
| Faltou campo | `❌ Aviso não foi para o telão. Faltou {campos}. Mande assim: #corolla prata abc1d23 vidro aberto` | "❌ faltou {campos}" |
| Repetido | `⚠️ Esse carro já está na fila.` / `já passou há N min` | "repetido" |
| LED desligado | `⏳ Telão desligado. O aviso está na fila e passa quando ele ligar.` (uma vez por aviso) | pílula "Telão desligado" + "na fila" |
| Expirado | `⌛ O aviso expirou sem passar no telão.` | "expirou" |
| Cancelado | — | "cancelado" |
| Pausa (pregação) | `⚠️ O pastor iniciou a pregação. O overlay foi pausado para não atrapalhar o culto.` (texto atual; os avisos pendentes foram cancelados) | "cancelado: pregação"; pílula "Barra pausada"; Enviar bloqueado |
| Aviso novo durante a pausa | `⏸️ Pregação em andamento: o telão não está mostrando avisos.` | Enviar bloqueado com essa mensagem |

## 6. Barra no LED (overlay)

- **Qual tela:** monitor ativo com resolução física exata 2560x768 (app com DPI por monitor v2). Se houver mais de um, o último usado (caminho do dispositivo, como no App Flash), senão o primeiro que não é o principal. Reavaliar em mudança de monitores. Sem LED: barra escondida, estado "LED desligado".
- **Janela:** sem borda, sempre no topo (reafirmar periodicamente como o App Flash), sem foco, clique passa através, fora da barra de tarefas, `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` **sempre**. Se a exclusão falhar, registrar erro e mostrar a bolinha da Barra em vermelho com a dica.
- **Visual:** fundo `#000000`, texto `#FFF7E8`, Marble Bold, espaçamento entre letras ~0,08em, margem lateral 24 px, posição topo (padrão) ou rodapé.
- **Tamanho:** altura 28–120 px (padrão 56); letra = 64% da altura, salvo ajuste avançado separado.
- **Velocidade:** 30–300 px/s (padrão 90).
- **Animação:** a barra desce do topo (ou sobe do rodapé) com fade em 320 ms (ease-out), o texto entra pela direita e sai pela esquerda **uma vez**, a barra sai do mesmo jeito. Sem "flash" do texto antes da rolagem.
- **Lisura:** posição calculada pelo tempo do quadro (não por passos fixos), texto renderizado uma vez e só movido. Sugestão: janela WPF com transparência (camada) e `CompositionTarget.Rendering`. Se engasgar no PC da igreja, trocar só o renderizador por DirectComposition/Direct2D (ver riscos).
- **Mostrar barra 3 s no LED:** botão em Configurações para testar posição e tamanho (texto de exemplo, não entra na fila).
- **Convivência com o App Flash:** as duas janelas são topmost na mesma tela; não brigar por cima. Se a camada do flash cobrir a barra por instantes, aceitável.

## 7. App do PC (interface)

Identidade: tema escuro, verde `#1DA35F`, fonte Marble, ícones Lucide em uma cor só. Tudo salva sozinho (sem botão Salvar), com o selo "✓ Salvo" no cabeçalho da aba. Dicas sempre em (i) com balão.

### 7.1 Bandeja e painel rápido (1 clique)

- Linha 1: ícones Telegram (avião), Site (globo) e LED (monitor), cada um com bolinha de estado; à direita a pílula de estado (Pronta / Exibindo / Pausada / LED desligado).
- "No telão agora": texto, origem, barra de progresso, "Fila: N · próxima: …". Sem mensagem: espaço vazio, sem texto.
- Campo "Novo aviso" + (i) com atalhos (Enter envia, Ctrl+Enter urgente, Esc limpa, Ctrl+Alt+Espaço abre o painel).
- Prévia "Vai aparecer assim" (verde), "aviso livre" (azul) ou aviso de repetido.
- Uma linha de botões: **Enviar** (com texto) + Urgente, Pausar/Retomar e Abrir janela (só ícones, com balão).
- Fecha ao clicar fora (não fixa).

### 7.2 Janela (2 cliques)

- Lateral: logo + "Alerta-IBT", abas **Início, Correção, Configurações**; embaixo, status pequeno e discreto (fonte light): Telegram, Site, LED, Barra, Gemini. Verde = ok, amarelo = reconectando/pausada, vermelho = fora; Gemini fica **azul "Offline"** quando a correção está no offline. Balão com o detalhe ao passar o mouse.
- **Início:** mensagem atual (com "Corrigido. Original: …" quando mudou), Próxima, Encerrar atual, Pausar/Retomar; campo de mensagem com prévia, Enviar, Enviar urgente, Limpar; Fila (com origem: Telegram/Site/PC, ↑ ↓ ✕, limpar); Histórico (✅/❌ com motivo, ↻).
- **Correção:** liga/desliga a correção automática; escolha Gemini ou Só offline; chave do Gemini (cadeado, guardada criptografada); regras fixas (uma por linha). Sem textos explicativos (só no (i)).
- **Configurações:** Barra no LED (posição, tamanho, velocidade, avançado: letra separada, margem, cores, animação) com prévia ao vivo e "Mostrar barra 3 s no LED"; Telegram (token e chat ID com cadeado; (i) explica o `#`); Sistema (atalho do painel, "Esconder da live: sempre ligado", site conectado); **Diagnóstico** recolhido (logs com copiar/exportar/limpar).

### 7.3 Sistema

- Abre com o Windows sempre (Run do usuário, `--tray`), instância única (segunda abertura mostra a janela).
- Configurações em `%AppData%\AlertaIBT\settings.json` (gravação atômica; arquivo corrompido vira cópia `settings.bad-*.json` e volta ao padrão). Token do Telegram, chave do Gemini e senha da conta do PC criptografados com DPAPI (usuário atual).
- Logs Serilog em `%AppData%\AlertaIBT\logs` (7 dias) + visão no Diagnóstico.
- API HTTP local **igual à atual** (porta 8787, só 127.0.0.1): `GET /overlay/status`, `GET|POST /overlay/on`, `GET|POST /overlay/off`, mais o fallback que entende `on/off/pause/resume/enable/disable` no caminho, para não quebrar o gatilho do Holyrics.
- Se encontrar o Alerta-IBT antigo (Electron) aberto ou no início do Windows: aviso único explicando que os dois juntos duplicam o Telegram e brigam pela porta 8787, com botão para tirar o antigo do início do Windows.

## 8. Telegram

- Bot por **long polling** (`getUpdates` com espera longa) só no PC; um único consumidor.
- Aceita só o chat configurado, ignora bots, só mensagens que começam com `#` (fixo). O `#` não aparece no telão.
- Passa pelo pipeline completo (regra de membro: modelo, cor e placa obrigatórios).
- Status: verde (último contato ok), amarelo (reconectando), vermelho (sem token/chat ou várias falhas seguidas). Erros repetidos são agrupados no log.

## 9. Firebase

### 9.1 Projeto

- Projeto novo **"Alerta IBT"** no plano grátis (Spark). Site de Hosting com ID `alertaibt` (`alertaibt.web.app`); se o ID do projeto `alertaibt` estiver livre, usar ele; senão criar o projeto com outro ID e um site extra `alertaibt`.
- Serviços: Authentication (Google para pessoas; e-mail/senha só para a conta do PC), Realtime Database, Hosting, AI Logic (Gemini Developer API), App Check (reCAPTCHA no site).
- Provedores de login configurados em `firebase.json` (`auth.providers`), como no Metrônomo.

### 9.2 Dados (Realtime Database)

```
/users/{uid}      { name, email, role: "member"|"admin", blocked, createdAt, lastSeenAt, lastSentAt, sentCount }
/devices/{uid}    true                                   ← conta do PC (gravado na instalação)
/requests/{id}    { uid, name, raw, urgent, createdAt,
                    preview: { text, kind: "vehicle"|"free", plate, by: "gemini"|"local" },
                    cancel: false,
                    status: { state, position, reason, text, at } }
                    state = pending | queued | showing | shown | rejected | cancelled | expired
/live             { heartbeatAt, version, led, paused, gemini,
                    now: { id, text, source, startedAt } | null,
                    queue: [ { id, text, source, urgent, plate } ],
                    recent: [ { id, text, state, at, plate } ],
                    bar: { position, height, fontSize, speed } }
```

- O PC lê `/requests` por streaming, processa os `pending`, escreve `status` e publica `/live` a cada mudança e um heartbeat a cada 30 s. O site mostra "PC offline" se o heartbeat tiver mais de 90 s.
- Pedidos antigos (mais de 24 h) são apagados pelo PC.

### 9.3 Regras de segurança (resumo)

- Ler `/live`: qualquer pessoa logada e não bloqueada.
- Criar `/requests/{id}`: logado, não bloqueado, `uid == auth.uid`, texto até 300 caracteres, junto com `users/{uid}/lastSentAt` no mesmo envio, que só aceita se o anterior tiver mais de 20 s (admin dispensado).
- `cancel`: dono ou admin. `status`: só a conta do PC.
- `/users/{uid}`: a própria pessoa cria o seu registro como `member` (não pode se dar admin nem se desbloquear); admin muda `role` e `blocked` dos outros.
- `/devices`: só leitura para o PC; gravado pela CLI na instalação.
- **Primeiro admin:** o dono entra uma vez no site e a instalação (CLI) grava `role: "admin"` no registro dele.

### 9.4 App do PC no Firebase

Conta de e-mail/senha própria do PC (criada na instalação), login pela API REST do Auth, token renovado sozinho, streaming SSE do RTDB com reconexão e espera crescente. Bolinha "Site": verde conectado, amarelo reconectando, vermelho sem conexão por mais de 60 s ou sem configuração.

## 10. Site AlertaIBT.web.app (PWA)

- React + Vite + TS, mobile-first, escala da interface **90%** do padrão, tema segue o sistema (claro e escuro desenhados), fonte Marble, ícones Lucide.
- **Entrar:** botão "Entrar com Google"; qualquer conta entra; bloqueado vê "Seu acesso foi removido".
- **Tutorial** de 3 telas no primeiro acesso (como escrever; o que é obrigatório; acompanhar e urgente). Guardado no aparelho.
- **Enviar:** campo de uma linha (com microfone pequeno e apagado no canto; usa o reconhecimento de voz do navegador e some onde não houver suporte), prévia com IA ("Corrigindo com IA…" → "Vai aparecer assim" / "Não vai para o telão: FALTA …" / "Esse carro já está na fila" / "aviso livre" para admin), **Enviar** + ícone **Urgente** (segurar 1 s, enchendo, com vibração; toque rápido mostra a dica), "Seus avisos" com estados e X para cancelar.
- **Status:** pílulas PC online / LED / Barra, **mini-telão** com a barra passando (posição, tamanho e velocidade vindos de `/live/bar`), fila e recentes.
- **Equipe** (admin): lista de quem já entrou (nome, e-mail, último uso, total de avisos, selo "novo"), tornar/tirar admin, remover (bloquear). "Sair" fica aqui; para membros, "Sair" fica no avatar.
- Sem sinal: erro na hora e a pessoa tenta de novo. Sem push.
- Desempenho: app instalado abre do cache; código separado por aba; meta de primeira carga em 4G abaixo de 3 s.

## 11. APK Android (fase 2)

- **Independente do site:** o APK leva o app inteiro dentro dele (o mesmo `web/dist` empacotado pelo Capacitor) e fala direto com o Firebase (Auth, RTDB, AI Logic). Desligar o Hosting não afeta o APK.
- Mesmas telas e regras do site. Login Google **nativo**, voz nativa, vibração nativa.
- **Distribuição:** APK assinado nas Releases do GitHub; o app checa se há versão nova e oferece baixar. A chave de assinatura fica com o dono, com cópia de segurança (perder a chave impede atualizar o app instalado).
- **App Check no APK:** o Play Integrity só atesta apps instalados pela Play Store. Fora da loja, a prévia com IA no APK fica sem App Check. Padrão: o APK usa o parser local na prévia e o PC faz a correção com Gemini (`preview.by = "local"`). Rever na fase 2 se houver forma grátis de manter a IA no celular com segurança.
- Android mínimo e detalhes do Capacitor ficam para quem constrói.

## 12. Falhas e recuperação

| Falha | Comportamento |
|---|---|
| LED desligado / some no meio | Barra some; aviso atual volta para a frente da fila; fila espera; avisa quem mandou; volta sozinho quando o LED reaparece. |
| Barra trava ou a janela cai | Recriar a janela e continuar do aviso atual (como o app atual faz). |
| Sem internet | Telegram e site em vermelho/amarelo com reconexão automática; PC continua mostrando o que já está na fila; correção cai no offline. |
| Gemini falha/cota | Offline, bolinha azul; volta sozinho. |
| PC desligado | Site mostra "PC offline"; pedidos ficam `pending`; ao voltar, o PC processa os de até 30 min e expira os mais velhos. |
| Configuração corrompida | Volta ao padrão e guarda cópia. |
| App fecha inesperadamente | Instância única + início com o Windows; fila salva em disco é recarregada. |
| Exclusão de captura falha | Erro no log e bolinha da Barra vermelha. |

## 13. Instalação, atualização e troca

- **Instalador Velopack** com atualização automática pelas Releases do GitHub do mesmo repositório; versão **8.0.0**. Build self-contained com ReadyToRun (não depende de runtime instalado).
- **Firebase:** criação do projeto, site, banco, regras, provedores e conta do PC pela CLI (com confirmação do dono antes de criar).
- **Troca:** configurar do zero (token, chat ID, chave do Gemini). O Electron continua instalado até o teste na igreja; o app novo avisa se o antigo estiver aberto. Depois do teste, desinstalar o Electron.
- **Release do site:** `firebase deploy` (hosting, regras, auth).

## 14. Testes

- **Automáticos (Core, xUnit v3):** formato e placas (antiga, Mercosul, trocas O/0 I/1 S/5 B/8, espaços e hífens), obrigatórios por origem/papel, aviso livre, repetido 10 min, limite por pessoa, expiração 30 min, fila (urgente, pausa, pular, reordenar, remover, histórico), parsing do Telegram (`#`, bots, chat errado), regras fixas, trava de segurança da IA (placa trocada, JSON inválido, tempo esgotado).
- **Casos compartilhados:** `shared/parser-cases.json` (entrada → saída esperada) roda no xUnit e no Vitest, garantindo que PC e site formatam igual.
- **Site:** Vitest para parser e estados; teste de ponta a ponta (Playwright) do fluxo enviar → status com o emulador do Firebase.
- **Modo de teste do PC:** `--test-led` usa o monitor principal como LED; simulador simples de pedidos do site para desenvolver sem o celular.
- **Checklist na igreja:** (1) abre sozinho na bandeja ao ligar o PC; (2) acha o LED 2560x768; (3) barra no topo, acima do Holyrics, sem roubar foco; (4) não aparece no OBS nem no NDI; (5) rolagem lisa; (6) Telegram: aviso certo, aviso sem placa (erro), repetido; (7) site: membro e admin; (8) desligar o LED com aviso na fila e religar; (9) slide da pregação no Holyrics pausa a barra: fila cancelada, mensagem no Telegram, avisos novos recusados; retomar libera; (10) convivência com o App Flash; (11) tirar o cabo de rede (status e offline) e voltar.

## 15. Fora de escopo (v8)

Notificação push, fila offline no celular, câmera para ler placa, modo uma mão, repetição automática, cara especial para urgente, modelos rápidos, publicação na Play Store, app iOS nativo, mais de um LED, mudar a pausa automática por horário.

## 16. Riscos e validações

1. **NDI/OBS:** a exclusão de captura funciona com captura do Windows (Graphics Capture e Desktop Duplication); validar NDI Screen Capture e OBS na igreja (o App Flash ainda não foi validado lá).
2. **Lisura da barra em WPF com transparência** no hardware da igreja; plano B: DirectComposition/Direct2D só no renderizador.
3. **Plano grátis do Gemini:** limites e modelos mudam; o app nunca depende dele (offline sempre pronto). Conferir os modelos grátis na hora de construir.
4. **AI Logic no plano Spark** e App Check no site: confirmar na criação do projeto; se não der, a prévia do site usa o parser local e o PC corrige.
5. **Acesso aberto** (qualquer conta Google): mitigado por limite por pessoa, regra de obrigatórios, bloqueio pelo admin e correção (texto ofensivo deve ser recusado pela IA, ver 17).
6. **iPhone:** login Google no PWA instalado e reconhecimento de voz variam por versão; o microfone some se não houver suporte.
7. **ID `alertaibt`** pode estar ocupado; ter nome alternativo pronto (`alerta-ibt`).
8. **Telegram:** o bot precisa continuar podendo ler o grupo (privacidade/admin), como hoje.
9. **APK fora da loja:** sem Play Integrity (ver 11).

## 17. O que fica para quem constrói

Estrutura interna das classes e do MVVM; texto exato do prompt e do schema do Gemini (seguindo 5.3); como detectar e recusar texto ofensivo pela IA; detalhes da lista do domínio do corretor offline; organização do React (rotas, estado); estratégia de cache do service worker; nomes das chaves no `settings.json`; Android mínimo do APK; formato exato dos logs.

## 18. Decisões que eu (Claude) tomei — conferir

1. **Realtime Database** (não Firestore) entre PC e site: o C# fala com ele por REST/streaming sem SDK pesado e sem chave de serviço no PC.
2. Site em **React + Vite + TypeScript**, igual aos outros apps do dono.
3. **Velopack** para instalador e atualização automática pelo GitHub.
4. Telegram por **long polling** (mais rápido e com menos chamadas que perguntar a cada 3 s).
5. **Expiração em 30 min** para aviso parado na fila.
6. **Limite por pessoa** no site: 1 a cada 20 s e 10 por hora (admin sem limite).
7. Repetido: **PC e admin** só são avisados e podem mandar mesmo assim; membro e Telegram são recusados.
8. **Conta própria do PC** no Firebase (e-mail/senha) e primeiro admin gravado na instalação.
9. Pedido do site: o PC **confere tudo de novo** a partir do texto original (segurança contra prévia forjada); quase sempre sai igual à prévia, e o texto final aparece em "Seus avisos".
10. **Fila salva em disco** para sobreviver a reinício do app.
11. **Histórico de 50** avisos e pedidos do banco apagados depois de 24 h.
12. **Qualquer pausa** (Holyrics ou botão Pausar do PC) é tratada como pregação: cancela tudo, recusa novos e manda o aviso no Telegram; retomar é silencioso.
13. Aviso único quando o Electron antigo estiver aberto, com botão para tirá-lo do início do Windows.
14. **APK:** prévia local (sem IA) por padrão, PC corrige com Gemini, até achar forma grátis e segura de usar IA fora da Play Store.
15. Build **self-contained ReadyToRun** (abre rápido e não exige .NET instalado na igreja).
16. **Conta do PC no site:** criada por mim na instalação; e-mail e senha colados uma vez em Configurações > Sistema (com cadeado, guardados criptografados).
17. **Próxima / Encerrar atual** contam como exibido (✅), como o app atual faz.
18. **Atualização do app:** baixa em segundo plano e aplica no próximo início do app (ou num botão "Reiniciar para atualizar" no Diagnóstico); nunca reinicia sozinho durante um culto.
19. **App Check** obrigatório só no AI Logic (prévia com IA do site); o banco é protegido por login + regras, porque o PC (REST) não consegue gerar token de App Check.
20. **APK em plano separado**, depois que o PC e o site estiverem prontos e testados na igreja.
