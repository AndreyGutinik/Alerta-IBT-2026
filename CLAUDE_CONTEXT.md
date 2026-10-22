# CLAUDE CONTEXT - ALERTA-IBT 8

Contexto rápido para o Claude Code trabalhar neste projeto. Leia este arquivo, o `PROJECT_CONTEXT.md` e a especificação antes de mexer em qualquer coisa.

## Mudança de stack (regra antiga revogada)

Até a versão 7 o app era Electron e este arquivo dizia "não troque a stack". **Essa regra foi revogada pelo dono do projeto em 2026-10-19.** A versão 8 é uma reescrita em **C# (.NET 10 + WPF)**. O código Electron saiu deste branch (está no histórico e na tag `v7.0.0`). Não recrie nada em Node/Electron.

## O que é

Aplicativo do PC do telão da igreja (PC do Holyrics, LED 2560x768): barra de avisos no topo do LED, fora da captura (OBS/NDI/print), com fila, Telegram, correção de português (Gemini grátis + offline), painel na bandeja, janela de controle e API local `127.0.0.1:8787` para o Holyrics pausar na pregação.

Quem usa: o dono (operador de luz), a equipe de projeção e voluntários que mandam avisos pelo Telegram. O PC de desenvolvimento tem um monitor 2560x1440 e **não tem o LED**: use `--test-led`.

## Stack

.NET 10 (`global.json`: SDK 10.0.100, `rollForward: latestFeature`, runner de testes Microsoft.Testing.Platform), WPF + WPF-UI 4.x, CommunityToolkit.Mvvm, H.NotifyIcon.Wpf, Microsoft.Extensions.Hosting, Serilog (arquivo, 7 dias), WeCantSpell.Hunspell + dicionário pt-BR (VERO), Velopack (instalador e atualização pelas Releases do GitHub), xUnit v3.

## Estrutura do repositório

```
desktop/
  AlertaIBT.slnx
  Directory.Build.props         LangVersion latest, Nullable, ImplicitUsings, Deterministic
  src/AlertaIBT.Core/           lógica pura, sem WPF (net10.0)
    Alerts/ Correction/ Queue/ Services/ Settings/ Telegram/ Overlay/ Screens/ Http/ Input/ Diagnostics/ Dictionaries/
  src/AlertaIBT.App/            WPF, cola fina (net10.0-windows)
    Hosting/ Platform/ Overlay/ Tray/ Services/ ViewModels/ Views/ Theme/
  tests/AlertaIBT.Core.Tests/   xUnit v3
  tests/AlertaIBT.App.Tests/    xUnit v3 (janelas reais, DI, view-models)
  build/pack.ps1                publish self-contained ReadyToRun + vpk pack
shared/parser-cases.json        casos de formato/regra (C# agora; o site em TypeScript depois)
assets/                         ícone (assets/icons/app.ico) e fontes Marble (assets/fonts/*.ttf) usados pelo App
docs/                           README de uso, CHECKLIST-IGREJA.md, PROXIMOS-PASSOS.md, superpowers/specs e plans
dotnet-tools.json               vpk 1.2.161 (mesma versão do pacote Velopack do App)
.github/workflows/release.yml   build + testes + vpk pack + vpk upload github em tag vX.Y.Z
```

Onde está a lógica: formato, placas, regra, fila, correção, Telegram, API HTTP, `AlertService`, `StatusHub` e configurações estão no **Core** (testados rápido). O **App** só tem janelas, bandeja, Win32, a barra (`Overlay/`) e os view-models.

## Onde estão a especificação e o plano

- Especificação aprovada: `docs/superpowers/specs/2026-10-19-alerta-ibt-csharp-design.md` (mockup: `docs/superpowers/specs/mockups/painel-v7.html`).
- Plano 1 (app do PC, este código): `docs/superpowers/plans/2026-10-19-alerta-ibt-v8-pc.md`. As restrições globais no topo do plano valem (versão 8.0.0, textos de resposta do Telegram da seção 5.6, valores da barra etc.).
- O que ainda falta (Plano 2: Firebase, site PWA, conector do PC; Plano 3: APK): `docs/PROXIMOS-PASSOS.md`.
- A pasta `.superpowers/` (relatórios de cada lote de implementação) é local e fica fora do git.

## Comandos

```powershell
dotnet build desktop/AlertaIBT.slnx                              # 0 avisos, 0 erros
dotnet test --project desktop/tests/AlertaIBT.Core.Tests         # Core (1334 testes na v8.0.0)
dotnet test --project desktop/tests/AlertaIBT.App.Tests          # App (417 testes na v8.0.0)
dotnet test --project desktop/tests/AlertaIBT.Core.Tests --filter-class "*AlertParserTests*"
pwsh desktop/build/pack.ps1                                      # instalador local em artifacts/Releases (não publica)
```

Os testes usam o runner nativo (Microsoft.Testing.Platform): **sem `--`** antes dos filtros.

Rodar o app sem o LED e sem mexer nas configurações reais:

```powershell
$env:ALERTAIBT_DATA_DIR = "$env:TEMP\alertaibt-dev"
dotnet run --project desktop/src/AlertaIBT.App -- --tray --test-led
```

### Variáveis de ambiente (só desenvolvimento e testes)

| Variável | Efeito |
| --- | --- |
| `ALERTAIBT_DATA_DIR` | Move a pasta de dados (`settings.json`, fila, logs) para outro lugar. |
| `ALERTAIBT_INSTANCE_NAME` | Troca o nome da instância única (mutex `Local<nome>.SingleInstance` e evento `Local<nome>.ShowWindow`; padrão `AlertaIBT`), lido em um só lugar: `SingleInstance.ResolveName()`. Quem abre o app (ou os testes) com outro nome não trava nem acorda a janela do app real da igreja. O app instalado nunca a define. |

Os testes do App (`TestIsolation`, módulo inicial) definem as duas com nomes únicos por execução, e o `WpfTestHost` desliga o `OnStartup` real (`App.RunsApplication = false`): o WPF dispara o `OnStartup` sozinho assim que o dispatcher roda, e antes isso abria o app de verdade durante os testes (tomava o mutex, acordava a janela do app aberto, mexia no `%AppData%`). Nenhum teste pode usar a porta 8787 (use uma porta livre), o atalho global real (os testes usam Ctrl+Alt+Shift+F22/F23) nem o `%AppData%AlertaIBT`.

### Início do app

O app **sempre** abre escondido na bandeja (`StartupPlan.Decide`): a janela só aparece na primeira abertura após instalar (não existia `settings.json`) e quando uma segunda abertura acorda a instância que já roda. Abertura manual (sem `--tray`) mostra um aviso "Alerta-IBT está na bandeja"; `--tray` continua aceito e o valor do Run do Windows não mudou. Nada no caminho da bandeja chama `Show`/`Activate`.

### Ícones

`desktop/build/make-icons.mjs` (`cd desktop/build; npm install; npm run icons`, ou `npm run icons` em `web/`) gera todos os ícones a partir de `assets/icons/logo.svg` (a mesma marca de `Logo_IBT/Creme Claro_3_Brand-Mark.svg`): `assets/icons/app.ico` (PNG em 16, 20, 24, 32, 40, 48, 64, 96, 128 e 256), `app-256.png`, `app-512.png` e os ícones do site em `web/public`. A cor do quadrado vem de `--bg` em `web/src/styles/tokens.css`.

## Regras de trabalho

- Mudanças pequenas e testadas (escreva o teste que falha primeiro). Build com **0 avisos**.
- Comentários e nomes em inglês no código; textos da interface, mensagens do Telegram, logs e documentação em **pt-BR**.
- **Ferramenta de escrita de arquivos:** ela converte `\uXXXX` minúsculo em caractere. Em C# use escapes de 8 dígitos (`\U000000E7`) para acentos nas strings, e confira com `grep -P '[^\x00-\x7F]'` que não sobrou byte não ASCII em `.cs`. Markdown e PowerShell podem ter UTF-8 real.
- Compilação **Debug** não registra o início com o Windows nem mostra o aviso do app antigo (o PC de desenvolvimento não pode ser alterado por um build de teste). A Release faz os dois.
- Nunca grave token ou chave em log, em texto puro ou no git. Segredos vão no `settings.json` protegidos por DPAPI (`SettingsSecrets`).
- Não faça push, não publique Release, não crie nem altere projeto no Firebase/Google Cloud sem o dono pedir.
- Mensagens de commit em pt-BR no padrão convencional (`feat(app): ...`, `chore: ...`).

## Pontos delicados

- **Barra no LED:** janela WPF com transparência, `WDA_EXCLUDEFROMCAPTURE` sempre, clique passa através, não rouba foco. Rolagem pelo tempo do quadro (`CompositionTarget.Rendering`). Se engasgar no hardware da igreja, o plano B é trocar só o renderizador (Direct2D/DirectComposition).
- **Fila e pausa:** pausar = pregação: cancela tudo, recusa novos, uma mensagem no Telegram por transição. `AlertQueue` tem um lock único; `Changed` é levantado fora do lock e em qualquer thread (nunca use `Dispatcher.Invoke` síncrono num handler).
- **Gemini:** nunca foi testado contra a API real durante o desenvolvimento (sem chave). Erro de campo na requisição só cai no offline em silêncio: a bolinha azul "Offline" depois de um aviso válido significa que o Gemini não está funcionando. A bolinha não espera o primeiro aviso: `GeminiHealthMonitor` (Core/Services) faz uma pergunta mínima ("ok", `GeminiHealthProbe`, mesmo `HttpClient` do corretor) ~8 s depois da abertura, ao mudar chave/modelo/liga-desliga (3 s de debounce) e a cada 30 min se nenhuma correção real passou nos últimos 20 min; falha repete com espera 30 s, 1 min, 2 min... até 30 min. Sem chave: cinza "Sem chave", sem chamada.
- **Atualização:** o `UpdateService` baixa em segundo plano e aplica só no próximo início do app. A auto-aplicação do próprio Velopack está desligada de propósito (`SetAutoApplyOnStartup(false)`) para a segunda abertura de um app já rodando só mostrar a janela.
- **Ícones Lucide:** as geometrias em `Theme/LucideIcons.xaml` foram escritas de memória (ISC); conferir visualmente.
- **Bloqueador de mouse:** `MouseLockService` (Core/Input) prende o cursor no monitor principal com `ClipCursor` (retângulo do `GetMonitorInfo` do principal, pixels físicos: o manifesto é PerMonitorV2) e refaz o travamento quando o Windows o solta (`MouseLockSignals`: `SystemEvents.DisplaySettingsChanged`, troca de sessão e um vigia de 1 s que só roda travado). `MouseLockController` liga isso às configurações (`MouseLock.*`), ao atalho (id 0xB12) e ao gancho `WH_KEYBOARD_LL` que só detecta Ctrl x3 (`TripleCtrlDetector`; instalado só com `TripleCtrl` ligado, passivo, nunca grava tecla). Nenhum teste chama `ClipCursor`, `SetCursorPos` nem o gancho de verdade: use `INativeCursor` e `IKeyboardHook` falsos (trocados no `ServiceCollection` depois do `AppHost.ConfigureServices`).
- **Porta 8787 e Telegram:** o app antigo (Electron) usa os mesmos; com os dois abertos aparece "Porta ocupada" e "Conflito".

## Estado

Plano 1 (app do PC) concluído na branch `v8-csharp`, versão 8.0.0, ainda **sem teste na igreja**: o roteiro está em `docs/CHECKLIST-IGREJA.md`. Falta uma revisão final do branch inteiro antes de mesclar na `main`.
