# Controle pela grandMA2 (aba Controle)

A grandMA2 onPC (em outro PC, ligado por switch) manda **Art-Net** na rede 2.x e o IBT Controller **obedece**: escolhe câmeras, corta, liga a câmera no telão, controla a música, a posição vertical da câmera e os volumes. O app só **escuta**: nunca manda nada na rede.

> **Honestidade:** os passos da parte da MA2 abaixo foram escritos a partir do conhecimento do grandMA2 (versão 3.x/onPC) e **precisam ser confirmados no onPC**: os nomes de menus podem variar um pouco entre versões. O lado do app foi testado com um simulador de Art-Net (parte 5).

## 1. Como funciona (resumo)

- O app escuta **UDP 6454** em **todas as placas de rede** (`0.0.0.0`). Por isso não importa qual é o IP deste PC na rede 2.x (o PC do app tem uma segunda placa nessa rede); a MA2 onPC do dono é **2.0.0.2**.
- Um **universo** só (padrão **100**, como a MA2 mostra). A MA2 conta universos a partir de 1 na tela e o Art-Net a partir de 0: com o botão **+1** ligado (padrão) o app escuta o Art-Net 99 quando a MA2 mostra 100.
- **IP da MA2** (opcional): vazio aceita qualquer origem; preenchido (2.0.0.2), o app ignora pacotes de outros endereços.
- **Sem sinal:** se não chegar pacote do universo por 3 segundos, o chip fica amarelo e o app **mantém tudo como está**. Quando o sinal volta, os valores que a MA2 já segura **não disparam nada** (o primeiro pacote só vira linha de base). O mesmo vale ao ligar a chave.
- A chave **Controle pela MA2** vem **desligada** na instalação; desligada, a porta fica fechada.

## 2. Mapa padrão (universo 100)

| Canal | Ação | Modo |
|---|---|---|
| 1 a 6 | Câmera 1 a 6 na prévia | Seleção (na subida) |
| 7 | Câmera no telão | Estado (ligado liga, desligado desliga) |
| 8 | Cortar | Botão |
| 9 | Tocar/pausar a música | Botão |
| 10 | Parar a música | Botão |
| 11 | Avançar a música | Botão |
| 12 | Voltar a música | Botão |
| 13 a 16 | Slide próximo, slide anterior, play/pausa do vídeo, logo do Holyrics | Botão (ver a parte 3b) |
| 17 | Posição vertical da PRÉVIA (0 a 100 %) | Fader |
| 18 | Posição vertical do NO AR | Fader |
| 19 | Volume do Windows | Fader |
| 20 | Volume do Holyrics | Fader |
| 21 | Volume do player | Fader |

Modos: **Botão** roda a ação (valor 128 ou mais é "ligado"; "Qualquer mudança" é o padrão porque as macros seguram o canal ligado ou desligado, e "Só na subida" roda só quando liga; 100 ms de proteção contra repique). **Seleção** escolhe a câmera na subida. **Estado** acompanha o canal. **Fader** vai de 0 a 255 para 0 a 100 %, atualizado no máximo ~30 vezes por segundo. **Pickup suave** (padrão ligado, por fader): o app ignora o fader até ele **cruzar o valor atual do app** e só então acompanha, assim o valor nunca dá salto com fader sem motor; se o valor do app mudar por outro lado (o controle deslizante na tela), o fader precisa cruzar de novo. Uma ampulheta amarela mostra "esperando" e um visto verde "assumiu". O mesmo canal pode ter duas ações (o app avisa com um ponto amarelo). **Restaurar mapa padrão** volta às 21 linhas.

## 3. Passo a passo na MA2 (confirmar no onPC)

1. **Rede:** *Setup > Network > DMX Protocols*: ligue **Art-Net**; escolha a **interface** da placa da rede 2.x; deixe a saída (*Output*) em **broadcast** e crie a saída do **universo 100** (a MA2 mostra 100; o app escuta o 99 do cabo por causa do +1).
2. **Patch:** crie **21 canais genéricos** no **universo 100, endereços 1 a 21**: pode ser uma fixture de 1 canal repetida 21 vezes (dimmer genérico) ou uma fixture "dummy" de 21 canais.
3. **Macros (botões):** na *View* de macros, cada botão tem uma linha de comando que põe o dimmer certo em **100** (ligado) ou **0** (desligado) e deixa assim (estilo estado), por exemplo `Fixture 8 At 100` e `Fixture 8 At 0` (ajuste a sintaxe à sua versão). Use a mesma macro para ligar e outra para desligar, ou uma que alterna. Dê **ícones** aos botões (Cortar, câmeras 1 a 6, play, parar, avançar, voltar).
4. **Faders (canais 17 a 21):** atribua cada um a um **executor de fader** sobre o dimmer correspondente (um fader por dimmer 17, 18, 19, 20, 21).
5. **App:** aba **Controle**: ligue **Controle pela MA2**, confira o **universo 100** com o **+1** ligado e, se quiser, escreva **2.0.0.2** em **IP da MA2**. O chip deve ficar verde (**Sinal ok**); a barra de cada linha mostra o valor que chega.

**Rede e a saída DMX física:** o universo 100 só vai para o cabo/nó DMX se estiver mapeado na saída do nó; se o universo 100 não for mapeado no nó, ele não conflita com a iluminação. Confirme isso no onPC.

## 3b. Holyrics (linhas 13 a 16 e setas do painel rápido)

O app controla o Holyrics **só pela API HTTP dele** (nada de teclas simuladas na janela). Cartão **Holyrics** da aba Controle:

1. No Holyrics: *Arquivo > Configurações > API Server*: ligue o servidor, anote a porta (padrão **8091**) e crie um **token** com permissão para `ActionNext`, `ActionPrevious`, `MediaPlayerAction`, `GetMediaPlayerInfo` e `SetLogo`.
2. No app: endereço (**127.0.0.1** se o Holyrics está neste PC; o IP dele se for outro PC), porta, token (**Colar**; fica protegido por DPAPI, só aparecem pontos) e o raio **Testar**. O chip diz **conectado**, **token sem permissão para X**, **token não aceito** ou **sem resposta** (2 s). Testar só pergunta (`CheckPermissions` e `GetMediaPlayerInfo`), não muda nada no Holyrics.
3. Sem token nada é enviado; as linhas 13 a 16 falham no log com "Holyrics sem token".

| Canal | Ação | O que o app manda (`POST /api/AÇÃO?token=...`, corpo JSON) |
| --- | --- | --- |
| 13 | Slide próximo | `ActionNext` `{}` |
| 14 | Slide anterior | `ActionPrevious` `{}` |
| 15 | Play/pausa do vídeo | `GetMediaPlayerInfo`; se `playing` é verdadeiro, `MediaPlayerAction {"action":"pause"}`, senão `{"action":"play"}` |
| 16 | Logo | ligado: `SetLogo {"enable":true,"hide":false}`; desligado: `SetLogo {"enable":false,"hide":true}` |

**Logo.** O Holyrics **não tem como ler** o estado do logo (a API só escreve), então o app guarda o dele (começa escondido). Modo **Botão** (padrão da linha 16, "qualquer mudança"): cada mudança do canal **alterna** o estado do app, igual ao Ctrl+K. Se alguém mexer no logo direto no Holyrics, o app perde a conta e a próxima mudança pode ficar invertida. Modo **Estado**: o canal É o logo (ligado mostra, desligado esconde), sem deriva, recomendado se a macro segura o canal. **Inferência (não confirmada em tela):** `enable` liga a função Logo do menu Ferramentas e `hide` mostra/oculta (a documentação diz "enable/disable functionality" e "show/hide functionality"); o desligado do app é o estado em que o Holyrics do dono foi encontrado (desligado e escondido).

**Sem apresentação aberta**, `ActionNext`/`ActionPrevious` respondem `{"status":"error","error":"No presentation available"}` (HTTP 200): o app trata como "nada para mover", não como falha da conexão.

**Anti-inundação:** um botão segurado não sobrecarrega o Holyrics: as chamadas saem uma de cada vez em segundo plano, no máximo uma de cada tipo esperando, a mesma ação não repete em menos de 150 ms e a chamada nunca trava o app (nem a fila da MA2). O Holyrics fora do ar não enche o log (1 aviso por tipo a cada 5 s). Não entra no backup.

**Setas do painel rápido:** com a chave **Setas do painel rápido controlam o Holyrics** ligada (padrão) e o token configurado, enquanto você digita o aviso na caixa do painel da bandeja, **Seta direita = slide próximo** e **Seta esquerda = slide anterior**. Só vale com o cursor nessa caixa (nunca no resto do Windows), sem Shift/Ctrl/Alt, tecla segurada não repete, e as setas **não movem mais o cursor do texto** (use mouse, Home e End). Sem token ou com a chave desligada elas movem o cursor normalmente. Funciona igual com ou sem a MA2.

**Macros da MA2 (lista):** canal 13 = slide próximo, 14 = slide anterior, 15 = play/pausa do vídeo, 16 = logo; cada macro põe o dimmer em 100 e 0 como as outras. Passos da MA2 continuam a confirmar no onPC.

## 4. Riscos conhecidos

- **A MA2 onPC no MESMO PC do app** (ou outro programa Art-Net, ou um nó/driver DMX) pode ocupar a porta 6454. O app liga com compartilhamento de endereço e coexiste com quem também compartilha; com quem ocupa de forma exclusiva, o chip fica vermelho (**Porta 6454 em uso**) e o app tenta de novo a cada 3 s, sem travar. Broadcast chega a todos os ouvintes; pacote unicast (para um IP) só chega a um.
- O app **não responde ArtPoll** (a MA2 não vai listar o app como nó): é só um ouvinte. Foi escolha deliberada por segurança.
- O firewall do Windows pode pedir permissão para o app na primeira vez (rede privada).

## 5. Testar sem a MA2 (simulador)

O projeto de testes tem um simulador (`desktop/tests/AlertaIBT.Core.Tests/Control/ArtNetSimulator.cs`). Escreva um roteiro de texto (`#` é comentário):

```
target 127.0.0.1 6454
universe 99
rate 40
wait 1000
pulse 8 300
set 7 255
wait 1000
ramp 21 0 255 3000
silence 4000
set 7 0
wait 1000
```

Comandos: `target IP porta` (ou `255.255.255.255` para broadcast, ou o IP do PC do app), `universe N` (no cabo: 99 para o 100 da MA2), `rate N`, `set canal valor`, `pulse canal ms`, `ramp canal de até ms`, `wait ms`, `silence ms` (para de enviar: o app mostra "sem sinal"). Rodar:

```powershell
$env:IBT_ARTNET_SCRIPT = "C:\caminho\roteiro.txt"
dotnet test --project desktop/tests/AlertaIBT.Core.Tests --filter-class "*ArtNetSimulatorTool*"
```

Sem a variável, o teste é pulado. Ligue **Controle pela MA2** no app antes. Para ver só um canal sem a MA2, use o botão **Testar** (raio) de cada linha.
