# Checklist do teste na igreja (IBT Controller 8.0.2)

Roteiro para testar o app novo no **PC do telão** (o PC do Holyrics, com o LED 2560x768). Marque cada item. Se algo falhar, anote o que viu e copie o log em **Configurações > Diagnóstico > Copiar logs**.

Tudo aqui foi testado no PC de desenvolvimento só com o monitor principal (modo `--test-led`) e com programas de mentira no lugar do Telegram, do Gemini e do LED. Os itens abaixo são o que **só dá para ver na igreja**. O Gemini, em especial, **nunca foi chamado de verdade** (ver a parte 3).

O item (7) da lista da spec (site, membro e admin) não entra aqui: o site ainda não existe (veja [PROXIMOS-PASSOS.md](PROXIMOS-PASSOS.md)).

Tempo estimado: 1h30 com calma. Levar: o instalador, o celular com o Telegram, o celular para a chave do Gemini, uma segunda pessoa para mandar mensagens no grupo.

---

## 0. Antes de sair de casa

- [ ] Baixar o `AlertaIBT-win-Setup.exe` na [página de Releases](https://github.com/AndreyGutinik/Alerta-IBT-2026/releases) (ou usar o de `artifacts/Releases` gerado por `pwsh desktop/build/pack.ps1`) e levar num pendrive.
- [ ] Ter à mão: o token do bot do Telegram e o ID do grupo (parte 3), ou criar na hora.
- [ ] Conta Google para criar a chave grátis do Gemini (parte 3).

## 1. Preparar o PC da igreja

- [ ] **Fechar o Alerta-IBT antigo (Electron, versão 7).** Os dois usam a porta **8787** e o **mesmo bot** do Telegram: abertos juntos, o Telegram dá "Conflito" e a porta fica "ocupada". Fechar pela bandeja (Sair) e conferir no Gerenciador de Tarefas que `Alerta-IBT.exe` não está rodando. **Não desinstale ainda**: ele fica instalado até o teste terminar (parte 14).
- [ ] Abrir o Gerenciador de Tarefas e deixar à vista (para olhar memória e CPU depois).
- [ ] LED ligado e a tela 2560x768 aparecendo no Windows (Configurações > Sistema > Tela).
- [ ] Holyrics, OBS (com a "Captura de tela" do LED) e o NDI Screen Capture abertos, como num culto. O App Flash também aberto.

## 2. Instalar e primeira abertura

- [ ] Rodar o `Setup.exe`. Se o Windows mostrar "O Windows protegeu o computador" (instalador sem assinatura), clicar em **Mais informações > Executar assim mesmo**.
- [ ] O app abre sozinho, com a **janela** (primeira vez), escura, com a fonte Marble e os acentos certos (Ã, Ç, É), a barra lateral com Início, Som e Configurações e, embaixo, cinco linhas pequenas de status.
- [ ] Clicar no **X** da janela: ela some e o app **continua na bandeja** (o ícone perto do relógio).
- [ ] Abrir o app de novo pelo Menu Iniciar: **não** nasce um segundo ícone nem um segundo processo; a janela volta para a frente.
- [ ] Se o app antigo estava no início do Windows (ou aberto), apareceu **um aviso único** sobre ele, com o botão para tirá-lo do início do Windows. Responder **Sim**. (O aviso não volta mais.)
- [ ] Abrir `%AppData%\AlertaIBT` no Explorador: existem `settings.json` e `logs\`.
- [ ] Conferir o registro (Win+R, `regedit`): `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run` tem o valor `AlertaIBT` igual a `"...\AlertaIBT.exe" --tray`.
- [ ] Menu da bandeja (botão direito): **Abrir, Pausar, Sair**. Clique simples no ícone abre o painel rápido; duplo clique abre a janela. **Sair** pede confirmação (responder Não por enquanto).
- [ ] Memória do `AlertaIBT.exe` no Gerenciador de Tarefas **abaixo de 350 MB** e CPU **perto de 0%** com o app parado (a meta definida pelo dono; no PC de desenvolvimento a versão de teste usou cerca de 180 MB logo após abrir, então anote o número real). Abrir para a bandeja em menos de 2 s depois de ligar o PC (conferir no item de reiniciar, parte 12).

## 3. Configurar: Telegram e Gemini

### Bot do Telegram

- [ ] No Telegram, falar com o **@BotFather**: `/newbot`, escolher nome e usuário (terminando em `bot`) e **copiar o token** (algo como `123456789:AAH...`). Não mostrar o token a ninguém. (Dá para reaproveitar o bot que o app antigo já usa, mas só **um** programa lê o bot por vez: o antigo precisa estar fechado.)
- [ ] Colocar o bot no grupo dos voluntários. Para ele ler mensagens que começam com `#`, no BotFather usar `/setprivacy`, escolher o bot e **Disable**, ou torná-lo **administrador** do grupo.
- [ ] Descobrir o **ID do chat**: mandar uma mensagem no grupo e abrir no navegador `https://api.telegram.org/bot<TOKEN>/getUpdates` (trocando `<TOKEN>`); procurar `"chat":{"id":-100...`. O ID de grupo é um número **negativo**, copiar com o sinal de menos. Fazer isso **antes** de colar o token no app (dois leitores ao mesmo tempo dão conflito) e fechar a aba depois.
- [ ] Em **Configurações > Telegram**: colar o token (vira bolinhas e o botão "Trocar") e o ID do chat. Esperar o selo **"Salvo às hh:mm"**.
- [ ] A bolinha do **Telegram** na lateral fica **verde "Conectado"**. Passando o mouse, o balão explica o estado.
- [ ] Abrir `%AppData%\AlertaIBT\settings.json` no Bloco de Notas: o token e a chave aparecem só protegidos (campos `...Protected`), **nunca em texto puro**.
- [ ] Um token com espaço ou incompleto é recusado com a dica, sem salvar.
- [ ] Trocar para outro token e voltar: depois da troca o bot lê as mensagens do grupo novo desde o começo (o contador de leitura zera).

### Chave grátis do Gemini

- [ ] Entrar em **aistudio.google.com/apikey** com a conta Google e criar uma **chave de API** (plano grátis, sem cartão). Lembrete: no plano grátis o Google pode usar os textos enviados para melhorar os produtos dele (aqui só vão carro, cor, placa e motivo).
- [ ] Em **Início > Correção** (seção fechada, abre pela seta): colar a chave (vira bolinhas); o ícone de **Gemini** fica aceso (verde). Esperar "Salvo".
- [ ] **Testar um aviso com a chave.** No painel rápido ou em Início, digitar `prata corolla bra2e19 vidro abrto` e esperar cerca de 0,7 s: a prévia deve aparecer **verde** ("Vai aparecer assim: COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO") e a bolinha do **Gemini** ficar **verde "Online"**.
- [ ] **Bolinha do Gemini sem digitar nada.** Abrir o app com a chave salva: em cerca de 10 s a bolinha do **Gemini** (lateral e cabeçalho da seção Correção, em Início) sai de cinza e fica **verde "Online"** (passa antes por amarelo "Verificando"); passar o mouse mostra "Gemini conectado há N min". Sem chave: cinza **"Sem chave"** ("Chave do Gemini não configurada"). Tirar a rede: na próxima verificação (30 s, 1 min, 2 min... a cada falha, até 30 min) a bolinha fica azul **"Offline"** com o motivo ("Offline: tempo esgotado"). Chave errada: azul com o motivo do Google (ex.: "Offline: HTTP 403 PERMISSION_DENIED"). Trocar a chave ou o modelo em Correção refaz a verificação em ~3 s. A verificação é **uma** pergunta mínima ("ok") no máximo a cada 30 min, e nenhuma enquanto avisos reais estiverem sendo corrigidos; no Diagnóstico aparece `Gemini: conectado (modelo ..., N ms)`.
- [ ] **Atenção: se a bolinha do Gemini ficar azul "Offline" depois de digitar um aviso válido (e a prévia mostrar "(offline)"), o Gemini NÃO está funcionando.** O app continua funcionando offline, mas agora diz o motivo no log: abrir **Configurações > Diagnóstico** e procurar as linhas que começam com `Gemini`. Cada falha deixa **uma linha** com o motivo e o modelo (nunca a chave nem o texto digitado), e o mesmo motivo só aparece **uma vez por minuto**. Exemplos exatos do que pode aparecer:
  - `Gemini falhou: HTTP 403 PERMISSION_DENIED: API key not valid. Please pass a valid API key. (modelo gemini-3.5-flash-lite)` : chave errada ou com restrição.
  - `Gemini falhou: HTTP 429 RESOURCE_EXHAUSTED: <mensagem do Google> (modelo gemini-3.5-flash-lite)` : cota do plano grátis acabou, esperar. (A parte `RESOURCE_EXHAUSTED: ...` é o corpo da resposta do Google; se o Google não mandar corpo, a linha fica só `HTTP 429`.)
  - `Gemini falhou: HTTP 400 INVALID_ARGUMENT: ... (modelo gemini-3.5-flash-lite)` ou `HTTP 404` : pedido ou nome do modelo recusado; anotar a linha inteira para eu corrigir.
  - `Gemini falhou: sem chave de API (modelo gemini-3.5-flash-lite)` : a chave não foi salva.
  - `Gemini falhou: tempo esgotado (modelo gemini-3.5-flash-lite)` ou `Gemini falhou: erro de rede (HttpRequestException) (modelo gemini-3.5-flash-lite)` : internet. (Pode vir com o erro de dentro, por exemplo `HttpRequestException/SocketException`.)
  - `Gemini falhou: resposta com JSON invalido (modelo gemini-3.5-flash-lite)` ou `resposta em formato inesperado` : o Google respondeu algo que o app não entende; anotar.
  - `Gemini falhou: trava de seguranca recusou a resposta (palavra nova) (modelo gemini-3.5-flash-lite)` : o Gemini respondeu, mas inventou placa, cor ou palavra, então o app usou o corretor offline (isto é proteção, não defeito).
  - `Gemini: recusou o texto (blockReason SAFETY) (modelo gemini-3.5-flash-lite)` : o Gemini recusou um texto impróprio.
  - `Gemini offline: os avisos usam so o corretor offline` e `Gemini online` : as mudanças de estado da bolinha.

  Conferir a chave e o **modelo** em Configurações > Modelo do Gemini (vazio = padrão `gemini-3.5-flash-lite`) e anotar a linha do log para eu corrigir.
- [ ] Mandar um texto ofensivo com a chave configurada: **nada** passa na barra, o grupo recebe `❌ ... conteúdo impróprio.` e o histórico mostra a linha ❌.
- [ ] Tirar o Wi-Fi/cabo de rede e mandar um aviso: a prévia sai azul "(offline)", o aviso passa corrigido pelo dicionário offline e o Gemini fica azul "Offline". Religar a rede: volta a verde sozinho no próximo aviso.
- [ ] Colar uma chave com espaço ou curta: recusada com a dica.
- [ ] Em Início > Correção, desligar a correção automática: a bolinha do Gemini fica cinza "Desligada". Ligar de novo e tocar no ícone **Só offline**: azul "Offline". Voltar para Gemini.
- [ ] Regras fixas: digitar `abrto => aberto`, uma linha inválida (`sem seta`): o aviso diz "1 regra · 1 linha ignorada". Reiniciar o app e conferir que as escolhas ficaram.

## 4. LED e barra (o item mais importante)

- [ ] No log (Diagnóstico) aparece **"LED: ... 2560x768"** logo depois de abrir, com o LED ligado. A bolinha **LED** fica verde "Ligado". Com **dois LEDs**, o mesmo é escolhido depois de reiniciar o app.
- [ ] Em **Configurações > Barra no LED**, clicar em **Mostrar barra 3 s no LED**: a barra aparece no **TOPO do LED** (e não em outro monitor), desce, o texto atravessa **uma vez** e a barra sobe.
- [ ] A rolagem está **lisa** (sem engasgo a 60 Hz). Olhar com atenção; se engasgar, anotar (o plano B é trocar o renderizador).
- [ ] Letras em **Marble Bold**, com espaçamento confortável, **acentos certos** (Ã, Ç, É); fundo preto, texto creme.
- [ ] A barra fica **por cima da janela do Holyrics** e por cima da camada do App Flash (o flash cobrir a barra por instantes é aceitável).
- [ ] **Clicar e digitar no Holyrics enquanto a barra passa**: nada atrapalha (a barra não rouba o foco, e os cliques atravessam).
- [ ] Mudar **posição** para Rodapé, **tamanho** e **velocidade** com os controles: a prévia reage na hora, os rótulos acompanham ("Média · 56 px", "90 px/s · passa em N s") e o teste de 3 s usa o que acabou de ser escolhido, mesmo se o controle acabou de ser mexido. Voltar para Topo.
- [ ] Em Avançado: letras num campo de número são recusadas com a dica; 9999 é limitado ao máximo ("Ajustado para ...").
- [ ] Deixar o app ligado por **horas**: a barra continua aparecendo quando chamada, a memória não cresce e a janela segue no topo.

### Escala e telas

- [ ] Mudar a escala do Windows do monitor principal para **125%** e **150%** (Configurações > Sistema > Tela): a janela e o painel rápido continuam legíveis e a barra continua do tamanho certo no LED. Voltar para a escala normal.
- [ ] Se o LED e o monitor principal tiverem escalas diferentes: a barra fica no LED com o tamanho certo e o painel rápido abre no canto da tela onde o mouse está (nunca no LED).

## 5. A barra NÃO pode aparecer na transmissão

- [ ] **OBS, "Captura de tela"** do monitor do LED, com a barra passando (usar o botão de teste de 3 s várias vezes): a barra **não aparece** na prévia nem na gravação do OBS.
- [ ] **NDI Screen Capture** do LED: a barra **não aparece** no receptor.
- [ ] **Print do Windows** (Win+Shift+S ou Print Screen) da tela do LED: a barra **não aparece**.
- [ ] A bolinha da **Barra** está verde e o log tem "Barra excluída da captura de tela" e **nenhuma** linha "NÃO está excluída". Se a bolinha da Barra ficar **vermelha**, a exclusão falhou: anotar o erro do log.
- [ ] **O painel rápido, a janela principal e as caixas do app (Sair, aviso do app antigo) também ficam fora da captura**: com o OBS/NDI olhando o LED, abrir o painel (Ctrl+Alt+Espaço) e a janela e conferir que **não aparecem** na prévia. O log não deve ter nenhuma linha "A janela ... NAO esta excluida da captura de tela". Exceção conhecida: a caixa do sistema de **Exportar logs** não dá para esconder; ela abre sobre a janela principal, que nunca fica no LED.
- [ ] **Nenhuma janela do app abre no LED**: com o mouse em cima do LED, apertar Ctrl+Alt+Espaço: o painel abre no canto da tela **principal** (não no LED). Clicar em "Abrir janela": a janela principal abre centralizada na tela principal. Arrastar a janela para o LED, fechar no X e abrir de novo: ela volta para fora do LED.

## 6. Telegram: avisos de verdade

Pedir a outra pessoa para mandar do grupo (e conferir as respostas do bot).

- [ ] `#corolla prata abc1d23 vidro aberto` passa na barra como `COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO` e o grupo recebe **✅ Mensagem exibida no telão.** com o texto. O `#` não aparece no telão.
- [ ] `#gol branco farol aceso` (sem placa): **não** passa, e o grupo recebe `❌ ... Faltou placa ...` com o exemplo de como mandar. Na janela, o histórico mostra ❌ "FALTOU PLACA".
- [ ] A **mesma placa** de novo: `⚠️ Esse carro já está na fila.` (ou `já passou há N min`).
- [ ] Mensagem **sem `#`**, mensagem de **outro grupo** e mensagem de **outro bot**: ignoradas.
- [ ] Mensagem de **3 linhas com emoji e 400 caracteres** (de um voluntário): é recusada com `❌ ... grande demais (máximo 200 letras)`. Do PC (painel), a mesma mensagem passa numa linha só, legível, rolando uma vez.
- [ ] O aviso expira se esperar mais de 30 min (opcional: deixar o LED desligado por 30 min com um aviso na fila; o grupo recebe `⌛ O aviso expirou sem passar no telão.`).
- [ ] Com o PC desligado por um tempo e o bot recebendo mensagens: ao ligar, mensagens com mais de 30 min **não** passam na barra (só as recentes).

## 7. Painel rápido (um clique)

- [ ] Apertar **Ctrl+Alt+Espaço** com o **Holyrics** (ou o OBS) na frente: o painel aparece no canto inferior direito **da tela onde está o mouse** (se o mouse estiver no LED, na tela principal, nunca no LED), já com o cursor no campo (o rascunho antigo vem selecionado). **Esc** limpa e **Esc de novo** fecha; o Holyrics recebe o teclado de volta, sem perder o lugar (e os atalhos do OBS continuam funcionando).
- [ ] Clicar em qualquer outro lugar (também em outro monitor ou na barra de tarefas): o painel fecha. Clicar no ícone da bandeja com o painel aberto: fecha e **não reabre**. Com ele fechado, o clique no ícone **abre** (este é o caminho que sempre funciona).
- [ ] Se **outro programa já usa o atalho**, o painel mostra a linha amarela, a dica do ícone da bandeja diz "Atalho ... em uso por outro programa" e o app abre normalmente. Trocar o atalho em Configurações > Sistema (clicar no campo e apertar a combinação, por exemplo Ctrl+Alt+F8; `Shift+A` sozinho, `Ctrl+V`, `Ctrl+C`, `Alt+F4`, `Alt+Espaço`, `Ctrl+F4` e outros atalhos do Windows são recusados com uma dica e o atalho anterior continua). Fechar o outro programa e clicar no ícone: o aviso some.
- [ ] Visual igual ao mockup: três ícones com bolinha e a pílula na primeira linha; cartão "No telão agora" (vazio e alto quando parado); campo com (i); prévia; **uma linha de botões**, só o Enviar com texto. Passar o mouse em cada bolinha e no (i): balões com o estado e os atalhos (Enter, Ctrl+Enter, Esc, atalho do painel).
- [ ] Digitar `prata corolla bra2e19 vidro abrto`: depois de uns 0,7 s aparece o texto corrigido (verde com Gemini, azul "(offline)" sem). `pais do pedro na recepcao` aparece azul "(aviso livre)". A mesma placa dentro de 10 min mostra o aviso amarelo "já passou há N min" e **ainda envia**.
- [ ] **Enter** envia ("Na fila: 1º"), **Ctrl+Enter** envia como urgente, o botão Urgente também. O painel fica aberto depois de enviar.
- [ ] Com o LED ligado, o aviso aparece em "No telão agora" com a origem, a **linha de progresso anda a passada inteira** e "Fila: N · próxima: ..." acompanha.
- [ ] **Abrir janela** fecha o painel e abre a janela.
- [ ] Abrir e fechar o painel muitas vezes: sem problema de foco e sem a memória crescer.

## 8. Janela (dois cliques)

- [ ] **Bolinhas da lateral**: Telegram verde com token e chat certos; Site cinza "Não configurado" (o site ainda não existe); LED verde ligado e vermelho desligado; Barra verde (amarela pausada); Gemini verde "Online" / azul "Offline" / cinza "Desligada". Passar o mouse mostra o balão com o detalhe.
- [ ] **Início**: enviar `prata corolla bra2e19 vidro abrto` (Enter e o botão). Enquanto passa, o cartão "Mensagem atual" mostra o texto com a origem, a linha de progresso anda e aparece **"Corrigido. Original: ..."** quando as palavras foram alteradas.
- [ ] **Próxima** e **Encerrar atual**: os dois terminam o aviso na barra na hora (o aviso conta como exibido).
- [ ] Colocar três avisos na fila e usar **↑ ↓ ✕**; a lixeira limpa a fila sem perguntar.
- [ ] **Histórico**: aviso exibido tem ✓ e ↻ (↻ coloca de novo na fila); o ❌ de um aviso sem placa mostra "FALTOU PLACA".
- [ ] **Salvar sozinho**: mexer em uma opção e ver "Salvando…" e depois "✓ Salvo às hh:mm:ss" cerca de 0,6 s depois. Fechar o app com **Sair** logo depois de mexer num controle e abrir de novo: a mudança está lá.
- [ ] **Diagnóstico** (recolhido no começo): abrir, ler o log (mais novo primeiro, avisos amarelos, erros rosados); **Copiar logs** e colar no Bloco de Notas; **Exportar** salva um `.txt`; **Limpar** esvazia (linhas novas continuam chegando). A janela aberta por uma hora, escondida e mostrada várias vezes, não aumenta a memória.
- [ ] Os **ícones** (Lucide) estão bonitos e reconhecíveis na janela e no painel: sirene, janela de app, varinha, wifi cortado, alertas, engrenagem, cadeado, lixeira. A varinha parece fina em 13 px e a engrenagem e o "rolo de texto" são os mais detalhados: se algum ícone estiver estranho, anotar qual.

## 9. LED desligado e ligado

- [ ] Com um aviso **na fila**, **desligar o LED**: a bolinha LED fica vermelha, a pílula diz "LED desligado" e o grupo recebe `⏳ Telão desligado. O aviso está na fila e passa quando ele ligar.` **uma vez por mensagem** (não repete se o LED piscar).
- [ ] **Ligar o LED**: os avisos passam **em ordem**.
- [ ] Desligar o LED **no meio de uma rolagem**: a barra some na hora, nada trava, e o aviso **recomeça do início** quando o LED volta.
- [ ] Mudar a resolução do LED durante a rolagem: a barra some na hora; quando volta ao normal, o próximo teste mostra a barra de novo.
- [ ] Sem o LED, "Mostrar barra 3 s" avisa o motivo em vez de não fazer nada.

## 10. Pregação: Holyrics pausa a barra

- [ ] Em um terminal: `curl http://127.0.0.1:8787/overlay/status` responde `{"success":true,"overlayEnabled":true}`.
- [ ] Com avisos na fila e um passando, chamar `http://127.0.0.1:8787/overlay/off`: a barra para na hora, a fila esvazia, o grupo recebe `⚠️ O pastor iniciou a pregação...` **uma vez**, a pílula diz **Pausada** (barra amarela), o botão Pausar fica amarelo "Retomar".
- [ ] Chamar `/overlay/off` de novo: **não** manda uma segunda mensagem.
- [ ] Mandar um aviso no Telegram durante a pausa: `⏸️ Pregação em andamento...`; digitar no painel: "Pregação em andamento: o aviso não foi enviado.", e o histórico mostra a linha vermelha "PREGAÇÃO".
- [ ] Chamar `/overlay/on`: volta ao normal, **sem** mensagem no grupo, fila vazia.
- [ ] **No Holyrics, de verdade**: o slide da pregação dispara `/overlay/off` e o slide seguinte dispara `/overlay/on` (o gatilho do Holyrics não muda; as rotas são as mesmas do app antigo).
- [ ] O botão **Pausar** do painel e da janela e o item do menu da bandeja mostram **o mesmo estado** nos três lugares. **Um clique = uma troca** (olhar o texto do menu da bandeja depois de cada clique).
- [ ] Fechar o app com avisos na fila (**Sair**) e abrir de novo: os avisos pendentes **voltam e passam**. A pausa **não** é lembrada.

## 11. Convivência com os outros programas

- [ ] **App Flash** aberto: as duas janelas no topo da mesma tela sem briga (a barra pode ser coberta por instantes pelo flash).
- [ ] **Holyrics** e **OBS** não perdem foco nem atalhos quando o painel abre e fecha.
- [ ] Com o app antigo (Electron) aberto de propósito (só para ver): o novo mostra "Porta ocupada" e o Telegram "Conflito". Fechar o antigo: a API assume a porta sozinha em até 30 s e o Telegram se recupera.
- [ ] **Tirar o cabo de rede**: Telegram vai para amarelo e depois vermelho, o PC continua mostrando o que já estava na fila e a correção cai no offline. **Recolocar**: tudo volta sozinho.

## 12. Reiniciar o PC

- [ ] **Reiniciar o Windows**: o app volta **sozinho, na bandeja, sem janela** (o valor `AlertaIBT` com `--tray` no registro). Aparece em menos de 2 s depois do logon.
- [ ] Depois do reinício a bolinha do Telegram volta a verde sozinha e `curl http://127.0.0.1:8787/overlay/status` responde.
- [ ] Fechar o app pelo **Sair** da bandeja (confirmar) e verificar no Gerenciador de Tarefas que o processo acabou.

## 13. Atualização automática (precisa de duas versões: a instalada N e uma publicada N+1)

O app procura versão nova 5 s depois de abrir e a cada 30 minutos; **vê também os pré-lançamentos** (8.0.1-pre.2 conta como nova para 8.0.1-pre.1). Achando, baixa, e **instala e reinicia sozinho quando está ocioso** (nada na fila, nada na barra, sem pregação pausada, nenhum envio em andamento).

- [ ] **Versão certa**: instalar a versão N (o `Setup.exe`), abrir e ver em **Configurações → Sistema** a linha "Versão N", o interruptor **Atualizar automaticamente ao abrir** ligado e a linha do resultado ("Atualizado (N) às HH:MM").
- [ ] **Atualiza ao abrir**: publicar a versão N+1 nas Releases (pode ser pré-lançamento), fechar o app (Sair) e abrir de novo. Em até uns 10 s aparece no alto da janela "Baixando a atualização N+1…" (e na dica do ícone da bandeja), depois "Instalando e reiniciando…"; o app fecha e abre sozinho **na mesma forma de antes** (na bandeja se tinha aberto com `--tray`) já na versão N+1 (conferir em Configurações → Sistema).
- [ ] **Verificar agora**: com o app aberto, publicar a N+2 e apertar **Verificar agora**: a linha do resultado mostra o andamento e o app atualiza. Sem versão nova a linha diz "Atualizado (N) às HH:MM". Sem internet diz "Sem conexão com o GitHub" e **não aparece erro**.
- [ ] **Ocupado não reinicia**: com um aviso na fila ou passando na barra (ou com a pregação **pausada**), publicar uma versão nova e apertar **Verificar agora**: ela baixa, a linha diz "Nova versão X baixada, instalando quando ficar ocioso" e o app **não reinicia**. Quando a fila esvaziar (ou retomar a pregação) ele instala e reinicia sozinho em até 15 s. A fila salva volta depois do reinício.
- [ ] **Interruptor desligado**: desligar "Atualizar automaticamente ao abrir", publicar versão nova, fechar e abrir: nada acontece sozinho. **Verificar agora** continua funcionando.
- [ ] **Sem laço**: se a atualização falhar, o app tenta no máximo 2 vezes por versão em 24 h (no log: "sem novas tentativas até amanhã") e segue funcionando na versão antiga.
- [ ] Versões instaladas **antes** desta (8.0.0) não atualizam sozinhas para ela: instalar a primeira versão com atualização automática à mão (veja `docs/PROXIMOS-PASSOS.md`).

## 14. Site (avisos pelo celular)

Antes: o app do PC com **Configurações → Site** preenchido e ligado (`docs/SITE-COMO-PUBLICAR.md`, seção 5), a bolinha **Site** **verde**, o LED ligado e o app **fora** da pausa. Um celular com a conta Google do teste (e outro, ou a conta do dono, para o administrador).

- [ ] **Site verde**: bolinha Site da barra lateral verde ("Conectado"). No celular, aba **Status**: pílulas **PC online**, **LED** e **Barra** corretas e o mini-telão igual à barra.
- [ ] **Enviar do celular** `corolla prata abc1d23 vidro aberto`. Em **Seus avisos** o selo muda sozinho: *enviado* → *Nº na fila* → *passando* → **hora** (✅). No LED passa `COROLLA PRATA | PLACA: ABC-1D23 | VIDRO ABERTO`. No grupo do Telegram aparece `✅ Mensagem exibida no telão.` com o texto (também vale para aviso do site).
- [ ] **Prévia não manda**: o texto que passa é o corrigido pelo PC, mesmo que a prévia do celular tenha dito outra coisa.
- [ ] **Faltando campo** (`corolla prata`): o botão Enviar fica bloqueado na prévia. Se chegar assim mesmo (modo avião/atrasado), o selo vira `faltou placa` e nada passa no LED.
- [ ] **Repetido**: mandar a mesma placa de novo logo depois: `repetido`. Como **administrador** o mesmo aviso passa (com aviso de repetido na prévia).
- [ ] **Urgente** (segurar o botão por 1 s, vibra): passa na frente dos normais; a posição dos outros muda sozinha em "Seus avisos".
- [ ] **Fila**: com um aviso passando, mandar mais dois do celular: selos `1º na fila` e `2º na fila` e o painel do PC mostra os dois com origem **site**.
- [ ] **Cancelar**: tocar no **X** de um aviso na fila: vira `cancelado` e some da fila do PC. Aviso que **já está passando** não tem X.
- [ ] **Admin**: no celular do administrador aparece a aba **Equipe**; um aviso livre (sem placa) passa como aviso livre.
- [ ] **Bloquear**: na aba **Equipe**, remover (bloquear) a conta de teste. O celular dela mostra "Seu acesso foi removido" e um aviso novo dessa conta nunca passa. Desbloquear volta ao normal.
- [ ] **Limite**: mandar 11 avisos válidos em uma hora com a mesma conta de membro (placas diferentes, 20 s entre eles): o 11º vira `limite de 10 por hora`. O administrador não tem limite.
- [ ] **Pregação**: com avisos na fila, pausar (botão Pausar ou `/overlay/off`): os avisos viram `cancelado: pregação`, a pílula do site mostra **Barra pausada** e o Enviar fica bloqueado; um aviso forçado vira `pregação`. Retomar libera de novo.
- [ ] **LED desligado**: desligar o LED (ou tirar o cabo do monitor): o celular mostra a pílula **Telão desligado** e os avisos ficam `na fila`; ao voltar o LED eles passam.
- [ ] **PC offline**: fechar o app do PC (**Sair**). Em até ~90 s o celular mostra **PC offline**. Mandar um aviso nesse tempo: fica `enviado`. Abrir o app de novo: a bolinha Site volta a verde, o aviso (se tiver menos de 30 min) passa; se a espera passou de 30 min ele vira `expirou` e **não** passa.
- [ ] **Sem internet no PC**: tirar o cabo de rede: Site amarelo e depois vermelho ("Sem conexão"); o painel e o Telegram continuam funcionando com o que já estava na fila. Recolocar: volta a verde sozinho e o que foi mandado nesse meio tempo é tratado.
- [ ] **Trocar a senha da conta do PC** (console do Firebase) e conferir que a bolinha vai a vermelha "Erro de login"; colocar a senha nova em Configurações → Site: volta a verde sem reiniciar o app.

## 15. Limpar histórico (botão e segunda-feira 00:00)

O botão **Limpar histórico** (Início, cabeçalho do Histórico) apaga **sem perguntar**: o histórico do PC, os pedidos já encerrados do site (e a lista "recentes" do Status) e as mensagens do bot no Telegram com **menos de 48 h**. Não mexe na fila, no que está passando, nem na memória de "essa placa passou há N min".

- [ ] **Botão**: com alguns avisos no histórico (e o site verde), clicar em **Limpar histórico**. Aparece por uns 7 s `Histórico limpo: N no PC, N no site, N no Telegram`; o histórico do PC fica vazio, no celular a aba **Seus avisos** perde os avisos já encerrados e **Status** mostra "recentes" vazio. Um aviso que está na fila ou passando continua lá.
- [ ] **Telegram**: o bot precisa ser **administrador do grupo com o direito "Excluir mensagens"** (Grupo > Administradores > o bot > Excluir mensagens). Com isso somem as respostas do bot (`✅ Mensagem exibida...`) e as mensagens `#...` que o app viu. A **mensagem fixada** (instruções do grupo) e qualquer mensagem que o app não viu **nunca** são apagadas. Sem o direito, ou com mensagens de mais de 48 h, o botão avisa `N mensagens do Telegram ficaram (mais de 48 h ou sem permissão)`.
- [ ] **Site offline**: tirar a rede do PC, clicar em **Limpar histórico**: a linha diz `o site será limpo quando voltar`; recolocar a rede e, quando a bolinha Site ficar verde, os pedidos encerrados do site somem (o PC não é limpo de novo).
- [ ] **Segunda-feira 00:00**: em **Configurações > Sistema** a chave "Limpar histórico toda segunda-feira, 00:00" vem **ligada**. Com o app aberto na virada de domingo para segunda, o histórico é limpo às 00:00.
- [ ] **PC desligado na segunda (teste em casa, sem esperar)**: feche o app, copie `%AppData%\AlertaIBT` para uma pasta temporária, edite o `settings.json` dessa cópia e ponha em `system` o valor `"lastHistoryClearAt": "2026-01-05T12:00:00Z"` (uma data de uma semana ou mais atrás), abra o app com `ALERTAIBT_DATA_DIR` apontando para a cópia: o histórico é limpo **na hora**, e `lastHistoryClearAt` passa a ser o momento atual. Sem esse campo (primeira vez depois de instalar) nada é apagado: o app só passa a contar a partir dali.
- [ ] **Chave desligada**: desligar a chave e repetir o teste acima: nada é apagado.

## 16. Bloqueador de mouse (3 monitores: principal, LED e retorno)

O travamento prende o cursor no **monitor principal do Windows**, para ele não parar no LED nem no retorno. Fica em **Configurações > Sistema**: a chave "Travar o mouse na tela principal", o atalho (padrão **Ctrl+Alt+M**) e a chave "Ctrl 3 vezes traz o mouse ao centro". Também há o item **Travar o mouse na tela principal** no menu do ícone da bandeja. Vem **desligado**; o que você escolher é lembrado ao reabrir.

- [ ] **Travar**: ligar a chave (ou o atalho, ou o item da bandeja) e empurrar o mouse para o LED e para o retorno: ele **para na borda da tela principal e não passa**. Se o mouse estava no LED na hora de ligar, ele vem para a tela principal. O item da bandeja fica marcado e a dica do ícone diz **mouse travado**.
- [ ] **Atalho**: **Ctrl+Alt+M** liga e desliga; a chave de Configurações e o item da bandeja acompanham. Trocar o atalho no campo e usar o novo. Se outro programa já usa a combinação, aparece o aviso embaixo do campo (o atalho do painel rápido e o do mouse não podem ser iguais).
- [ ] **Lembrar**: com o travamento ligado, **Sair** e abrir de novo: continua ligado. Desligado e reaberto: continua desligado.
- [ ] **Ctrl 3 vezes**: com o travamento **desligado**, levar o mouse ao LED e apertar **Ctrl três vezes seguidas, rápido** (menos de 1 s): ele vai para o **centro da tela principal**. Repetir com o travamento ligado. **Ctrl+C**, **Ctrl+V**, segurar o Ctrl e Ctrl com Alt **não contam**. Com o Holyrics ou o OBS na frente, nada muda: nenhuma janela abre e o foco não sai deles. Com a chave "Ctrl 3 vezes" desligada, nada acontece.
- [ ] **Desligar e religar um monitor** (cabo do LED ou do retorno) com o travamento ligado: em até 1 s o mouse continua preso à tela principal. No Diagnóstico, uma linha "Travamento do mouse refeito" é normal.
- [ ] **Trocar o monitor principal** no Windows (Configurações > Sistema > Tela > escolher o monitor > "Tornar este meu vídeo principal"): em até 1 s o mouse fica preso ao **novo** principal, e Ctrl 3 vezes leva ao centro dele. Voltar o principal ao que era.
- [ ] **UAC, bloqueio e área de trabalho remota**: com o travamento ligado, abrir um aviso do UAC (instalador), bloquear e desbloquear o Windows (Win+L) e conectar/desconectar por área de trabalho remota: depois de voltar, o mouse continua preso (ou volta a prender em até 1 s). **Ctrl+Alt+Del** solta o mouse por conta própria do Windows e ele volta a prender sozinho.
- [ ] **Matar o processo**: com o travamento ligado, Gerenciador de Tarefas > Finalizar tarefa em "IBT Controller". O Windows deve soltar o mouse sozinho (ele passa a andar pelos 3 monitores). Se não soltar, anotar; Ctrl+Alt+Del solta, e reabrir o app e desligar a chave também.
- [ ] **Atualização**: com o travamento ligado, quando o app reiniciar para se atualizar o mouse fica livre no reinício e volta preso ao abrir.
- [ ] **Privacidade**: no Diagnóstico o único registro do Ctrl 3 vezes é `Ctrl x3: mouse centralizado`; nenhuma tecla aparece em lugar nenhum.

## 17. Som (volumes e saídas)

A aba **Som** (barra lateral, depois de Configurações) cuida do volume e de **por onde sai cada som**. Nada aqui foi ouvido com o WING de verdade (o PC de desenvolvimento não tem a mesa), então esta parte é para conferir **de ouvido** na igreja. Antes: a mesa **WING** ligada por USB e o Holyrics aberto.

- [ ] **Qualidade (uma vez)**: Windows > Configurações > Sistema > Som > Mais configurações de som > aba Reprodução > cada saída do WING (**Out 1-2** e **Out 3-4**) > Propriedades > Avançado: escolher **24 bits, 48000 Hz** (o formato da mesa). Assim o Windows não reamostra a música.
- [ ] **Volumes ao abrir**: fechar o app e abrir (ou reiniciar o PC). Uns 6 s depois, no misturador de volume do Windows (clique direito no alto-falante > Abrir misturador de volume): **Volume geral 80%**, **Holyrics (javaw) 100%** assim que ele tocar, **Sons do Windows 0%**. Na aba Som, a linha embaixo de "Aplicar agora" diz o que foi aplicado.
- [ ] **A cada vez que o programa abre**: fechar o Holyrics e abrir de novo: o volume dele volta ao da lista (não só na primeira vez). Mudar o volume do Holyrics à mão no misturador e **não** mexer em nada: o app não corrige de novo até o Holyrics fechar e abrir.
- [ ] **Volumes na hora (lote 26)**: arrastar o volume do **Windows**, do **Holyrics**, do **Player** e dos **Sons do sistema** (e clicar no ícone de mudo de cada um) e **ouvir/ver na hora** (o misturador do Windows se mexe junto): vale **no mesmo instante**, sem esperar segundos e sem clicar em Aplicar agora. O arquivo só é salvo uns 600 ms depois (o "Salvo" do canto), mas o som não espera. O volume do player **só existe na linha Player** deste cartão (o botão do player não tem mais volume).
- [ ] **Suavizar mudança de volume (menu no topo do cartão Volumes ao iniciar, só o ícone)**: **seco** (padrão) = muda na hora. Com **0,5 s**, **1 s**, **2 s** ou **3 s** o volume **anda até o valor novo** nesse tempo (todos os volumes, também o mudo, que desce e depois muda). Mexer de novo no meio da subida/descida **troca o destino** sem pulo. Conferir com 2 s e 3 s mexendo nos quatro volumes e no mudo.
- [ ] **Aplicar agora**: mudar o volume geral à mão, clicar **Aplicar agora**: volta ao valor da aba. Com "Ajustar o som ao iniciar" desligado, o app não mexe em volume nenhum sozinho (só em Aplicar agora).
- [ ] **Mudo**: marcar "Mudo" em uma linha e **Aplicar agora**: o programa fica mudo no misturador. Desmarcar e aplicar de novo.
- [ ] **Saída padrão do Windows (travada no WING)**: deixar "Travar a saída padrão neste dispositivo" ligado e a saída em "Automático (WING)". Com o WING ligado, a **saída padrão do Windows vira o WING**. Trocar a saída padrão à mão para outro aparelho: em até 1 s o app **põe de volta** no WING (no máximo 3 vezes por minuto, para não brigar com o Windows). Desligar a chave: o app para de mexer.
- [ ] **WING desligado**: tirar o cabo USB. Na aba Som a linha diz "**WING ... desconectado, aguardando**" e o app **não troca nada**. Religar: a saída volta ao WING e o volume é posto de novo.
- [ ] **Trocar a saída em uso (lote 26)**: com a música tocando, mudar a lista **Player** (por exemplo do Out 3-4 para o Out 1-2, ou para outro aparelho): o som **passa para o aparelho novo na hora**, **da mesma posição** (sem recomeçar a música, sem parar e tocar de novo, sem reabrir o app), com uma descida e uma subida de ~0,15 s (sem estalo). Mudar várias vezes seguidas vai para a **última** escolha. Com a música **pausada**, trocar a saída e tocar: toca já no aparelho novo. Tirar o cabo do aparelho escolhido durante a música: ela passa para a saída padrão (o ponto da linha fica amarelo, "desconectado") e volta ao aparelho quando ele volta. A **saída padrão do Windows** (cadeado) e a do **Holyrics** também mudam na hora; se o Windows só obedecer na **próxima vez** que o Holyrics tocar, o ponto do Holyrics fica amarelo e a dica diz "vale na próxima vez que o Holyrics tocar" (fechar e abrir o Holyrics resolve).
- [ ] **Escolher a saída por nome**: a lista mostra as saídas ligadas e, se for o caso, a lembrada marcada **(desconectado)**. Escolher outra e voltar a **Automático** funciona; um aparelho escolhido é lembrado também em outra porta USB.
- [ ] **Holyrics na saída 1-2**: com "Definir a saída do Holyrics" ligado (saída em "Automático (WING + 1-2)"), abrir o Holyrics e tocar um som: no Windows > Som > Misturador de volume, o Holyrics aparece com **Dispositivo de saída = WING Out 1-2**, e o som sai **só** pelas saídas 1-2 da mesa. A linha da aba Som diz "Holyrics toca em ...". Desligar a chave: o app **nunca mais mexe** nessa saída (o que já está gravado no Windows fica como está).
- [ ] **Privacidade e log**: no Diagnóstico só aparecem nomes de aparelhos, nomes de programas e porcentagens (por exemplo "Som: Holyrics 100%").

## 18. Player e despertadores

O player toca a pasta de música da igreja (padrão: `C:\Users\gutni\Music\Mornye\Igreja`, trocável em **Som > Player > Lista de músicas**). Sai pelo **Out 3-4** do WING; o Holyrics continua no **Out 1-2** ao mesmo tempo (modo compartilhado do Windows).

- [ ] **Botões do player (lote 26)**: embaralhar, anterior, **tocar/pausar** (o disco verde grande), **parar** (um botão **redondo menor, só com o contorno** e um quadradinho dentro, ao lado do tocar; parar não pede confirmação), próxima e repetir ficam numa linha centralizada sob a linha do tempo. **Não há mais volume no player** (use a linha Player dos Volumes). **Instalações novas** começam com Fade-in **seco**, Fade-out 5 s, **sem mescla** e **sem suavizar volume**; quem já tinha valores salvos não muda.
- [ ] **Tocar**: **Player ligado**, botão tocar: a música sai **só pelo Out 3-4** (na aba, a linha diz "Saída do player: WING ... Out 3-4"). Anterior, tocar/pausar, parar e próxima funcionam; **embaralhar** liga e desliga; **repetir** passa por desligado, toda a lista e esta música. A barra fina e o tempo andam.
- [ ] **Saídas ao mesmo tempo**: tocar o player e um som do Holyrics: cada um na sua saída (1-2 e 3-4), sem estalo e sem um atrapalhar o outro. Abrir o misturador do Windows: o app aparece com o volume dele; o volume do player na aba Som mexe **só no player**.
- [ ] **WING desligado**: tirar o cabo durante a música: a linha diz "desconectado, tocando na saída padrão" (ou espera) e a música volta sozinha no 3-4 quando o WING volta (na troca de música).
- [ ] **Fades (menus Fade-in e Fade-out, no fim do player)**: o ícone com a subida é o **Fade-in** (vale ao tocar e ao despertador começar) e o com a descida é o **Fade-out** (vale ao pausar e parar, pelo botão, pela API do Holyrics e pelas teclas). Cada um é um menu com **seco** (0), 0,5 s, 1 s, 2 s ... 10 s. Com **5 s**: pausar desce o volume ao longo dos 5 s e depois silêncio; tocar sobe em 5 s. Testar **0,5 s**, **1 s**, **10 s** e **seco** (para e começa na hora, sem estalo). A descida e a subida seguem uma curva (quadrática), não uma reta: o volume muda de forma **perceptível ao longo de todo o tempo** e termina macio, sem corte no fim. O despertador usa o mesmo Fade-in. Os botões próxima e anterior continuam com a troca curta de sempre. "Tocar" depois de uma pausa começa **outra** música (a menos que você tenha arrastado a linha do tempo durante a pausa: aí continua a mesma música, de onde você pôs).
- [ ] **Mescla entre músicas (terceiro menu, o ícone com duas curvas que se cruzam)**: **sem mescla** = uma música segue a outra **sem pausa nenhuma** entre elas (ouvir a troca com músicas FLAC de verdade). Com **3 s** ou **5 s**: a próxima começa antes da atual acabar e as duas se misturam sem buraco e sem volume que cai no meio, como no Spotify (testar com um par de FLAC 24 bits/48 kHz e um de 44,1 kHz). **Lote 26**: a mescla agora se apoia no **último som de verdade** da música que sai (o último trecho acima de -50 dBFS, medido só no final do arquivo) e no **primeiro som** da que entra (pula o silêncio do começo), porque as músicas reais têm final quieto (de -50 a -96 dBFS nos últimos segundos) e a primeira ficava muda antes da segunda entrar: ouvir com **12 s**, **5 s** e **3 s** e conferir que **as duas se ouvem juntas no meio**, uma descendo e a outra subindo, sem buraco. Só acontece quando a música acaba sozinha: nos botões próxima e anterior não mistura. Conferir: o título, a capa e a linha do tempo mudam para a música nova **no começo** da mistura, sem piscar; pausar no meio da mistura baixa **as duas** juntas; arrastar a linha do tempo no meio da mistura cancela a mistura (a música em que você mexeu segue sozinha); no fim de uma lista que não repete não há mistura com o nada; uma música curta (menos que o dobro da mescla) mistura só na metade dela.
- [ ] **Linha do tempo**: clicar ou arrastar a linha do player vai para aquele ponto (o tempo à esquerda acompanha o dedo e a música só pula ao soltar, sem estalo). Setas esquerda e direita com a linha focada andam 5 s. Funciona também com a música pausada. A **capa do álbum** (embutida no FLAC/MP3/M4A ou `cover.jpg`/`folder.jpg`/`front.jpg` da pasta) aparece ao lado; sem capa, aparece o quadrado escuro com a nota musical.
- [ ] **Holyrics: contagem regressiva do culto**: no Holyrics, na contagem que termina e abre o culto, chamar `http://127.0.0.1:8787/player/pause` (GET ou POST, no mesmo gatilho do `/overlay/off`, se for o caso): a música **desce em 5 s e para**. Conferir também `/player/play`, `/player/stop`, `/player/next`, `/player/previous` e `/player/status` no navegador (a resposta é JSON; com o player desligado diz `player desligado`).
- [ ] **Teclas de mídia**: com "Teclas de mídia controlam o player" ligado, play/pausa, próxima, anterior e parar do teclado comandam o player. Com Spotify ou Chrome abertos eles também reagem (o app não engole a tecla). Desligar a chave: o player ignora.
- [ ] **Listas (pastas)**: **+** escolhe uma pasta e cria a lista com o nome dela; o lápis renomeia; a lixeira tira a lista (as músicas ficam); **Trocar a pasta** muda a pasta; **Atualizar** lê a pasta de novo. Copiar uma música nova para a pasta: aparece em até 2 s sem parar a música.
- [ ] **Despertador**: o de domingo vem às **18:00**, "não iniciar depois de" **18:25**, só domingo, e a linha "**Próximo despertador: domingo 18:00**" confere. Mudar a hora (tem que ser hh:mm completo), clicar nos dias (D S T Q Q S S), trocar a lista, ligar e desligar um despertador, **Adicionar despertador** e a lixeira.
- [ ] **Disparo**: pôr um despertador 2 minutos adiante: a música começa na hora, **embaralhada**, repetindo a lista, com a subida de volume escolhida em "Ao começar pelo despertador" (5 s, 3 s ou seco). O despertador **nunca para** a música que já está tocando. Fechar e abrir o app no mesmo dia: **não** dispara de novo.
- [ ] **Abrir o PC atrasado**: com o app fechado na hora, abrir às 18:10: a música começa **na hora que abre**; abrir às 18:30: **não** começa.
- [ ] **Player desligado**: a chave "Player ligado" desligada para tudo na hora, os despertadores não disparam e o `/player/...` responde `player desligado`. O painel rápido (um clique na bandeja) mostra a linha do player (anterior, tocar/pausar, próxima e o título) **só com o player ligado**.
- [ ] **Uso de recursos**: 2 horas de música seguidas: olhar CPU e memória do app no Gerenciador de Tarefas e se há estalos na troca de música.

## 19. Alertas desligados (templo menor)

Em **Configurações > Sistema > Alertas**, a chave "**Alertas ligados (barra no LED, Telegram, site e API do overlay)**" vem **ligada**. Desligada, o app só cuida do **som**: serve para o templo menor, que não usa o telão.

- [ ] **Desligar**: a barra no LED some (se havia um aviso passando, ele volta para a fila), o **Telegram** e o **site** param (as bolinhas ficam cinza "Desligado"), e a fila não anda. A aba **Início** mostra o aviso **"Alertas desligados"** com o botão **Ligar os alertas**. O painel rápido (um clique na bandeja) esconde a parte dos avisos e deixa só a linha "Alertas desligados" e a do player.
- [ ] **API do overlay**: `http://127.0.0.1:8787/overlay/on`, `/overlay/off` e `/overlay/status` respondem **200** com `{"success":false,"disabled":true,"message":"alertas desligados"}` e **não pausam nada**. As rotas `/player/...` continuam respondendo normalmente.
- [ ] **O resto continua**: player, aba Som, travamento do mouse, histórico e atualizações funcionam com os alertas desligados. O app abre desligado (a chave é lembrada).
- [ ] **Ligar de novo**: pela chave ou pelo botão **Ligar os alertas** da aba Início: a barra, o Telegram e o site voltam sozinhos (as bolinhas ficam verdes em instantes), os avisos que esperavam na fila passam, e as rotas `/overlay/...` voltam às respostas de sempre.

## 20. NDI no telão

A aba **NDI** manda uma câmera da rede (NDI) para o LED, **por baixo** da letra do Holyrics e da barra de avisos. Precisa do **NDI Tools** instalado no PC do telão (gratuito, https://ndi.video/tools/). A câmera no telão **sempre começa desligada** quando o app abre.

- [ ] **Barras de teste sobre o LED**: aba NDI, cartão **Ajustes**, ligar **Barras de teste** (a barra **Telão** do topo tem a chave **Câmera no telão** e os números de sinal, quadros, resolução e perdidos): as barras de cores cobrem o LED inteiro e o marcador branco anda suave (60 quadros por segundo, sem rasgo).
- [ ] **Letra do Holyrics por cima das barras**: com as barras ligadas, projetar uma letra no Holyrics: a letra aparece **por cima** das barras (a janela transparente do Holyrics fica acima da câmera). Se o Holyrics estiver com "sempre no topo", tudo bem.
- [ ] **Barra de avisos por cima**: mandar um aviso de teste: a barra no topo do LED passa **por cima** das barras de teste e da câmera, e continua sem aparecer no OBS/NDI da live.
- [ ] **Corte entre duas câmeras, sem preto nem piscada**: clicar na câmera 1 (vai para a **Prévia**, nunca ao ar), **Cortar**; clicar na câmera 2 (Prévia), **Cortar**: a troca é seca, sem quadro preto e sem piscar, e a câmera que estava no ar volta para a Prévia. Conferir a etiqueta **NO AR** (vermelha com a câmera no telão, verde só escolhida) e os marcadores Prévia / No ar nos botões.
- [ ] **Congelar 5 s**: com a câmera no ar e a câmera no telão ligada, tirar o cabo (ou parar o emissor NDI) por 6 s: a última imagem fica **parada por 5 s** e depois **some** (o Holyrics aparece normal, sem preto); ao voltar o sinal, a imagem volta sozinha. O chip do cartão mostra "congelado" e depois "sem sinal".
- [ ] **Posição vertical por câmera**: arrastar a barrinha sob a Prévia e sob o No ar (0 a 100%) e girar o mouse sobre a imagem (1%; com Shift, 5%): o retângulo verde (a faixa de 53% que o LED mostra de uma câmera 1920x1080) anda na miniatura e o telão acompanha na hora. Cada câmera guarda a sua posição; fechar e abrir o app mantém.
- [ ] **Atalhos**: com **Atalhos F1-F4 e Ctrl+Espaço** ligado, F1 a F4 mandam as câmeras 1 a 4 para a Prévia e **Ctrl+Espaço** corta; a aba mostra a mudança na hora. Se uma tecla já era de outro programa (F1-F4 podem bater com atalhos do Holyrics), aparece o aviso amarelo; desligar a chave devolve as teclas.
- [ ] **API do Holyrics**: `http://127.0.0.1:8787/ndi/on`, `/ndi/off`, `/ndi/take` (não escolhe câmera) e `/ndi/status` respondem 200; a chave "Câmera no telão" e a Prévia/No ar da aba acompanham.
- [ ] **Escolha do monitor**: o seletor vem **travado** no monitor 2560x768 (cadeado). Destravar deixa escolher outro monitor da lista; destravar sempre começa de novo no 2560x768. Travar volta ao LED. **Esconder da live (OBS/NDI)** vem desligado: ligado, o OBS não vê a câmera.
- [ ] **Sem o NDI Tools**: com o NDI Tools desinstalado (ou renomeando a pasta do runtime), o cartão **"NDI Tools não encontrado"** aparece no topo com o link e o botão **Verificar de novo**; o resto da aba fica visível mas apagado. Instalar o NDI Tools e clicar em **Verificar de novo**: o cartão some e as câmeras aparecem na lista.
- [ ] **Câmeras e IPs fixos**: o lápis de cada botão muda o nome e a fonte (lista das fontes achadas ou o IP digitado, `192.168.1.50` ou `192.168.1.50:5961`); o **+** adiciona até 6 câmeras; a lixeira pede confirmação. O ícone de rede abre os **IPs fixos** (para câmeras em outra rede).
- [ ] **Rede**: cada câmera chega com ~125 Mbps e há duas conectadas (Prévia e No ar): só em cabo gigabit, nunca por Wi-Fi.

## 21. Depois que tudo passou

- [ ] **Desinstalar o Alerta-IBT antigo (Electron)**: Configurações do Windows > Aplicativos > "Avisos de Estacionamento - IBT" (Alerta-IBT).
- [ ] Conferir que o `AlertaIBT` continua no início do Windows e que o antigo não aparece mais lá.
- [ ] Guardar o token e a chave num lugar seguro (o app guarda criptografados, mas o dono precisa deles se trocar de PC).

## Se algo der errado

| Sintoma | O que olhar |
|---|---|
| Telegram vermelho "Conflito" | Outro programa lendo o mesmo bot (o app antigo). Fechar o antigo. |
| HTTP/API "Porta ocupada" | O app antigo (porta 8787). Fechar o antigo; o novo toma a porta em até 30 s. |
| Gemini azul "Offline" com internet e chave | O Gemini não está funcionando: log do Diagnóstico, modelo em Configurações > Modelo do Gemini, chave. |
| Barra vermelha | A barra não conseguiu se esconder da captura: log do Diagnóstico. |
| Barra não aparece | LED desligado ou sem 2560x768 (bolinha LED vermelha), app pausado (pílula "Pausada"), ou o aviso foi recusado (histórico). |
| Bolinha Site vermelha ou "PC offline" no celular | Seção "Se der erro" de `docs/SITE-COMO-PUBLICAR.md` (login, regras, internet do PC). |
| Atalho do painel não abre | Outro programa usa a combinação: trocar em Configurações > Sistema; o clique no ícone sempre funciona. |
| O mouse não trava (ou escapa para o LED) | Ligar a chave em Configurações > Sistema (vem desligada); Diagnóstico: linhas "Mouse travado" e "Travamento do mouse refeito". Ctrl+Alt+M liga e desliga; Ctrl 3 vezes traz o mouse de volta. |
| Qualquer erro estranho | Configurações > Diagnóstico > Copiar logs e me mandar. |
