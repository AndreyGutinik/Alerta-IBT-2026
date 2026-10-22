# Checklist do teste na igreja (Alerta-IBT 8.0.0)

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
- [ ] O app abre sozinho, com a **janela** (primeira vez), escura, com a fonte Marble e os acentos certos (Ã, Ç, É), a barra lateral com Início, Correção e Configurações e, embaixo, cinco linhas pequenas de status.
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
- [ ] Em **Correção**: colar a chave (vira bolinhas); o seletor fica em **Gemini**. Esperar "Salvo".
- [ ] **Testar um aviso com a chave.** No painel rápido ou em Início, digitar `prata corolla bra2e19 vidro abrto` e esperar cerca de 0,7 s: a prévia deve aparecer **verde** ("Vai aparecer assim: COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO") e a bolinha do **Gemini** ficar **verde "Online"**.
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

  Conferir a chave e o **modelo** em Correção > Avançado (vazio = padrão `gemini-3.5-flash-lite`) e anotar a linha do log para eu corrigir.
- [ ] Mandar um texto ofensivo com a chave configurada: **nada** passa na barra, o grupo recebe `❌ ... conteúdo impróprio.` e o histórico mostra a linha ❌.
- [ ] Tirar o Wi-Fi/cabo de rede e mandar um aviso: a prévia sai azul "(offline)", o aviso passa corrigido pelo dicionário offline e o Gemini fica azul "Offline". Religar a rede: volta a verde sozinho no próximo aviso.
- [ ] Colar uma chave com espaço ou curta: recusada com a dica.
- [ ] Em Correção, desligar a correção automática: a bolinha do Gemini fica cinza "Desligada". Ligar de novo e escolher **Só offline**: azul "Offline". Voltar para Gemini.
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

## 16. Depois que tudo passou

- [ ] **Desinstalar o Alerta-IBT antigo (Electron)**: Configurações do Windows > Aplicativos > "Avisos de Estacionamento - IBT" (Alerta-IBT).
- [ ] Conferir que o `AlertaIBT` continua no início do Windows e que o antigo não aparece mais lá.
- [ ] Guardar o token e a chave num lugar seguro (o app guarda criptografados, mas o dono precisa deles se trocar de PC).

## Se algo der errado

| Sintoma | O que olhar |
|---|---|
| Telegram vermelho "Conflito" | Outro programa lendo o mesmo bot (o app antigo). Fechar o antigo. |
| HTTP/API "Porta ocupada" | O app antigo (porta 8787). Fechar o antigo; o novo toma a porta em até 30 s. |
| Gemini azul "Offline" com internet e chave | O Gemini não está funcionando: log do Diagnóstico, modelo em Correção > Avançado, chave. |
| Barra vermelha | A barra não conseguiu se esconder da captura: log do Diagnóstico. |
| Barra não aparece | LED desligado ou sem 2560x768 (bolinha LED vermelha), app pausado (pílula "Pausada"), ou o aviso foi recusado (histórico). |
| Bolinha Site vermelha ou "PC offline" no celular | Seção "Se der erro" de `docs/SITE-COMO-PUBLICAR.md` (login, regras, internet do PC). |
| Atalho do painel não abre | Outro programa usa a combinação: trocar em Configurações > Sistema; o clique no ícone sempre funciona. |
| Qualquer erro estranho | Configurações > Diagnóstico > Copiar logs e me mandar. |
