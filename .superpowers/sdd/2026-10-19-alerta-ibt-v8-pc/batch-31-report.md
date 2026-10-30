# Lote 31 - travamento ao ligar "Gravar etiquetas" e "crashs estranhos"

Branch `a2026`, csproj continua 8.0.4 (o lead empacota 8.0.4-pre.2). Sem push, release, firebase. NDI-SDK intacto. O app instalado (8.0.4-pre.1) nao foi tocado: so LI os logs de `%AppData%\AlertaIBT\logs` (nenhum valor secreto copiado) e o Visualizador de Eventos do Windows. `C:\Users\gutni\Music` so foi LIDO (enumerar e medir, 237 arquivos); nenhuma etiqueta foi gravada fora de pastas temporarias de FLAC feitos com ffmpeg.

## 1. O travamento

### O que a medicao mostrou
- Bancada nova `UiResponsivenessRig` (App.Tests): configuracoes reais em pasta temp, `PlaylistLibrary` com vigia de pasta real, decodificador real (MediaFoundation), varredura e gravador de etiquetas reais, `PlayerViewModel` real, uma thread de "placa de som" puxando o mixer em tempo real, tocando de verdade, e uma thread de UI propria com batimento (a cada 10 ms pede uma resposta ao dispatcher e guarda a maior espera) mais cronometro em volta de cada chamada na thread de UI.
- As quatro combinacoes (Igualar volume ligado/desligado x etiquetas ligadas/desligadas), ligando e desligando as duas chaves 4 vezes enquanto toca e a varredura roda: **maior chamada na UI 0,5 a 3,2 ms, batimento maximo 3,0 a 5,3 ms, 0 batimentos acima de 100 ms**. Com a chave das etiquetas ligada de verdade, o gravador escreveu 10/10 FLAC (16 e 24 bits, com capa) em copias temp sem segurar a UI (chamada da chave 1,8 ms, batimento 3,6 ms).
- Pasta real do dono (somente leitura): 237 arquivos enumerados em **5 ms**; trocar Igualar volume 4 vezes tocando: maior chamada 2,4 ms, batimento 4,2 ms. Chamadas reais do Windows Audio (as mesmas do player e das regras de som a cada mudanca): listar saidas 0,6 a 12 ms, padroes 2 ms, sessoes 0,6 a 8 ms.
- Conclusao honesta: com tudo saudavel o app NAO trava ao ligar a chave; o travamento do dono nao se reproduz sem um Windows Audio lento. Nao e o gravador (nao havia nenhum arquivo para gravar: nenhum `.ibt-tmp`, nenhuma data mudada, nenhuma linha de erro) nem o varredor, nem o cache.

### Causa raiz encontrada e reproduzida (travamento estrutural na thread de UI)
Mudar QUALQUER opcao e uma chamada na thread de UI que roda, em fila, todos os ouvintes de `SettingsService.Changed`. Dois deles esperavam pelo servico de audio do Windows:
1. `MusicPlayer.OnSettingsChanged` (MusicPlayer.cs, antes da linha 1191): a cada mudanca de opcao (ate "Gravar etiquetas"), dentro do lock do player, chamava `RefreshOutputLocked` = `_audio.ActiveRenderDevices()` + `GetDefaultDevices()` (enumeracao CoreAudio por COM) e esperava o lock `_gate` do `WindowsAudioSystem`.
2. `AudioController.OnSettingsChanged` e `Kick` (AudioController.cs:247-285) e a propriedade `Status` pegavam o `_gate` do controlador, que `Reconcile` segura durante o `Pass` inteiro (todo o trabalho COM: listar saidas, sessoes de cada programa, ajustar volumes). Se um programa de audio nao responde (o log mostra `javaw`/Holyrics ativo), a UI esperava ate o `Pass` terminar, e o ajuste de volume das telas lia `Status` na UI.
Reproducao: `SettingsChangeNeverBlocksTests` (Core.Tests) com um "Windows Audio travado" (gancho `OnRead` no `FakeAudioSystem`): antes da correcao a mudanca de opcao ficou presa por **3000 ms (o limite do teste)** nos dois caminhos e a leitura de `Status` tambem; depois: **< 100 ms** (os asserts sao 100 ms). Um terceiro risco do mesmo tipo: `PlayerViewModel.OnDevicesChanged` listava as saidas na thread de UI.

### Correcao na raiz
- `MusicPlayer.OnSettingsChanged` so pergunta ao Windows quando uma opcao DE SAIDA mudou (`OutputDeviceId` ou `OutputMatch`); volume, etiquetas, Igualar volume etc. nao tocam no servico de audio. Eventos de dispositivo e o botao "Atualizar" continuam atualizando a saida.
- `AudioController`: `Kick`, `OnSettingsChanged` e `Status` sem o lock longo (`_started/_disposed/_passing` viram `volatile`, `Status` por `Volatile.Read`, chave de saida em lock proprio curto, timer e thread-safe).
- `PlayerViewModel.OnDevicesChanged`: a lista e lida fora da UI (`_run`) e entregue com `_post`.
- `WindowsAudioSystem.Dispose` (achado, deadlock de saida): desregistrava o cliente de notificacao DENTRO do `_gate` enquanto o callback de dispositivo (`OnDevicesChanged -> ForgetCaches`) espera o mesmo `_gate`; um evento de dispositivo na hora de fechar travava a saida. Agora desregistra fora do lock.
- `SettingsService`: cada ouvinte e cronometrado; mais de 50 ms vira aviso no log ("Configuracao: o ouvinte X.Y levou N ms na thread que mudou a opcao") e migalha. Da proxima vez o log aponta o ouvinte culpado.
- `PlaylistLibrary`: os arquivos `.ibt-tmp`/`.ibt-bak` do gravador de etiquetas nao acordam mais a releitura da pasta inteira (a cada gravacao, de 4 em 4 s).
- Regressao: `UiResponsivenessTests` (4 combinacoes + gravacao real em copias, batimento < 200 ms e cada chamada < 100 ms; diagnostico local da pasta real e do audio real so com `IBT_REAL_MUSIC_DIAG=1`), `SettingsChangeNeverBlocksTests` (4).
- Nao era o caso (verificado): sync-over-async (`SiteEngine.Dispose` e `TelegramEngine.Dispose` bloqueiam ate 6 s so ao fechar, mas usam `ConfigureAwait(false)`: nao ha deadlock); `Dispatcher.Invoke` de thread de fundo segurando lock: nenhum; `LoudnessCache.Get` faz `FileInfo` (E/S) mas so roda na thread da varredura e uma vez na UI para a faixa que toca; com a chave desligada o gravador nem e chamado (`TagWriteDue` exige a chave) e a medicao roda de qualquer jeito, com Igualar volume ligado ou nao.

## 2. As sessoes e os "crashs estranhos"

Fontes: logs 27/10 a 30/10, Visualizador de Eventos (Aplicativo e Sistema, 4 dias; eventos que citam AlertaIBT/IBT Controller: **nenhum**; nenhum Application Error, .NET Runtime, Application Hang ou WER do app; os eventos 1000/1002 sao de `mornye.exe`, `Asana`, `Creative Cloud`, `IObit` e do servico "Codex"; 28/10 23:26 o shell do Windows (explorer) caiu e o `userinit` o reiniciou, sem relacao com horario do app).

| # | Inicio | Versao | Fim | Como terminou |
|---|---|---|---|---|
| 1 | 27/10 20:25:59 | 8.0.1-pre.7 | 21:26:26 | atualizacao para pre.8 (esperado) |
| 2 | 27/10 21:26:28 | pre.8 | 28/10 01:53:45 | atualizacao para pre.9 (esperado) |
| 3 | 28/10 01:53:49 | pre.9 | 02:01:22 | PC desligado (evento 1074 as 02:01:22; "Mouse destravado" no mesmo segundo) |
| 4 | 28/10 12:03:02 (boot 12:02) | pre.9 | 22:06:04 | "Saindo a pedido do usuario" |
| 5 | 29/10 00:56:07 | pre.9 | 00:56:20 | atualizacao para 8.0.2-pre.1 (esperado) |
| 6 | 29/10 00:56:22 | 8.0.2-pre.1 | 02:43:00 | PC desligado (1074) |
| 7 | 29/10 12:58:55 (boot 12:58) | pre.1 | 15:29:28 | saida pelo usuario |
| 8 | 29/10 15:29:32 | pre.1 | 15:59:46 | atualizacao para 8.0.3-pre.1 (esperado) |
| 9 | 29/10 15:59:49 | 8.0.3-pre.1 | 16:11:19 | saida pelo usuario |
| 10 | 29/10 16:41:06 | 8.0.3-pre.1 | 16:41:20 | atualizacao para 3.2 (esperado) |
| 11 | 29/10 16:41:21 | 3.2 | 18:20:36 | atualizacao para 3.3 (esperado) |
| **12** | 29/10 18:20:37 | 8.0.3-pre.3 | **~20:58:04** | **ANORMAL**: ultimo registro 20:58:04 (8 erros `Win32Exception 1816 "Nao ha cota suficiente para processar este comando"` em `HwndTarget.UpdateWindowSettings`, a janela nao conseguia mais atualizar) e proxima abertura as 20:58:30 sem linha de saida, sem evento 1074 e sem evento de erro do Windows: encerrado pelo dono (Gerenciador de Tarefas) ou caiu |
| 13 | 29/10 20:58:30 | 3.3 | 21:23:08 | saida pelo usuario |
| 14 | 29/10 22:09:29 | 3.3 | 22:39:45 | atualizacao para 3.4 (esperado) |
| 15 | 29/10 22:39:48 | 3.4 | 23:50:21 | atualizacao para 8.0.4-pre.1 (esperado) |
| 16 | 29/10 23:50:24 | 8.0.4-pre.1 | 23:52:58 | PC desligado (1074) |
| 17 | 30/10 12:39:21 (boot 12:38) | 8.0.4-pre.1 | em andamento | travamento da tela ~14:36 (o processo continuou e voltou) |

Resumo: das 17 sessoes, 15 terminaram de forma limpa/esperada (8 por atualizacao, 4 saidas do usuario, 3 desligamentos do PC), **1 anormal (a de 29/10 20:58)** e 1 viva. O "Mouse destravado" no fim de uma sessao sem "Saindo..." e o desligamento do Windows (1074 no mesmo segundo), nao queda.

Achados do log:
- **29/10 20:58: erro 1816 em `HwndTarget.UpdateWindowSettings`** (cota do sistema de janelas/composicao do WPF, DWM). E o unico episodio em 4 dias que se parece com "o app ficou estranho": a janela principal parou de se atualizar, o app foi encerrado 28 s depois. Causa nao provada. Hipoteses: GPU/DWM sobrecarregado ou reiniciado, ou fila de mensagens da janela cheia por uma enxurrada de atualizacoes. O dispatcher ja tratava a excecao (`Handled = true`), mas 8 linhas por segundo e sem contexto. Agora o erro igual e escrito uma vez a cada 10 s (com contagem dos suprimidos), com os contadores GDI/USER/handles/threads/memoria do processo e as ultimas operacoes.
- A linha "Travamento do mouse refeito" (12 vezes entre 13:15 e 14:36 hoje): significa que ALGUM programa/Windows soltou o `ClipCursor` e o app o refez no verificador de 1 s; e inofensivo por si, mas indica um programa que mexe no cursor. A linha agora diz qual programa estava em primeiro plano.
- 27/10 21:26: `ObjectDisposedException` em `TelegramEngine.StopAsync` ao reiniciar para atualizar (corrida de `Dispose` da `CancellationTokenSource`, benigna, uma vez).
- 28/10 01:09 e 29/10 00:57: "Aplicar agora sem saida de audio disponivel" (PC sem saida de som naquele momento).

### Registro robusto de falhas (o que ja existia e o que foi acrescentado)
Ja existia: `DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException` e `AppDomain.UnhandledException` (este com `Log.CloseAndFlush`). Faltava e foi acrescentado (App.xaml.cs):
- marcador de sessao `session.txt` (`SessionMarker`): escrito ao abrir; no fim normal grava `ended=` e o motivo (saida normal, Windows encerrando a sessao + motivo, reiniciando para atualizar, falha ao iniciar); a proxima abertura loga **"A execucao anterior (versao X, iniciada em ...) terminou sem encerramento limpo: queda do app, encerrado pelo Gerenciador de Tarefas, queda de energia ou travamento"** quando nao ha a linha `ended=`; reinicio de atualizacao nao conta como anormal;
- linhas "Execucao iniciada (versao, processo, .NET)", "IBT Controller encerrando (motivo, codigo)" e "IBT Controller encerrado" (antes so havia "Saindo a pedido do usuario"), `OnSessionEnding` registra desligamento/logoff do Windows;
- todos os tres tratadores agora escrevem as ultimas ~24 operacoes (migalhas) e os contadores do processo; erros iguais repetidos sao agrupados.

### Vigia de travamento da interface (`UiStallWatchdog`)
Timer em thread de fundo a cada 250 ms pede ao dispatcher (prioridade Input, o que a pessoa sente) uma resposta minima. Sem resposta por mais de 2 s: **WARNING "Interface parada ha 2,3 s [GDI n, USER n, handles n, threads n, memoria n MB, GC a/b/c]. Ultimas operacoes: 14:36:01.123 (-2.4 s) configuracao: Player.WriteLoudnessTags | ..."**, repetido a cada 10 s enquanto durar ("Interface continua parada ha X s"), e quando volta **"Interface voltou a responder; ficou parada por X s"**. Se o proprio timer parou por mais de 2 s (PC dormiu) recomeca sem acusar. Para ao fechar. Custo: um post de delegate a cada 250 ms. Testavel com relogio falso (11 testes) e com um dispatcher real bloqueado 2,8 s (`SessionMarkerTests`).
Pilha da thread de UI: NAO ha forma segura em .NET de ler a pilha gerenciada de outra thread sem depurador; por isso o log traz as migalhas e os contadores. Migalhas gravadas (so NOMES, nunca valores): mudanca de configuracao (`Secao.Chave`, no maximo 6, nunca o valor), comandos do player (Play, Pause, Stop, Next, Previous, Seek), passos da varredura/gravador (medindo/gravando etiqueta com o NOME do arquivo), NDI (camada ligar/desligar/alternar, Take, previa, barras), copia automatica de backup, atalho global, tecla de midia, rota da API (so o caminho, sem a query).

### Pontos de risco revistos (arquivo:linha) e o que foi feito
- `MusicPlayer.cs` OnSettingsChanged / `AudioController.cs:247-285`, `:306-330` (Reconcile segura `_gate` com COM): **corrigido** (acima).
- `WindowsAudioSystem.cs` Dispose (deadlock com callback de dispositivo): **corrigido**. `WindowsAudioSystem._gate` ainda e segurado durante todas as chamadas COM (GetSessions etc.); nada na UI espera por ele agora, mas uma chamada COM que nunca volta seguraria o `Pass` (e o `ApplyNow`).
- `D3dVideoRenderer.cs:204` `Present()` com VSync: uma perda de dispositivo (reset de driver, monitor trocado) matava a thread de renderizacao deixando a imagem congelada no telao ate ligar/desligar a camada. **Agora recria o dispositivo ate 3 vezes em 1 minuto**, com aviso no log. Teardown: `NdiTelaoHost.Dispose` fecha o renderizador antes da janela e os timers antes de ambos (ok); o `Join` de 3 s roda na UI.
- `WindowsAppOutputPolicy.cs` (IAudioPolicyConfigFactory por indice de vtable, slots 25/26): nao ha defesa contra mudanca de layout da interface num Windows novo (a maquina de desenvolvimento e Windows 11 build 26300): um IID aceito com vtable diferente seria uma violacao de acesso (nao capturavel no .NET). Nao alterado (nao ha prova); registrado como risco. Os filtros de `catch` cobrem COM/DllNotFound/EntryPointNotFound.
- `LowLevelKeyboardHook.cs`: o callback ja captura toda excecao e sempre chama o proximo gancho; roda na thread de UI, entao uma UI parada acima de ~300 ms faz o Windows pular a chamada (nao ha reinstalacao automatica caso o Windows remova o gancho). Os comandos de tecla de midia ja sao despachados fora do gancho. Nao alterado.
- `MouseLockService` laco "refeito": nao e bug; **diagnostico novo** (programa em primeiro plano na linha do log) para achar quem solta o cursor.
- `App.xaml.cs:112` e `:167`: `StartAsync/StopAsync(...).GetAwaiter().GetResult()` na UI, so ao abrir e ao fechar, o ultimo com limite de 3 s; sem deadlock encontrado.
- `ClipCursor` (`WinCursor.cs`) e COM em thread errada: `MMDeviceEnumerator` e usado de threads do pool e da UI (agil), as notificacoes sao tratadas sem `SynchronizationContext`; nada a corrigir.

## Desconhecidos que restam
1. Se o travamento de 14:36 foi mesmo o servico de audio do Windows (programa de audio, dispositivo lento). Provei que ESSE caminho travava a UI e o corrigi, mas nao consegui medir um Windows Audio lento no PC de desenvolvimento (chamadas de 1 a 12 ms). O vigia dira o ouvinte e a hora da proxima vez.
2. A causa do erro 1816 (29/10 20:58) e da sessao 12 abruptamente encerrada.
3. Quem solta o cursor (12 vezes hoje).
4. Se o "Gerenciador de Tarefas" encerrou a sessao 12 (nao ha prova de queda; nenhum evento do Windows).

## O que o dono deve me mandar da proxima vez
O arquivo `%AppData%\AlertaIBT\logs\alertaibt-AAAAMMDD.log` do dia e o horario aproximado (por exemplo "14:36 de 30/10"). Procure por "Interface parada", "Configuracao: o ouvinte", "terminou sem encerramento limpo" e "Erro na interface". Nao precisa mandar `settings.json`.

## Verificacao
`dotnet build desktop/AlertaIBT.slnx --no-incremental`: 0 avisos, 0 erros. Core.Tests 3784 (0 falhas, 5 ignorados). App.Tests 1056: verdes na reexecucao, exceto o flake conhecido de mouse real (`RealMouseComboBoxTests`, que passa sozinho) e `CrossfadeGainProbeTests` com o pool real (passa sozinho). Ajuste de teste: `SomLayoutTests` chama `Player.Refresh()` depois de adicionar saidas ao fake (o fake nao dispara o evento que o Windows dispara).
