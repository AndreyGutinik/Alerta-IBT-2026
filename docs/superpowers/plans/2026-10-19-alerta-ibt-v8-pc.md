# Alerta-IBT 8 — Plano 1: app do PC (C#) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** App do PC em C# que substitui o Electron v7: barra no LED 2560x768 fora da captura, fila, Telegram, correção (Gemini grátis + offline), painel na bandeja, janela, API local e instalador com auto-update.

**Architecture:** `AlertaIBT.Core` (net10.0, sem WPF) concentra toda a lógica testável: placas, parser/formato/regra, regras fixas, fila, correção (pipeline, Gemini REST, offline Hunspell), Telegram, configurações, linha do tempo da barra e escolha do LED. `AlertaIBT.App` (net10.0-windows, WPF + WPF-UI) é a cola fina: janelas, bandeja, Win32, HTTP local. Peças de plataforma são adaptadas do App Flash (`C:\Users\gutni\Documents\VS_Code_Apps\Igreja\App Flash\src\AppFlash.App\Platform\*`). Firebase/site ficam no Plano 2 (a bolinha "Site" fica cinza "não configurado" até lá); APK no Plano 3.

**Tech Stack:** .NET 10, WPF, WPF-UI 4.x, CommunityToolkit.Mvvm, H.NotifyIcon.Wpf, Microsoft.Extensions.Hosting, Serilog, WeCantSpell.Hunspell, Velopack, xUnit v3 + Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `docs/superpowers/specs/2026-10-19-alerta-ibt-csharp-design.md` (ler junto; mockup `docs/superpowers/specs/mockups/painel-v7.html`).

## Global Constraints

- Alvos: `net10.0` (Core e testes), `net10.0-windows` + `UseWPF` (App). `global.json` na raiz: SDK `10.0.100`, `rollForward: latestFeature`, `test.runner: Microsoft.Testing.Platform`. Testes: `dotnet test --project desktop/tests/AlertaIBT.Core.Tests [--filter-class "*X*"]`.
- Barra: só no monitor físico **2560x768**; posição padrão **topo**; altura **56** (28–120); letra **64%** da altura; velocidade **90 px/s** (30–300); fundo `#000000`, texto `#FFF7E8`; entrada/saída **320 ms** ease-out; passa **1 vez**; `WDA_EXCLUDEFROMCAPTURE` **sempre**.
- Texto final sempre **MAIÚSCULAS**; formato `MODELO COR | PLACA: ABC-1234 | MOTIVO`; Mercosul exibida `ABC-1D23`.
- Obrigatórios modelo, cor, placa para Telegram e membro do site; PC e admin aceitam tudo (sem placa = aviso livre).
- Telegram: prefixo fixo `#`; textos de resposta exatamente os da seção 5.6 da spec.
- Repetido: mesma placa na fila/passando/exibida há ≤ **10 min**. Expiração: **30 min** na fila. Histórico: **50**. Pausa = pregação: cancela tudo e recusa novos até retomar.
- Gemini: timeout **3 s**; chave e modelo configuráveis; falha → offline.
- API local: `127.0.0.1:8787`, rotas e fallback iguais a `src/main.js` v7 (`/overlay/status|on|off`).
- Arquivos: `%AppData%\AlertaIBT\settings.json`, `%AppData%\AlertaIBT\queue.json`, logs `%AppData%\AlertaIBT\logs` (7 dias). Segredos (token Telegram, chave Gemini) com DPAPI (CurrentUser).
- Início com o Windows **sempre** (HKCU Run, valor `AlertaIBT`, `"<exe>" --tray`), sem opção na UI.
- UI em pt-BR, tema escuro, verde `#1DA35F`, fonte Marble (`assets/fonts/*.ttf`), ícones Lucide (geometrias XAML), autosave com debounce **600 ms**, dicas em (i).
- Versão **8.0.0**; Velopack + Releases de `AndreyGutinik/Estacionamento_IBT`.

## Review Focus

1. Mensagem com quebras de linha, emoji ou muito longa (> 300 caracteres) → vira uma linha só, sem travar a barra (teste na Task 2).
2. LED some no meio de uma rolagem → aviso volta para a frente da fila e nada quebra (teste na Task 4 + verificação na Task 12).
3. Outro programa consumindo o mesmo bot (Electron aberto) → HTTP 409 no `getUpdates` vira status "conflito" com dica, sem processar duas vezes (teste na Task 8).
4. Gemini devolve JSON bonito com placa trocada ou palavra inventada → cai no offline (teste na Task 5).
5. `settings.json` corrompido, incompleto ou com valores fora da faixa → padrão, cópia `settings.bad-*.json` e valores presos na faixa (teste na Task 9).

---

### Task 1: Solução, projeto Core e placas

**Files:**
- Create: `global.json`, `desktop/Directory.Build.props` (igual ao App Flash: LangVersion latest, Nullable, ImplicitUsings, Deterministic), `desktop/AlertaIBT.slnx`, `desktop/src/AlertaIBT.Core/AlertaIBT.Core.csproj`, `desktop/tests/AlertaIBT.Core.Tests/AlertaIBT.Core.Tests.csproj` (pacotes como `App Flash/tests/AppFlash.Core.Tests`: Microsoft.NET.Test.Sdk, xunit.v3, xunit.runner.visualstudio; + Microsoft.Extensions.TimeProvider.Testing)
- Create: `desktop/src/AlertaIBT.Core/Alerts/PlateParser.cs`, `desktop/tests/AlertaIBT.Core.Tests/Alerts/PlateParserTests.cs`

**Interfaces:**
- Produces: `static string? PlateParser.Normalize(string candidate)`; `static bool PlateParser.TryExtract(string text, out string plate, out string rest)` (remove a placa e a palavra "placa"/"placa:" de `rest`).

- [ ] **Step 1: Write the failing tests** — `[Theory]` `Normalize`: `"abc1234"→"ABC-1234"`, `"abc-1d23"→"ABC-1D23"`, `"QWE 1B23"→"QWE-1B23"`, `"gbr- 9003"→"GBR-9003"`, `"a8c1234"→"ABC-1234"` (8→B em posição de letra), `"abcI234"→"ABC-1234"` (I→1 em posição de número), `"abc-1o23"→"ABC-1O23"` (5ª posição fica como veio), `"abc123"→null`, `"corolla"→null`. `TryExtract("gol branco placa ffo4745 farol aceso")` → `true`, `"FFO-4745"`, rest `"gol branco farol aceso"`; `TryExtract("corolla prata vidro aberto")` → `false`.
- [ ] **Step 2: Run** `dotnet test --project desktop/tests/AlertaIBT.Core.Tests --filter-class "*PlateParserTests*"` → FAIL (não compila).
- [ ] **Step 3: Implement** — 7 caracteres alfanuméricos após limpar; posições 0–2 letras (`0→O,1→I,5→S,8→B`), 3/5/6 números (`O→0,I→1,S→5,B→8`), 4 letra ou número; `TryExtract` procura candidatos `[A-Za-z]{3}\s*-?\s*[0-9OoIiSsBb][0-9A-Za-z][0-9OoIiSsBb]{2}` delimitados por espaço/pontuação.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(core): solução .NET 10 e normalização de placas antiga/Mercosul`

### Task 2: Parser, formato e regra (+ casos compartilhados)

**Files:**
- Create: `desktop/src/AlertaIBT.Core/Alerts/{ParsedAlert.cs, Vocabulary.cs, AlertParser.cs, AlertFormatter.cs, AlertRules.cs}`, `shared/parser-cases.json`, `desktop/tests/AlertaIBT.Core.Tests/Alerts/{AlertParserTests.cs, SharedCasesTests.cs}` (o csproj de testes copia `../../../shared/parser-cases.json` para a saída)

**Interfaces:**
- Consumes: `PlateParser.TryExtract`.
- Produces: `sealed record ParsedAlert(string Model, string Color, string? Plate, string Reason, string FreeText)`; `enum AlertSource { Pc, Telegram, Site }`; `enum SenderRole { Member, Admin, Operator }`; `static ParsedAlert AlertParser.Parse(string raw)`; `static string AlertFormatter.Format(ParsedAlert a)` (veículo) e `static string AlertFormatter.FormatFree(string text)`; `static IReadOnlyList<string> AlertRules.Missing(ParsedAlert a)` (valores `"modelo"`, `"cor"`, `"placa"`, nessa ordem); `static RuleDecision AlertRules.Decide(ParsedAlert a, SenderRole role)` com `enum RuleKind { Vehicle, Free, Reject }` e `sealed record RuleDecision(RuleKind Kind, IReadOnlyList<string> Missing)`.

- [ ] **Step 1: Write the failing tests** — `Parse`+`Format`: `"#creta preto | placa:gbr- 9003 / farol aceso"` → `"CRETA PRETO | PLACA: GBR-9003 | FAROL ACESO"`; `"prata corolla bra2e19 vidro aberto"` → `"COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO"`; `"onix preto qwe1b23"` → `"ONIX PRETO | PLACA: QWE-1B23"`; `"hb20 vermelho abc1234 parado em frente à garagem"` → `"HB20 VERMELHO | PLACA: ABC-1234 | PARADO EM FRENTE À GARAGEM"`. `Decide`: `"gol branco farol aceso"` Member → `Reject` com `["placa"]`; `"corolla abc1234 vidro aberto"` → `["cor"]`; `"prata abc1d23"` → `["modelo"]`; Operator e Admin sem placa → `Free`; com tudo → `Vehicle`. Review Focus 1: `"gol branco\nffo4745\n\nfarol aceso 🚗"` vira uma linha; texto de 400 caracteres é aceito e `FormatFree` não quebra (só colapsa espaços). `SharedCasesTests` lê `parser-cases.json` (`[{ "raw", "role", "expectedText", "missing", "kind" }]`) e confere todos (incluir os casos acima).
- [ ] **Step 2: Run** `--filter-class "*AlertParserTests*"` e `"*SharedCasesTests*"` → FAIL.
- [ ] **Step 3: Implement** — `Vocabulary.Colors` e `Vocabulary.ReasonStarts` com as listas da spec 5.2 (cores) e os gatilhos de motivo de `src/main.js` (`farol, vidro, estacionad, parad, porta, pisca, luz, alarme, som, pneu, dono, comparecer, bloqueando`); segmentos por `| / ;` e quebra de linha; primeira parte sem gatilho = veículo; cores vão depois do modelo; `#` inicial removido; tudo em `ToUpper(pt-BR)`.
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(core): parser, formato MODELO COR | PLACA | MOTIVO e regra de obrigatórios`

### Task 3: Regras fixas e Telegram (filtro + textos)

**Files:**
- Create: `desktop/src/AlertaIBT.Core/Correction/FixedRules.cs`, `desktop/src/AlertaIBT.Core/Telegram/{TelegramDtos.cs, TelegramMessageFilter.cs, TelegramReplies.cs}`, testes `Correction/FixedRulesTests.cs`, `Telegram/TelegramMessageFilterTests.cs`

**Interfaces:**
- Produces: `static FixedRules FixedRules.Parse(string lines)` (aceita `=>`, `->`, `=`; ignora linhas inválidas e repetidas); `string FixedRules.Apply(string text)` (casamento sem acento e com espaços/hífen flexíveis, como `buildLooseLiteralPattern` de `src/main.js`); `static string? TelegramMessageFilter.TryAccept(TgMessage m, long chatId)` (retorna o texto sem o `#`, ou null para outro chat, bot, sem `#`); `static class TelegramReplies` com `Shown(string text)`, `Missing(IReadOnlyList<string> campos)`, `DuplicateQueued`, `DuplicateShown(int minutos)`, `LedOff`, `Expired`, `Paused`, `PausedRefused` — textos **exatos** da spec 5.6.

- [ ] **Step 1: Write the failing tests** — `Parse("farol acesso => farol aceso\nlixo\nparado local proibid -> parado em local proibido").Apply("FAROL  ACÉSSO")` → `"farol aceso"`; regras repetidas contam uma vez; `TryAccept`: chat certo `"#gol branco"` → `"gol branco"`; sem `#` → null; `from.is_bot` → null; outro chat → null. `TelegramReplies.Missing(["placa"])` == `"❌ Aviso não foi para o telão. Faltou placa. Mande assim: #corolla prata abc1d23 vidro aberto"`; `Paused` == `"⚠️ O pastor iniciou a pregação. O overlay foi pausado para não atrapalhar o culto."`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(core): regras fixas e filtro/textos do Telegram`

### Task 4: Fila

**Files:**
- Create: `desktop/src/AlertaIBT.Core/Queue/{QueueItem.cs, HistoryEntry.cs, EnqueueResult.cs, QueueSnapshot.cs, AlertQueue.cs}`, teste `Queue/AlertQueueTests.cs`

**Interfaces:**
- Consumes: `AlertSource`.
- Produces: `sealed record QueueItem(string Id, string Text, string? Plate, AlertSource Source, bool Urgent, DateTimeOffset CreatedAt, string? SenderId, string? Original)`; `enum HistoryState { Shown, Rejected, Cancelled, Expired }`; `sealed record HistoryEntry(QueueItem Item, HistoryState State, DateTimeOffset At, string? Reason)`; `enum DuplicatePolicy { Block, Warn }`; `EnqueueResult` com `Kind` ∈ `{ Queued, DuplicateBlocked, PausedRefused }`, `Position`, `DuplicateMinutesAgo`, `WasDuplicate`; `sealed class AlertQueue(TimeProvider clock)`: `EnqueueResult Enqueue(QueueItem item, DuplicatePolicy policy)`, `QueueItem? Current`, `QueueItem? StartNext()`, `void FinishCurrent()` (histórico `Shown`), `void ReturnCurrentToFront()`, `IReadOnlyList<QueueItem> Pending`, `bool Move(string id, int delta)`, `bool Remove(string id)`, `bool Cancel(string id)`, `void Clear()`, `IReadOnlyList<QueueItem> Pause()`, `void Resume()`, `bool IsPaused`, `IReadOnlyList<QueueItem> ExpireOld()`, `void AddRejected(QueueItem item, string reason)`, `IReadOnlyList<HistoryEntry> History`, `QueueSnapshot ToSnapshot()`, `static AlertQueue FromSnapshot(QueueSnapshot s, TimeProvider clock)`; evento `Changed`.

- [ ] **Step 1: Write the failing tests** (com `FakeTimeProvider`) — urgente entra na frente, normais em ordem; `Pause()` devolve atual + pendentes como `Cancelled` e `Enqueue` durante a pausa → `PausedRefused`; `Resume()` volta a aceitar; mesma placa pendente → `Block` dá `DuplicateBlocked`, `Warn` dá `Queued` com `WasDuplicate`; exibida há 9 min bloqueia, há 11 min não; pendente com 31 min sai em `ExpireOld()` como `Expired`, com 29 min fica; Review Focus 2: `StartNext()` + `ReturnCurrentToFront()` → item volta em primeiro e `Current` fica null; histórico guarda só 50, o mais novo primeiro; `ToSnapshot`/`FromSnapshot` preserva fila e histórico.
- [ ] **Step 2: Run** `--filter-class "*AlertQueueTests*"` → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(core): fila com urgente, pausa da pregação, repetido, expiração e histórico`

### Task 5: Pipeline de correção com trava

**Files:**
- Create: `desktop/src/AlertaIBT.Core/Correction/{ICorrector.cs, CorrectionResult.cs, CorrectionPipeline.cs}`, teste `Correction/CorrectionPipelineTests.cs`

**Interfaces:**
- Consumes: `AlertParser`, `AlertFormatter`, `PlateParser`, `FixedRules`.
- Produces: `interface ICorrector { Task<ParsedAlert?> CorrectAsync(string raw, CancellationToken ct); }` (null = não deu); `enum CorrectorKind { Gemini, Offline }`; `sealed record CorrectionResult(ParsedAlert Alert, CorrectorKind By, bool Changed, bool Refused)` (`Refused` = a IA recusou por conteúdo impróprio: `kind free` com `texto` vazio); `sealed class CorrectionPipeline(ICorrector gemini, ICorrector offline, Func<FixedRules> rules, TimeSpan timeout)` com `Task<CorrectionResult> RunAsync(string raw, bool useGemini, CancellationToken ct)` e evento `GeminiHealthChanged(bool online)`.

- [ ] **Step 1: Write the failing tests** — Gemini ok → `By=Gemini`; Gemini null/exceção/demora > timeout (`timeout` de 50 ms no teste) → `By=Offline` e `GeminiHealthChanged(false)`; Review Focus 4: Gemini devolve placa `"ABC-9999"` para raw com `"abc1234"` → Offline; Gemini devolve modelo que não está no raw (ex.: raw `"gol branco abc1234"`, IA `"Corolla"`) → Offline; regras fixas aplicadas depois do corretor; Gemini com `FreeText` vazio e sem campos → `Refused=true` (não cai no offline); `Changed` true para `"farol acesso"` corrigido e false quando só mudou formato/maiúsculas.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** (comparar palavras do raw, sem acento e sem a placa, com as do resultado; aceitar palavras novas só se forem correção próxima — distância de edição ≤ 2 — de alguma palavra do raw). **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(core): pipeline de correção com fallback e trava contra IA`

### Task 6: Corretor offline (Hunspell pt-BR)

**Files:**
- Create: `desktop/src/AlertaIBT.Core/Correction/{OfflineCorrector.cs, DomainWords.cs}`, `desktop/src/AlertaIBT.Core/Dictionaries/pt_BR.aff`, `pt_BR.dic`, `README.md` (origem: dicionário VERO do LibreOffice, licença LGPL/MPL), teste `Correction/OfflineCorrectorTests.cs`
- Modify: `AlertaIBT.Core.csproj` (pacote `WeCantSpell.Hunspell`; dicionários como `Content` copiados para a saída)

**Interfaces:**
- Produces: `sealed class OfflineCorrector : ICorrector` (`OfflineCorrector.Load(string dictionaryDir)`); `static IReadOnlySet<string> DomainWords.All` (modelos de carro comuns no Brasil, cores, motivos).

- [ ] **Step 1: Write the failing tests** — `"corolla prata abc1d23 vidro abrto"` → motivo `"vidro aberto"`; `"pais do pedro compareser na recepssao"` → livre contendo `"comparecer"` e `"recepção"`; placa intacta; `"hb20"`, `"corolla"`, `"onix"` intactos; palavra sem sugestão segura fica como está.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** (só troca palavra fora do dicionário e fora de `DomainWords` quando a primeira sugestão tem distância ≤ 2). **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(core): corretor offline pt-BR com Hunspell`

### Task 7: Cliente Gemini

**Files:**
- Create: `desktop/src/AlertaIBT.Core/Correction/{GeminiCorrector.cs, GeminiPrompt.cs}`, teste `Correction/GeminiCorrectorTests.cs` (com `HttpMessageHandler` falso)

**Interfaces:**
- Produces: `sealed class GeminiCorrector(HttpClient http, Func<string?> apiKey, Func<string> model) : ICorrector`.

- [ ] **Step 1: Write the failing tests** — requisição `POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent` com a chave no cabeçalho `x-goog-api-key` (nunca na URL), `responseMimeType: application/json` e schema `{ kind: "vehicle"|"free", modelo, cor, placa, motivo, texto }`; resposta válida → `ParsedAlert`; HTTP 429/500, JSON inválido ou sem chave → null.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** — `GeminiPrompt.System` em pt-BR: corrigir ortografia/acentos/concordância de avisos curtos de estacionamento; extrair campos; não inventar; manter a placa como veio; aviso que não é de carro → `free` com `texto` corrigido; recusar conteúdo ofensivo com `kind: "free"` e `texto` vazio (o pipeline trata vazio como recusa "conteúdo impróprio"). Modelo padrão em `AppSettings` (Task 9): conferir na hora qual Flash-Lite está no plano grátis em `https://ai.google.dev/gemini-api/docs/pricing`.
- [ ] **Step 4: Run** → PASS. **Step 5: Commit** `feat(core): correção com Gemini (REST, saída JSON)`

### Task 8: Telegram (cliente e consumidor)

**Files:**
- Create: `desktop/src/AlertaIBT.Core/Telegram/{TelegramClient.cs, TelegramPoller.cs, TelegramStatus.cs}`, teste `Telegram/TelegramPollerTests.cs`

**Interfaces:**
- Consumes: `TelegramMessageFilter`, DTOs.
- Produces: `sealed class TelegramClient(HttpClient http, Func<string?> token)` com `Task<IReadOnlyList<TgUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)` e `Task SendMessageAsync(long chatId, string text, CancellationToken ct)`; `enum TelegramState { NotConfigured, Connecting, Online, Reconnecting, Offline, Conflict }`; `sealed class TelegramPoller` com `event Action<string /*texto*/, TgMessage> MessageReceived`, `TelegramState State`, `long Offset` (persistido via callback `Action<long>`), `Task RunAsync(CancellationToken ct)` (long polling `timeout=25`, espera crescente 1→30 s em erro).

- [ ] **Step 1: Write the failing tests** — duas atualizações → dois eventos e `Offset` = último `update_id + 1`; mensagem de outro chat é ignorada mas avança o offset; HTTP 409 → `State=Conflict` (Review Focus 3); 3 falhas seguidas → `Offline`; sucesso depois → `Online`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(core): Telegram por long polling com estado e offset`

### Task 9: Configurações e intenção da API local

**Files:**
- Create: `desktop/src/AlertaIBT.Core/Settings/{AppSettings.cs, SettingsJson.cs}`, `desktop/src/AlertaIBT.Core/Http/OverlayIntent.cs`, testes `Settings/SettingsJsonTests.cs`, `Http/OverlayIntentTests.cs`

**Interfaces:**
- Produces: `sealed record AppSettings` com padrões da spec (Bar: `Position="top"`, `Height=56`, `FontSize=null` (= 64%), `Speed=90`, `Background="#000000"`, `Foreground="#FFF7E8"`, `SlideMs=320`, `PaddingX=24`; Telegram: `ChatId`, `TokenProtected` (base64 DPAPI), `Offset`; Correction: `Enabled=true`, `Mode="gemini"|"offline"`, `GeminiKeyProtected`, `GeminiModel`, `FixedRules` (texto); System: `Hotkey="Ctrl+Alt+Space"`, `LastLedDevicePath`); `static (AppSettings Settings, bool WasCorrupt) SettingsJson.Load(string? json)`; `static string SettingsJson.Save(AppSettings s)`; `static AppSettings SettingsJson.Normalize(AppSettings s)`; `enum OverlayIntentKind { Status, On, Off, NotFound }`; `static OverlayIntentKind OverlayIntent.Parse(string method, string rawUrl)` (mesmas regras de `startHttpControlServer` e `detectOverlayIntent` em `src/main.js`).
- [ ] **Step 1: Write the failing tests** — Review Focus 5: JSON inválido → padrão + `WasCorrupt`; chaves faltando → padrão; `Height=500` → 120, `Speed=5` → 30; ida e volta mantém valores. `OverlayIntent`: `GET /overlay/off` → Off; `POST /overlay/on` → On; `GET /overlay/status` → Status; `GET /overlay/pause` → Off; `GET /overlay/resume` → On; `GET /x` → NotFound.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(core): configurações tolerantes e intenção da API local`

### Task 10: Escolha do LED e linha do tempo da barra

**Files:**
- Create: `desktop/src/AlertaIBT.Core/Screens/{ScreenInfo.cs, LedSelector.cs}`, `desktop/src/AlertaIBT.Core/Overlay/BarTimeline.cs`, testes `Screens/LedSelectorTests.cs`, `Overlay/BarTimelineTests.cs`

**Interfaces:**
- Produces: `sealed record ScreenInfo(string DevicePath, string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary)` (mesma forma do App Flash); `static ScreenInfo? LedSelector.Select(IReadOnlyList<ScreenInfo> screens, string? lastDevicePath)`; `sealed class BarTimeline(double screenWidth, double textWidth, double speed, TimeSpan slide)` com `TimeSpan Total` e `BarFrame At(TimeSpan t)`; `record struct BarFrame(BarPhase Phase, double Offset /*0 visível…1 escondida*/, double Opacity, double TextX)`; `enum BarPhase { In, Scroll, Out, Done }`.
- [ ] **Step 1: Write the failing tests** — `Select`: só 2560x768 exato conta; dois LEDs → o do `lastDevicePath`, senão o primeiro não principal; nenhum → null. `BarTimeline(2560, 1000, 90, 320ms)`: `Total` = 320 ms + (3560/90) s + 320 ms; em 0 → `In`, `Offset=1`, `Opacity=0`; em 320 ms → `Scroll`, `TextX=2560`; ao fim da rolagem `TextX = -1000`; depois de `Total` → `Done`; entrada com ease-out cúbica (em 160 ms o `Offset` < 0,2).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat(core): seleção do LED 2560x768 e linha do tempo da barra`

### Task 11: App WPF: host, bandeja, sistema

**Files:**
- Create: `desktop/src/AlertaIBT.App/AlertaIBT.App.csproj` (WinExe, `net10.0-windows`, `UseWPF`, `ApplicationManifest` com DPI PerMonitorV2, ícone `assets/icons/app.ico`, fontes Marble como `Resource`; pacotes do App Flash + Velopack), `App.xaml(.cs)`, `Hosting/{AppHost.cs, AppPaths.cs, SettingsStore.cs, SecretProtector.cs}`, `Platform/` adaptados do App Flash (`NativeMethods`, `MonitorService`, `StartupRegistration` sempre ligado e nome `AlertaIBT`, `SingleInstance` com nomes `AlertaIBT`, `MessageWindow`, `HotkeyService`), `Tray/TrayService.cs`, `Theme/{AppTheme.xaml, LucideIcons.xaml}`
- Modify: `desktop/AlertaIBT.slnx`

**Interfaces:**
- Consumes: `SettingsJson`, `AppSettings`.
- Produces: `SettingsStore` (`AppSettings Current`, `void Update(Func<AppSettings, AppSettings>)` com gravação atômica e debounce 600 ms, renomeia arquivo corrompido para `settings.bad-<data>.json`), `SecretProtector.Protect/Unprotect(string)` (DPAPI CurrentUser), `TrayService` (clique → `QuickPanelRequested`, duplo clique → `MainWindowRequested`, menu Abrir/Pausar/Sair), argumentos `--tray` e `--test-led`.
- [ ] **Step 1:** `dotnet build desktop/AlertaIBT.slnx -c Release` → 0 erros, 0 avisos.
- [ ] **Step 2:** rodar o exe → ícone na bandeja; 2ª execução não abre outro processo; em Release, `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\AlertaIBT` = `"<exe>" --tray`; `%AppData%\AlertaIBT\settings.json` e `logs\` criados.
- [ ] **Step 3: Commit** `feat(app): app WPF na bandeja com início no Windows, instância única e configurações`

### Task 12: Barra no LED

**Files:**
- Create: `desktop/src/AlertaIBT.App/Overlay/{OverlayWindow.xaml, OverlayWindow.xaml.cs, OverlayController.cs}`, `desktop/src/AlertaIBT.App/Platform/LedWatcher.cs`

**Interfaces:**
- Consumes: `BarTimeline`, `LedSelector`, `MonitorService`, `SettingsStore`.
- Produces: `OverlayController.ShowAsync(string text, CancellationToken ct) → Task<bool>` (true = passou inteira; false = cancelado/LED sumiu), `Task PreviewAsync()` (3 s), `bool CaptureExcluded`; `LedWatcher` com `ScreenInfo? Led` e evento `LedChanged` (`WM_DISPLAYCHANGE` + checagem a cada 5 s; `--test-led` usa o monitor principal).
- Janela: sem borda, `AllowsTransparency`, `Topmost` reafirmado a cada 2 s, `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`, `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` após criar o HWND (falha → log de erro e `CaptureExcluded=false`); posição por `CompositionTarget.Rendering` com o tempo do quadro; texto medido uma vez.
- [ ] **Step 1:** `--test-led` + "Mostrar barra 3 s" (chamada temporária no menu da bandeja) → barra no topo do monitor principal, desce, passa, sobe; rolagem lisa.
- [ ] **Step 2:** OBS com "Captura de tela" desse monitor → a barra **não** aparece; print do Windows também não.
- [ ] **Step 3:** mudar a resolução durante a rolagem (simula LED sumindo) → `ShowAsync` retorna false e a barra some.
- [ ] **Step 4: Commit** `feat(app): barra no LED fora da captura com rolagem por tempo de quadro`

### Task 13: Serviço de avisos e API local

**Files:**
- Create: `desktop/src/AlertaIBT.App/Services/{AlertService.cs, HttpControlServer.cs, StatusHub.cs, QueueStore.cs}`

**Interfaces:**
- Consumes: `CorrectionPipeline`, `AlertRules`, `AlertQueue`, `TelegramPoller`, `TelegramClient`, `TelegramReplies`, `OverlayController`, `LedWatcher`, `OverlayIntent`.
- Produces: `AlertService.SubmitAsync(string raw, AlertSource source, SenderRole role, bool urgent, string? senderId) → Task<SubmitOutcome>` (`Queued`, `Rejected(missing)`, `Refused` (conteúdo impróprio), `Duplicate`, `PausedRefused`); `Task<PreviewResult> PreviewAsync(string raw, SenderRole role)` com `sealed record PreviewResult(string Text, RuleKind Kind, IReadOnlyList<string> Missing, bool Duplicate, CorrectorKind By)`; `void Pause()`, `void Resume()`, `void Skip()`, `void Clear()`; `StatusHub` (Telegram, Site=`NotConfigured`, LED, Barra, Gemini) com `PropertyChanged`; `QueueStore` grava `queue.json` a cada `Changed`.
- Regras: Telegram usa `SenderRole.Member` e `DuplicatePolicy.Block`; PC usa `Operator` e `Warn`; respostas no grupo com `TelegramReplies` (exibido, faltou, repetido, LED desligado uma vez por aviso, expirado, pausa); pausa por qualquer origem = `AlertQueue.Pause()` + `TelegramReplies.Paused`; Telegram durante a pausa → `PausedRefused`; `ExpireOld()` a cada 60 s; LED sumiu → `ReturnCurrentToFront()`.
- `HttpControlServer`: `HttpListener` em `http://127.0.0.1:8787/`, JSON `{ success, overlayEnabled }` como o v7; porta ocupada → log de erro e dica de app antigo aberto.
- [ ] **Step 1:** `curl http://127.0.0.1:8787/overlay/status` → `{"success":true,"overlayEnabled":true}`; `/overlay/off` → fila cancelada e mensagem de pausa no grupo de teste; `/overlay/on` → volta.
- [ ] **Step 2:** grupo de teste do Telegram: `#corolla prata abc1d23 vidro aberto` → passa na barra e chega `✅ Mensagem exibida no telão.`; `#gol branco farol aceso` → resposta de faltou placa; mesma placa duas vezes → repetido.
- [ ] **Step 3: Commit** `feat(app): serviço de avisos, respostas no Telegram e API local 8787`

### Task 14: Painel rápido e atalho global

**Files:**
- Create: `desktop/src/AlertaIBT.App/Tray/{QuickPanel.xaml, QuickPanel.xaml.cs}`, `desktop/src/AlertaIBT.App/ViewModels/QuickPanelViewModel.cs`

**Interfaces:**
- Consumes: `AlertService`, `StatusHub`, `HotkeyService`, `TrayService`.
- Layout conforme `painel-v7.html`: linha 1 com ícones Telegram/Site/LED + bolinhas e a pílula de estado; "No telão agora" (vazio sem texto quando não há aviso); campo + (i); prévia "Vai aparecer assim" com debounce 700 ms via `AlertService.PreviewAsync(raw, SenderRole.Operator)` (azul quando `By=Offline`; aviso quando `Duplicate`; "aviso livre" quando `Kind=Free`); linha `[Enviar][Urgente][Pausar/Retomar][Abrir janela]` (só Enviar com texto); Enter envia, Ctrl+Enter urgente, Esc limpa; fecha ao perder o foco; `Ctrl+Alt+Espaço` abre já com o campo focado.
- [ ] **Step 1:** atalho de qualquer programa abre o painel com foco no campo; digitar `prata corolla bra2e19 vidro abrto` mostra a prévia corrigida; Enter coloca na fila.
- [ ] **Step 2: Commit** `feat(app): painel rápido da bandeja com prévia e atalho global`

### Task 15: Janela completa

**Files:**
- Create: `desktop/src/AlertaIBT.App/Views/{MainWindow.xaml(.cs), HomePage.xaml(.cs), CorrectionPage.xaml(.cs), SettingsPage.xaml(.cs)}`, `desktop/src/AlertaIBT.App/ViewModels/{MainViewModel.cs, HomeViewModel.cs, CorrectionViewModel.cs, SettingsViewModel.cs}`

**Interfaces:**
- Consumes: `AlertService`, `StatusHub`, `SettingsStore`, `OverlayController.PreviewAsync`.
- Conforme `painel-v7.html` e spec 7.2: lateral com 3 abas e status discreto (fonte light, bolinhas; Gemini azul "Offline"); Início (atual com "Corrigido. Original: …", Próxima, Encerrar, Pausar; mensagem com prévia; fila com origem e ↑ ↓ ✕ e limpar; histórico com ✅/❌ e motivo e ↻); Correção (liga/desliga, Gemini/Só offline, chave com cadeado, regras fixas); Configurações (barra com sliders e prévia ao vivo + "Mostrar barra 3 s", Telegram token/chat com cadeado, atalho, "Esconder da live: sempre ligado", Diagnóstico recolhido com logs copiar/exportar/limpar). Tudo com autosave e selo "✓ Salvo".
- [ ] **Step 1:** mudar tamanho/velocidade atualiza a prévia na hora e persiste após reiniciar; cadeado impede editar o token; Diagnóstico mostra os logs.
- [ ] **Step 2: Commit** `feat(app): janela completa com Início, Correção e Configurações`

### Task 16: Empacotamento, saída do Electron e documentação

**Files:**
- Delete: `src/`, `package.json`, `package-lock.json`, `config.example.json`, `iniciar_app.bat`, `instalar_dependencias.bat`, `compilar_instalador.bat`, `publicar_release.bat`, `scripts/release.ps1`, `GUIA_RELEASE.md`, `dist/` (ignorado)
- Create: `desktop/src/AlertaIBT.App/Platform/LegacyAppDetector.cs` (processo `Alerta-IBT.exe` ou valor do Electron no Run → aviso único com botão para tirar do início do Windows), `desktop/build/pack.ps1` (`dotnet publish -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true` + `vpk pack --packId AlertaIBT --packVersion 8.0.0`), `docs/CHECKLIST-IGREJA.md` (checklist da spec 14)
- Modify: `.github/workflows/release.yml` (build + testes + `vpk pack` + `vpk upload github`), `README.md`, `CLAUDE_CONTEXT.md`, `PROJECT_CONTEXT.md` (nova stack, sem a regra antiga), `.gitignore` (`desktop/**/bin`, `obj`, `publish`, `Releases`)
- [ ] **Step 1:** `pwsh desktop/build/pack.ps1` gera `Setup.exe`; instalar no PC de desenvolvimento → abre na bandeja; reiniciar o Windows → volta sozinho; desinstalar limpa.
- [ ] **Step 2:** `dotnet test --project desktop/tests/AlertaIBT.Core.Tests` → tudo PASS.
- [ ] **Step 3: Commit** `chore: instalador Velopack, saída do Electron e documentação da v8`

---

**Depois deste plano:** Plano 2 (Firebase + conector do PC + site PWA) e Plano 3 (APK), escritos a partir da mesma spec.
