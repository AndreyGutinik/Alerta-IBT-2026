# Lote 40 - pausa nao e mais guardada + o app para a musica no cronometro do Holyrics

Branch `a2026`, csproj 8.0.5. Sem push/release. App instalado e `%AppData%\AlertaIBT` nao tocados. Token provisorio lido sem eco (so em scripts fora do repo); nunca gravado nem logado.

## TASK 1 - Pausar barra sempre abre rodando
- `system.barPaused` (SystemSettings + SettingsJson), `AlertService.RestorePause` e o restaurar/gravar do `AlertRuntime` foram REMOVIDOS. Uma chave antiga `barPaused: true` e ignorada e some no proximo salvamento (o arquivo reescrito nao a tem; nao e reescrita como `false`, e removida). Pausar/retomar na sessao (Inicio, painel, bandeja, API do Holyrics, Telegram) segue igual.
- Testes: `BarPauseRestoreTests` agora 4 testes "sempre abre rodando" (primeira instalacao; arquivo antigo pausado via grafo real + API `/overlay/status` rodando + chave some; pausar na sessao nao grava e o proximo inicio roda; alertas desligados + arquivo antigo). `PersistedSwitchesTests`: linha removida e teste "unica excecao". Removido o teste `A_restored_pause...`.
- Docs: INTERRUPTORES-SALVOS (linha saiu; paragrafo da excecao e do motivo), README, CLAUDE_CONTEXT, CHECKLIST (a linha "a pausa nao e lembrada" ficou mais clara). Comentarios de codigo corrigidos.

## TASK 2 - Cronometro do Holyrics

### Medido no Holyrics real (127.0.0.1:8091, 2.30.0, token provisorio ainda valido; CheckPermissions ok para GetCPInfo, StartCountdownCP, StopCountdownCP, ShowCountdown, StartTimerCP, StopTimerCP, GetCurrentPresentation, CloseCurrentPresentation)
**Cronometro do painel de comunicacao** (`StartCountdownCP {seconds:10}`; sondado a 4 Hz, latencia 10 ms):
- `countdown_time` = **segundos INTEIROS**, so conta para baixo enquanto `countdown_show=true`. Num de 10 s a primeira leitura (40 ms apos iniciar) ja e **9**; troca de numero a cada 1,0 s (9,8,...,1,0); o **0 dura 2 s** e depois **continua negativo** (-1, -2, -3 ...) com `countdown_show` ainda **true** ate `StopCountdownCP` (mesmo com `stop_at_zero:true` o numero seguiu negativo). Escondido/parado: `countdown_show=false`, `countdown_time=0`. Ou seja o valor e `trunc(restante)`: o "2" aparece quando faltam entre 3,0 e 2,0 s. A documentacao diz "tempo mostrado (em segundos)": assumi que o numero da API e o da tela (dispara em `<= N`). **Nao confirmei na tela** (a janela do LED neste PC nao mostrou nada na captura; ver "nao verificado").
**Apresentacao de contagem regressiva** (`ShowCountdown {time:"00:10"}`, e quase certamente o "10 s" do dono: item `countdown`, texto "para iniciar o culto"):
- **NAO aparece no GetCPInfo** (`countdown_show` ficou false o tempo todo). Aparece em `GetCurrentPresentation`: `data.type:"countdown"`, `data.current_time` texto **"M:SS"** ("0:10","0:09",...,"0:02","0:01","0:00"); cada valor dura 1,0 s (mostra 0:10 no inicio, nao 9); "0:00" dura ~0,8 s e a apresentacao some sozinha (`data:null`). Esse e o tipo que o app tambem le.
**Cronometro progressivo** (`StartTimerCP`): `stopwatch_show=true`, `stopwatch_time` sobe em segundos inteiros; `countdown_show` continua false. Ignorado.
Holyrics deixado como encontrado: `countdown_show=false`, sem cronometro progressivo, `GetCurrentPresentation` null (confirmado ao fim de cada teste; usei StopCountdownCP, StopTimerCP e CloseCurrentPresentation).
Nao ha acao para iniciar o cronometro "pronto" do dono (item de lista) sem criar outro: `ShowCountdown`/`StartCountdownCP` criam novos. Para o teste na igreja o dono inicia a mao.

### Que tipos de temporizador a API expoe (docs + medicao)
1. Contagem do painel (`StartCountdownCP`/`StopCountdownCP`, item `countdown_cp` de lista): lida por `GetCPInfo` (**vigiado**).
2. Cronometro progressivo do painel (`StartTimerCP`/`StopTimerCP`): `GetCPInfo` (ignorado, a pedido).
3. Apresentacao de contagem regressiva (`ShowCountdown`, item `countdown`): `GetCurrentPresentation` (**vigiado**).
4. Multipla escolha com `countdown` (1-120 s) (acao de apresentacao de multipla escolha), `close_after_seconds` do alerta, `duration` de itens, slides automaticos (`GetAutomaticPresentationPlayerInfo.time_ms`), tempo restante do VLC (`display_vlc_player_remaining_time`): nao ha leitura de "segundos restantes" confiavel; nao vigiados.

### Desenho
- `Core/Holyrics/CountdownWatcher.cs`: `CountdownTrigger` (puro: dispara 1x por rodada em `<= N`; rearma ao sumir, ao tempo subir ou ao ficar acima de N; comeca em <= N dispara 1x; negativos do painel nao redisparam), `CountdownText` (parse "M:SS"/"H:MM:SS", formato `00:07`), `CountdownWatcher` (loop em segundo plano com `Task.Delay(.., TimeProvider)`, ritmo 1 s sem cronometro / 250 ms com cronometro / recuo 2,4,8,15 s sem resposta e volta ao normal ao responder; nao faz nada sem conexao ou com o interruptor desligado; um piscar do Holyrics NAO rearma; nunca usa a UI; chip via evento).
- Acao: **`IPlayerControl.Pause()`** (fade-out do dono + pausa) - e o caminho do `/player/pause` que o gatilho antigo do Holyrics chamava; Stop() nao e usado. Player nao tocando = nada (log INFO "a musica nao estava tocando"). Log INFO unico por disparo: `Cronômetro do Holyrics chegou a 2 s: música parada`; migalha `holyrics: cronometro chegou a N s`.
- `HolyricsClient.ReadCountdownAsync` (GetCPInfo + GetCurrentPresentation; so GetCPInfo e obrigatorio; 401 em GetCurrentPresentation e dito uma vez e nao e repetido ate a conexao mudar; falhas usam a mesma coalescencia do log; nao mexe no chip de conexao; mensagem de 401 do GetCPInfo: "token sem permissão para GetCPInfo"). `RequiredActions` agora inclui `GetCPInfo,GetCurrentPresentation` (Testar confere).
- Config `holyrics.countdownStopEnabled` (padrao ligado, tambem em arquivo antigo) e `holyrics.countdownStopAt` (1..10, padrao 2, fixado ao ler). Registrado no AppHost; iniciado em `AppShell.Start` (Guarded) e solto no Dispose.
- UI (cartao Holyrics, estilo do sistema; skill impeccable, craft-floor lido): linha "Parar a música em" + icone ampulheta + etiqueta verde ao vivo `00:07` (so quando ha cronometro) + lista "2 s" (1..10; desabilitada com o interruptor desligado) + interruptor; dica longa no bloco; linha de aviso amarela sob o cartao so quando o token nao pode ler. Bloco na coluna da esquerda (balanceia melhor que o modo apresentacao esticado). PNGs olhados em `.superpowers/sdd/2026-10-19-alerta-ibt-v8-pc/batch-40/png/countdown-{900,1100,1600}-{sem-cronometro,ao-vivo,desligado,sem-permissao}.png`: sem corte, uma coluna a 900. O rotulo longo ("Parar a música no cronômetro") truncava a 1100; virou "Parar a música em".

### Testes novos
Core: `CountdownWatcherTests` (67: gatilho 10..0 = 1x em 2 e nao em 3, nao duas vezes, rearma (some/sobe/recomeco), comeca em 1, limiar de 1..10; unidades "0:10","1:02","1:00:05", lixo; ritmo e recuo; relogio falso no loop; player tocando/nao tocando/recusa/excecao; interruptor desligado e sem conexao nao perguntam; piscar nao redispara; chip; cliente contra o servidor falso (painel, negativo, escondido, progressivo, apresentacao, tipo que nao e contagem, dois cronometros = menor, 10 s roteirizado pelo cliente real, 401 do painel, 401 da apresentacao perguntado 1x, Holyrics desligado sem encher o log e sem tocar no chip, token nunca no log); configuracao ida e volta, migracao, limite 1..10). `FakeHolyricsServer` ganhou `Live` (respostas calculadas). `PersistedSwitchesTests` ganhou a linha do novo interruptor. App: `CountdownViewModelTests` (7), layout `PresentationLayoutTests` (+3 larguras), `ControlLayoutTests` ajustado ao novo bloco.

### Build/testes
(ver mensagem final; numeros conferidos no fim)

## Nao verificado / riscos
1. **Tela do Holyrics vs numero da API** no cronometro do painel: a API da `trunc(restante)`; se a tela mostrar `ceil` o "00:02" da tela aparece quando a API ja diz 1 (a musica pararia ~1 s mais cedo ou mais tarde que o esperado). Na apresentacao o numero da API e o texto da tela (`current_time`), sem ambiguidade. Conferir na igreja (checklist).
2. O fade-out do dono (ate 5 s no exemplo do README) dura mais que os 2 s: a musica ainda esta descendo quando o cronometro zera; ajuste o N (ate 10 s) para a musica ja estar em silencio no zero.
3. Item `countdown_cp` de lista e cronometro "pronto" do dono nao foram iniciados por mim (so `ShowCountdown`/`StartCountdownCP` novos); espera-se o mesmo comportamento.
4. `GetCurrentPresentation` pode ser pesado com apresentacoes grandes a 1 Hz (respostas locais de ~10 ms no teste, sem apresentacao de musica); nao medido com um culto inteiro aberto.
5. O token provisorio nao foi usado pelo app (so por scripts); o caminho real `HolyricsClient` -> Holyrics real para estas duas acoes foi coberto pelo servidor falso com as respostas reais copiadas.
