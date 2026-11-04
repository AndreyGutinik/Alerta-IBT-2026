# Interruptores que voltam como estavam

Regra do app: **todo interruptor que liga ou desliga uma função guarda a última posição** e a **aplica de verdade** no próximo início (fechar o app, reiniciar o PC, atualização que reinicia). O valor de fábrica só vale **na primeira vez** (quando não existe valor salvo). Arquivos de configuração antigos continuam valendo: uma chave que não existe no `settings.json` assume o padrão de fábrica e **nenhuma chave existente muda**.

A gravação segue a regra das outras configurações: some 0,6 s depois da mudança (e o app grava de novo ao sair).

Quando o interruptor depende de algo que não está lá (sem token do Holyrics, sem o NDI Tools, sem a senha da cópia), ele **volta desligado ou sem efeito, mostra o motivo e não repete a tentativa** até o próximo início. O app nunca trava nem mostra janela preta por isso.

| Interruptor | Onde | Padrão da 1ª instalação | Chave no `settings.json` | Aplicado no início por |
| --- | --- | --- | --- | --- |
| Alertas ligados | Configurações | ligado | `system.alertsEnabled` | `AlertsSwitch` |
| Correção automática | Início | ligado | `correction.enabled` | `StatusHub` / corretor |
| Só offline (em vez do Gemini) | Início | Gemini | `correction.mode` | corretor |
| Player ligado | Som | ligado | `player.enabled` | `MusicPlayer.Start` |
| Despertadores ligados | Som | ligado | `player.alarmsEnabled` | `AlarmScheduler.Start` |
| Cada despertador (liga/desliga) | Som | ligado | `player.alarms[].enabled` | `AlarmScheduler` |
| Ajustar o som ao iniciar | Som | ligado | `audio.enabled` | `AudioController.Start` |
| Travar a saída padrão | Som | ligado | `audio.lockOutput` | `AudioController` |
| Definir a saída do Holyrics | Som | ligado | `audio.holyricsRoute` | `AudioController` |
| Mudo: volume geral, Holyrics, sons do Windows | Som | desligado (com som) | `audio.masterMuted`, `audio.apps[].muted`, `audio.systemSoundsMuted` | `AudioController` |
| Mudo do player | Som | desligado | `player.startMuted` | `MusicPlayer` |
| Embaralhar | Som | ligado | `player.shuffle` | `MusicPlayer` |
| Repetir (todas / uma / não) | Som | todas | `player.repeat` | `MusicPlayer` |
| Teclas de mídia | Som | ligado | `player.mediaKeys` | `MouseLockController` / `MediaKeyRouter` |
| Igualar volume | Som | ligado | `player.normalizeEnabled` | `LoudnessService` / `MusicPlayer` |
| Gravar etiquetas nos arquivos | Som | desligado | `player.writeLoudnessTags` | `LoudnessService` |
| Câmera no telão | NDI, MA2, API, atalho | desligado | `ndi.layerOn` | `NdiService.RestoreSavedState` (a janela só aparece quando a câmera no ar tem imagem; sem câmera ou sem o NDI Tools, a chave volta ligada e nada aparece) |
| Barras de teste | NDI | desligado | `ndi.testPattern` | `NdiService.RestoreSavedState` (as barras cobrem o LED assim que o app abre, se ficaram ligadas) |
| Esconder da live | NDI | desligado | `ndi.hideFromCapture` | `NdiTelaoHost` |
| Atalhos F1-F4 / Ctrl+Espaço | NDI | ligado | `ndi.shortcutsEnabled` | `NdiHotkeyController.Apply` |
| Monitor travado no LED | NDI | ligado | `ndi.monitorLocked` | `NdiTelaoHost` |
| Controle pela MA2 | Controle | desligado | `control.enabled` | `ControlService.Start` (abre a porta Art-Net; porta ocupada = chip "porta em uso" e nova tentativa a cada 3 s, como sempre) |
| Universo mostrado = Art-Net + 1 | Controle | ligado | `control.universeOffset` | `ControlService` |
| Liga/desliga de cada linha do mapa | Controle | ligado | `control.rows[].enabled` | `ControlService` |
| Setas do painel rápido controlam o Holyrics | Controle | ligado | `holyrics.arrowsEnabled` | `QuickPanelViewModel` |
| Parar a música quando o cronômetro do Holyrics chegar a N s (e o N, de 1 a 10, padrão 2) | Controle (cartão Holyrics) | ligado, 2 s | `holyrics.countdownStopEnabled`, `holyrics.countdownStopAt` | `CountdownWatcher` (lê as configurações a cada leitura; só pergunta ao Holyrics com a conexão configurada) |
| Modo apresentação (controle do pastor) | Controle, Ctrl+Alt+P, bandeja, API, MA2 | desligado | `holyrics.presentationMode` | `PresentationModeController.RestoreSavedState` (instala o gancho do teclado; sem token ou com o gancho recusado fica desligado e mostra o motivo; o desejo salvo continua para o próximo início) |
| Travar o mouse | Configurações, Ctrl+Alt+M, bandeja | desligado | `mouseLock.enabled` | `MouseLockController.Start` |
| Ctrl três vezes | Configurações | ligado | `mouseLock.tripleCtrl` | `MouseLockController` |
| Limpar o histórico toda segunda | Configurações | ligado | `system.clearHistoryWeekly` | `HistoryClearScheduler` |
| Atualizar automaticamente ao abrir | Configurações | ligado | `system.autoUpdate` | `UpdateService` |
| Janelas do app aparecem em prints | Configurações | ligado | `system.appWindowsInScreenshots` | `AppWindowPolicy` |
| Site ligado | Configurações | desligado | `site.enabled` | `SiteConnector.Apply` (sem endereço, e-mail ou senha continua como antes: fica parado e mostra o que falta) |
| Templo menor (modo) | Configurações > Sistema, API `/templo/on` e `/templo/off` | desligado | `temple.enabled` | `AppSettings.Temple`, lido por cada serviço ao iniciar (`TempleProfile.Effective`): os serviços das funções desligadas nem começam, a tela já abre sem as abas escondidas e o telão já usa o tamanho do templo menor |
| Funções desligadas no templo menor (engrenagem) | Configurações > Sistema | nenhuma marcada | `temple.disabled` (nomes: `alerts`, `player`, `audio`, `ndi`, `ma2`, `holyrics`, `gemini`) | idem; a lista fica guardada mesmo com o modo desligado, e só vale com o modo ligado |
| Resolução do telão: normal e templo menor | Configurações > Sistema | 2560 x 768 e 1920 x 1080 | `temple.normalWidth/normalHeight`, `temple.templeWidth/templeHeight` | `LedTracker` (o monitor que conta como LED), a barra, a janela do NDI |
| Cópia automática do backup | Configurações | desligada | `backup.autoEnabled` (a senha fica só protegida pelo Windows, em `backup.passwordProtected`) | `AutoBackupService.Start` (se a senha não puder ser lida neste Windows, a chave volta **desligada** com o motivo e a pasta é mantida) |

Itens da bandeja (Pausar, Travar mouse, Modo apresentação) não têm valor próprio: mostram o estado do mesmo interruptor acima.

**A única exceção: Pausar barra (pregação).** Ela **não é guardada**: o app sempre abre com a barra **rodando**, seja qual for o estado em que foi fechado (e um `settings.json` antigo com `barPaused` é ignorado; a chave some no próximo salvamento). Motivo: uma pausa esquecida na pregação bloquearia os avisos do domingo seguinte; depois de ligar o PC os avisos precisam sempre funcionar. Pausar e retomar continuam funcionando normalmente durante a sessão (Início, painel rápido, bandeja, API do Holyrics, Telegram).

Não são interruptores e **não** entram nesta regra: o que está tocando agora, a posição da música, a câmera da Prévia e a do No ar, as listas (playlists), os volumes e os fades (esses já eram guardados).

O teste `PersistedSwitchesTests` (Core) tem esta mesma lista: um interruptor novo é uma linha nova lá, nesta tabela e, se ele depende de um serviço, um teste de "aplicado no início".

**Templo menor não reescreve os seus interruptores.** As chaves de cada função (Alertas ligados, Player ligado, Controle pela MA2, modo apresentação, camada NDI e as outras da tabela) continuam com o valor que o dono escolheu; o modo só faz os serviços lerem essas chaves como desligadas (`TempleProfile.Effective`: alertas efetivos = a chave do dono E a função não desligada pelo modo). Sair do modo, ou tirar a função da lista, devolve tudo como estava, pelo caminho normal de início de cada serviço (o modo apresentação e a camada NDI voltam sozinhos). O Templo menor e a lista fazem parte do backup (itens "Templo menor" e "Funções desligadas no templo menor"); um backup antigo, sem esses itens, não mexe neles. Teste: `TempleModeTests` (Core) e `PersistedSwitchesTests`.
