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
- **Gemini:** nunca foi testado contra a API real durante o desenvolvimento (sem chave). Erro de campo na requisição só cai no offline em silêncio: a bolinha azul "Offline" depois de um aviso válido significa que o Gemini não está funcionando.
- **Atualização:** o `UpdateService` baixa em segundo plano e aplica só no próximo início do app. A auto-aplicação do próprio Velopack está desligada de propósito (`SetAutoApplyOnStartup(false)`) para a segunda abertura de um app já rodando só mostrar a janela.
- **Ícones Lucide:** as geometrias em `Theme/LucideIcons.xaml` foram escritas de memória (ISC); conferir visualmente.
- **Porta 8787 e Telegram:** o app antigo (Electron) usa os mesmos; com os dois abertos aparece "Porta ocupada" e "Conflito".

## Estado

Plano 1 (app do PC) concluído na branch `v8-csharp`, versão 8.0.0, ainda **sem teste na igreja**: o roteiro está em `docs/CHECKLIST-IGREJA.md`. Falta uma revisão final do branch inteiro antes de mesclar na `main`.
