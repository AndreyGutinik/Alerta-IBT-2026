# Interruptores que voltam como estavam

Regra do app: **todo interruptor que liga ou desliga uma função guarda a última posição** e a **aplica de verdade** no próximo início (fechar o app, reiniciar o PC, atualização que reinicia). O valor de fábrica só vale **na primeira vez** (quando não existe valor salvo). Arquivos de configuração antigos continuam valendo: uma chave que não existe no `settings.json` assume o padrão de fábrica e **nenhuma chave existente muda**.

A gravação segue a regra das outras configurações: some 0,6 s depois da mudança (e o app grava de novo ao sair).

Quando o interruptor depende de algo que não está lá (sem token do Holyrics, sem o NDI Tools, sem a senha da cópia), ele **volta desligado ou sem efeito, mostra o motivo e não repete a tentativa** até o próximo início. O app nunca trava nem mostra janela preta por isso.

| Interruptor | Onde | Padrão da 1ª instalação | Chave no `settings.json` | Aplicado no início por |
| --- | --- | --- | --- | --- |
| Pausar barra (pregação) | Início, painel rápido, bandeja, API do Holyrics, Telegram | desligado (barra rodando) | `system.barPaused` | `AlertRuntime.Start` (volta pausada em silêncio: o grupo do Telegram não é avisado de novo) |
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
| Modo apresentação (controle do pastor) | Controle, Ctrl+Alt+P, bandeja, API, MA2 | desligado | `holyrics.presentationMode` | `PresentationModeController.RestoreSavedState` (instala o gancho do teclado; sem token ou com o gancho recusado fica desligado e mostra o motivo; o desejo salvo continua para o próximo início) |
| Travar o mouse | Configurações, Ctrl+Alt+M, bandeja | desligado | `mouseLock.enabled` | `MouseLockController.Start` |
| Ctrl três vezes | Configurações | ligado | `mouseLock.tripleCtrl` | `MouseLockController` |
| Limpar o histórico toda segunda | Configurações | ligado | `system.clearHistoryWeekly` | `HistoryClearScheduler` |
| Atualizar automaticamente ao abrir | Configurações | ligado | `system.autoUpdate` | `UpdateService` |
| Janelas do app aparecem em prints | Configurações | ligado | `system.appWindowsInScreenshots` | `AppWindowPolicy` |
| Site ligado | Configurações | desligado | `site.enabled` | `SiteConnector.Apply` (sem endereço, e-mail ou senha continua como antes: fica parado e mostra o que falta) |
| Cópia automática do backup | Configurações | desligada | `backup.autoEnabled` (a senha fica só protegida pelo Windows, em `backup.passwordProtected`) | `AutoBackupService.Start` (se a senha não puder ser lida neste Windows, a chave volta **desligada** com o motivo e a pasta é mantida) |

Itens da bandeja (Pausar, Travar mouse, Modo apresentação) não têm valor próprio: mostram o estado do mesmo interruptor acima.

Não são interruptores e **não** entram nesta regra: o que está tocando agora, a posição da música, a câmera da Prévia e a do No ar, as listas (playlists), os volumes e os fades (esses já eram guardados).

O teste `PersistedSwitchesTests` (Core) tem esta mesma lista: um interruptor novo é uma linha nova lá, nesta tabela e, se ele depende de um serviço, um teste de "aplicado no início".
