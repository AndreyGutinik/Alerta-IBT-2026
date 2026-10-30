# IBT Controller 8

(Antes chamado Alerta-IBT. O identificador interno, a pasta `%AppData%AlertaIBT`, o repositório e o site `alertaibt.web.app` continuam com o nome antigo, para a atualização automática e as configurações seguirem funcionando.)

Aplicativo do PC do telão da igreja. Mostra avisos (principalmente de carros no estacionamento) numa **barra no topo do LED 2560x768**, sem aparecer na transmissão (OBS, NDI ou print do Windows).

Os avisos chegam pelo **Telegram** (grupo com o bot, mensagens que começam com `#`) ou são digitados no próprio PC. Antes de ir para o telão, o texto é corrigido (Gemini grátis, com corretor offline de reserva), colocado no padrão e entra numa fila.

Escrito em C# (.NET 10 + WPF). Substitui o app Electron da versão 7 (o código antigo fica na tag `v7.0.0`).

> Esta versão é só o app do PC. O site AlertaIBT.web.app e o aplicativo de Android ainda não existem: veja [docs/PROXIMOS-PASSOS.md](docs/PROXIMOS-PASSOS.md).

## Instalar

1. Baixe o `AlertaIBT-win-Setup.exe` na página de [Releases](https://github.com/AndreyGutinik/Alerta-IBT-2026/releases) do projeto.
2. Dê dois cliques. O Windows pode mostrar "O Windows protegeu o computador" porque o instalador não é assinado: clique em **Mais informações** e depois em **Executar assim mesmo**.
3. O app abre sozinho. Não precisa instalar o .NET.
4. Ele passa a **abrir sozinho com o Windows**, direto na bandeja (o ícone perto do relógio). Não existe botão para desligar isso.
5. Se o Alerta-IBT antigo (versão 7) estiver aberto ou começar com o Windows, o app novo mostra **um aviso único** com o botão para tirar o antigo do início do Windows. Feche o antigo antes de testar o novo: os dois usam o mesmo bot do Telegram e a mesma porta 8787.

O app **sempre abre escondido na bandeja**, seja pelo Windows, pelo atalho do menu Iniciar ou da área de trabalho, ou depois de uma atualização, e nunca tira o foco do Holyrics nem do OBS. A única vez que mostra a janela é a primeira abertura logo após instalar (para você ver que funcionou). Abrir o app de novo com ele já rodando traz a janela dele para a frente. Quando você abre à mão, um aviso discreto "Alerta-IBT está na bandeja" confirma que ele iniciou; clique no ícone perto do relógio (um clique abre o painel rápido, dois cliques a janela).

**Atualizações:** ao abrir, o app espera 5 s, procura uma versão nova nas Releases do GitHub (**pré-lançamentos incluídos**: vale sempre a mais alta) e, achando, baixa e **instala e reinicia sozinho quando está ocioso**: nada na fila, nada na barra, sem pregação pausada e sem envio em andamento. Se estiver ocupado, deixa a versão baixada e instala assim que ficar ocioso (e, de qualquer jeito, na próxima abertura). Procura de novo a cada 30 minutos, então um PC que fica ligado por dias também atualiza. Reinicia com os mesmos argumentos (na bandeja, se estava na bandeja) e salva a fila antes. Em **Configurações → Sistema** há o interruptor "Atualizar automaticamente ao abrir", a versão, o botão **Verificar agora** e a linha com o resultado da última verificação. Sem internet, nada acontece e nada dá erro; uma atualização que falha não é repetida mais de 2 vezes em 24 h. Versões instaladas antes da 8.0.1 não se atualizam sozinhas para ela (veja `docs/PROXIMOS-PASSOS.md`).

**Desinstalar:** Configurações do Windows > Aplicativos > IBT Controller. Isso também tira o app do início do Windows. Suas configurações em `%AppData%\AlertaIBT` ficam guardadas (apague essa pasta para começar do zero).

## Configurar (uma vez)

Abra a janela (dois cliques no ícone da bandeja ou **Abrir**).

- **Configurações > Telegram:** cole o **token** do bot e o **ID do chat** do grupo. Os dois ficam atrás de um cadeado ("Trocar" abre para editar). O token é guardado criptografado.
- **Início > Correção (seção que abre pela seta) > chave do Gemini:** cole a chave grátis do [Google AI Studio](https://aistudio.google.com/apikey). Sem chave, o app corrige só offline (funciona, mas a bolinha do Gemini fica azul "Offline"). Atenção: no plano grátis o Google pode usar os textos enviados para melhorar os produtos dele. Os textos são só carro, cor, placa e motivo.
- **Configurações > Barra no LED:** posição (topo ou rodapé), tamanho e velocidade, com prévia ao vivo. O botão **Mostrar barra 3 s no LED** testa na tela de verdade.

Tudo salva sozinho (aparece "Salvo às hh:mm"). Não existe botão Salvar.

## Mandar um aviso pelo Telegram

No grupo do bot, escreva uma mensagem que **começa com `#`** (o `#` não aparece no telão). Para carros, informe **modelo, cor e placa**; o motivo é opcional, e a ordem não importa:

```
#corolla prata abc1d23 vidro aberto
```

Aparece no telão assim: `COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO`.

O bot responde no grupo:

| Situação | Resposta |
|---|---|
| Passou no telão | ✅ Mensagem exibida no telão. |
| Faltou modelo, cor ou placa | ❌ Aviso não foi para o telão. Faltou ... Mande assim: #corolla prata abc1d23 vidro aberto |
| Mesma placa já na fila ou exibida há menos de 10 min | ⚠️ Esse carro já está na fila. / já passou há N min |
| LED desligado | ⏳ Telão desligado. O aviso está na fila e passa quando ele ligar. |
| Ficou mais de 30 min esperando | ⌛ O aviso expirou sem passar no telão. |
| Texto grande demais (mais de 200 letras) ou impróprio | ❌ ... |
| Pregação em andamento | ⏸️ Pregação em andamento: o telão não está mostrando avisos. |

Mensagens de bots e de outros grupos são ignoradas.

## Mandar um aviso do próprio PC

- **Um clique no ícone da bandeja** (ou o atalho **Ctrl+Alt+Espaço**) abre o **painel rápido**: digite o aviso, veja a prévia ("Vai aparecer assim"), **Enter** envia, **Ctrl+Enter** envia como urgente, **Esc** limpa (e fecha). Clicar fora fecha o painel.
- **Dois cliques no ícone** abrem a janela, com a fila, o histórico, as correções e as configurações.

No PC, avisos sem placa são aceitos como **aviso livre** (só corrige o português e põe em maiúsculas). Pelo Telegram, modelo, cor e placa são obrigatórios.

O atalho pode ser trocado em Configurações > Sistema. Se outro programa já usa a combinação, o painel e a dica do ícone avisam, e o clique no ícone continua funcionando.

**Bloqueador de mouse:** em Configurações > Sistema, a chave "Travar o mouse na tela principal" (ou o atalho **Ctrl+Alt+M**, ou o item do menu da bandeja) prende o cursor no monitor principal do Windows, para ele não parar no LED nem no retorno. Apertar **Ctrl três vezes** rápido leva o cursor ao centro da tela principal, com o travamento ligado ou não. O app só conta os Ctrl: não grava o que é digitado.

**Som:** a aba **Som** põe o volume do Windows, do Holyrics, do player e dos sons do sistema ao abrir o PC e a cada vez que o programa abre, e cada controle de volume vale **na hora** (com o menu opcional **Suavizar mudança de volume** de 0,5 a 3 s); escolhe por onde sai o som (a saída padrão do Windows, travada no WING; o player, no Out 3-4; o Holyrics, no Out 1-2, pela saída por programa do Windows) e tem o **player de música** (listas = pastas, embaralhar, repetir, fade-in e fade-out ajustáveis de 0,5 a 10 s ou seco, e mescla entre músicas de 0,5 a 12 s como no Spotify, ancorada no último som da música e com a **curva escolhida no player** (Linear, Volume constante ou Intermediária, trocável na hora), ou sem mescla e sem pausa entre elas; trocar a saída do player move o som na hora, da mesma posição) com **despertadores** (horário, dias da semana e "não iniciar depois de"). O Holyrics controla o player pela API local (`/player/play`, `/player/pause`, `/player/stop`, `/player/next`, `/player/previous`, `/player/status`) e as teclas de mídia do teclado também.

**NDI no telão:** a aba **NDI** (entre Som e Configurações) recebe câmeras NDI da rede e as põe no LED, por baixo da letra do Holyrics e da barra de avisos: duas miniaturas grandes (**Prévia** e **No ar**) com o botão **Cortar** no meio, até 6 câmeras com o nome que você der (clicar manda para a Prévia; só o Cortar vai ao ar), posição vertical por câmera (o retângulo mostra a faixa que o LED recorta), barras de teste, escolha do monitor (travado no 2560x768), "esconder da live" e atalhos F1-F4 / Ctrl+Espaço. Precisa do NDI Tools (gratuito, https://ndi.video/tools/); o app não leva nenhum arquivo do NDI. NDI® is a registered trademark of Vizrt NDI AB. A API local tem `/ndi/on`, `/ndi/off`, `/ndi/take` e `/ndi/status`.

**Alertas desligáveis:** em Configurações > Sistema, a chave "Alertas ligados" liga e desliga a barra no LED, o Telegram, o site e a API do overlay de uma vez (para o templo menor, que só quer o som). Desligada, a aba Início avisa e tem o botão para ligar; `/overlay/on`, `/overlay/off` e `/overlay/status` respondem `{"success":false,"disabled":true,"message":"alertas desligados"}`.

**Limpar histórico:** o botão no cabeçalho do **Histórico** (Início) apaga, sem perguntar, o histórico do PC e do site e as mensagens do bot no Telegram com menos de 48 h (o bot precisa ser administrador do grupo com "Excluir mensagens"). Não mexe na fila nem no que está passando. Toda segunda-feira, 00:00, isso acontece sozinho (se o PC estava desligado, na próxima vez que ligar); dá para desligar em Configurações > Sistema.

## Pregação (Holyrics)

O Holyrics chama a API local do app quando entra o slide da pregação:

- `http://127.0.0.1:8787/overlay/off` pausa a barra: cancela o aviso atual e a fila, recusa avisos novos e manda uma mensagem no Telegram.
- `http://127.0.0.1:8787/overlay/on` volta ao normal (sem mensagem).
- `http://127.0.0.1:8787/overlay/status` mostra o estado.

São as mesmas rotas do app antigo; o gatilho do Holyrics não muda. O botão **Pausar** do painel e da janela faz a mesma coisa. Com os alertas desligados (Configurações > Sistema > Alertas) essas três rotas respondem `{"success":false,"disabled":true,"message":"alertas desligados"}`.

Para o fim da contagem regressiva do culto, o Holyrics também pode chamar `http://127.0.0.1:8787/player/pause`: a música do player desce em 5 s e para.

## O que significam as bolinhas

Na lateral da janela e nos ícones do painel rápido:

| Bolinha | Verde | Amarelo | Vermelho | Cinza | Azul |
|---|---|---|---|---|---|
| **Telegram** | Conectado | Conectando ou reconectando | Sem conexão, ou conflito com outro app usando o mesmo bot | Falta o token ou o ID do chat | |
| **Site** | | | | Não configurado (o site ainda não existe) | |
| **LED** | Ligado (tela 2560x768 achada) | | Desligado: os avisos esperam na fila | | |
| **Barra** | Pronta ou Exibindo | Pausada | Falha (inclui a barra não estar escondida da captura) | Escondida (sem LED) | |
| **Gemini** | Online | | | Aguardando (ainda não usado) ou Desligada (correção desligada) | **Offline**: corrigindo só offline (sem chave, sem internet ou sem cota) |

A pílula do painel diz o estado geral: **Pronta**, **Exibindo**, **Pausada** ou **LED desligado**. Passe o mouse em qualquer bolinha para ver o detalhe.

## Backup das configurações

Em **Configurações > Backup**: o ícone de baixar exporta um arquivo `.ibtbackup` e o de subir restaura. O arquivo é protegido por uma senha que você escolhe (AES-256-GCM, chave derivada com PBKDF2-SHA256 de 600 mil voltas): sem a senha ninguém abre, e uma alteração no arquivo é detectada. Guarda só token e grupo do Telegram, chave e modelo do Gemini, atalhos, barra (tamanho, velocidade, posição e tela do LED), conexão com o site (inclusive o interruptor ligado/desligado), regras fixas, "Alertas ligados" e a limpeza semanal; playlists, câmeras, volumes, saídas, player e despertadores ficam de fora. Antes de trocar qualquer coisa o app mostra o que vai mudar (segredos só com 3 letras) e pede a confirmação.

A **cópia automática** grava um arquivo novo na pasta escolhida 5 segundos depois da última mudança de um desses itens e guarda só as 5 últimas (`ibt-backup-AAAAMMDD-HHmmss.ibtbackup`). A senha dela é pedida uma vez e fica protegida pelo Windows, só neste usuário.

## Onde ficam os arquivos

Tudo em `%AppData%\AlertaIBT` (cole isso na barra do Explorador):

| Arquivo | Para quê |
|---|---|
| `settings.json` | Suas configurações. Token e chave estão criptografados (só este usuário do Windows lê). Se o arquivo estragar, o app volta ao padrão e guarda uma cópia `settings.bad-...json`. |
| `queue.json` | A fila e o histórico. Sobrevive a um reinício; a pausa não é lembrada. |
| `telegram-journal.json` | Os números das mensagens do grupo que o app viu (respostas do bot e avisos com `#`), por 48 h: só essas o **Limpar histórico** apaga do Telegram. |
| `logs\` | Registro do dia a dia (7 dias). Também aparece em Configurações > Diagnóstico, com copiar, exportar e limpar. |
| `legacy-notice-shown.txt`, `update-restart.txt` | Marcas internas (o aviso do app antigo já apareceu; o app acabou de ser reiniciado por uma atualização). Podem ser apagadas. A contagem de tentativas de instalar uma versão fica em `settings.json`. |

O programa em si fica em `%LocalAppData%\AlertaIBT`.

## Para quem desenvolve

Requisitos: Windows, .NET SDK 10 (`global.json`). Detalhes de estrutura e regras em [CLAUDE_CONTEXT.md](CLAUDE_CONTEXT.md).

```powershell
dotnet build desktop/AlertaIBT.slnx
dotnet test --project desktop/tests/AlertaIBT.Core.Tests
dotnet test --project desktop/tests/AlertaIBT.App.Tests
```

Sem o LED (PC de desenvolvimento): `AlertaIBT.exe --test-led` usa o monitor principal como se fosse o LED, e `ALERTAIBT_DATA_DIR` aponta para outra pasta de dados (para não mexer nas configurações reais). Em compilação Debug o início com o Windows e o aviso do app antigo ficam desligados.

**Instalador local** (não publica nada): `pwsh desktop/build/pack.ps1` publica self-contained ReadyToRun e roda o `vpk pack`. O `Setup.exe` e os pacotes ficam em `artifacts/Releases`.

**Release:** suba a versão em `desktop/src/AlertaIBT.App/AlertaIBT.App.csproj` e crie a tag `vX.Y.Z`. O workflow `.github/workflows/release.yml` compila, testa, empacota e publica a Release (a tag precisa bater com a versão do csproj).

## Documentos

- [docs/CHECKLIST-IGREJA.md](docs/CHECKLIST-IGREJA.md): passo a passo para testar no PC da igreja.
- [docs/PROXIMOS-PASSOS.md](docs/PROXIMOS-PASSOS.md): o que ainda falta (Firebase, site, conector, APK).
- [docs/superpowers/specs/2026-10-19-alerta-ibt-csharp-design.md](docs/superpowers/specs/2026-10-19-alerta-ibt-csharp-design.md): especificação completa.
- [docs/superpowers/plans/2026-10-19-alerta-ibt-v8-pc.md](docs/superpowers/plans/2026-10-19-alerta-ibt-v8-pc.md): plano do app do PC.

## Licença

O código deste repositório é licenciado sob a [Licença MIT](LICENSE) (© 2026 Andrey Gutinik). O dicionário pt-BR (VERO/LibreOffice) é de terceiros e segue a licença descrita em `desktop/src/AlertaIBT.Core/Dictionaries/README.md`.

O app usa bibliotecas de terceiros pelo NuGet, cada uma com a sua licença: NAudio, CommunityToolkit.Mvvm, H.NotifyIcon, WPF-UI, Velopack e Microsoft.Extensions (MIT); Serilog (Apache-2.0); WeCantSpell.Hunspell (MPL 1.1/GPL 2.0/LGPL 2.1, herdada do Hunspell); TagLibSharp (LGPL-2.1, só para ler a capa das músicas). Todas são usadas sem modificação, cada uma na sua própria DLL dentro da pasta do programa instalado.
