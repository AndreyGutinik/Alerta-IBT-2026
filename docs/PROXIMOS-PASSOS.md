# Próximos passos (fora do Plano 1)

Estado em 2026-10-21. Este documento só diz o que existe e o que falta; as decisões estão na [especificação](superpowers/specs/2026-10-19-alerta-ibt-csharp-design.md).

## Pronto (Plano 1: app do PC, versão 8.0.0)

Barra no LED fora da captura, fila, Telegram, correção (Gemini + offline), painel da bandeja, janela, API local 8787, instalador e atualização automática (Velopack: abre, procura, instala e reinicia sozinho quando ocioso; vê pré-lançamentos). Está no branch `v8-csharp`, **ainda sem teste na igreja**: o roteiro é o [CHECKLIST-IGREJA.md](CHECKLIST-IGREJA.md).

Antes de mesclar na `main`: testar na igreja, corrigir o que o teste mostrar e fazer a revisão final do branch inteiro. Nada foi enviado ao GitHub (sem push, sem Release).

## Falta: Plano 2 (Firebase, site e conector)

1. **Projeto Firebase "Alerta IBT"** (plano grátis): Authentication (Google + e-mail/senha da conta do PC), Realtime Database, Hosting `alertaibt` (`alertaibt.web.app`), AI Logic e App Check. **Precisa da aprovação do dono antes de criar qualquer coisa** (spec 13). Nada disso foi criado: nenhum projeto, site, banco ou regra existe.
2. **Regras de segurança do banco** (spec 9.3) e a pasta de regras/`firebase.json`. O trabalho do site e das regras foi começado num worktree separado (branch `v8-site`), só com emulador, e está **pausado e sem mesclar**.
3. **Site PWA** (React + Vite + TypeScript, `web/`): login Google, enviar aviso com prévia, status ao vivo, aba Equipe para admin (spec 10).
4. **Conector do PC com o Firebase**: login REST da conta do PC, streaming do banco, processar pedidos `pending`, publicar `/live` e heartbeat (spec 9.4). Faz o ponto "Site" da janela sair de cinza "Não configurado". Aqui entra também o limite de **10 avisos por hora por membro do site** (não foi construído; o ponto de ligação é `AlertService.SubmitAsync` com `AlertSource.Site` e o `senderId`).
5. `shared/parser-cases.json` já existe e é usado pelo C#; o site vai usar o mesmo arquivo nos testes dele.

## Falta: Plano 3 (APK Android)

Capacitor empacotando o `web/dist`, login Google nativo, voz e vibração nativos, APK assinado nas Releases do GitHub com aviso de versão nova. Só começa depois do PC e do site estarem testados na igreja (spec 11). A chave de assinatura do APK precisa de cópia de segurança.

## Atualização automática: a primeira instalação é à mão (bootstrap)

As versões instaladas **antes** da 8.0.1 (as 8.0.0-pre.N) só enxergam lançamentos estáveis e só instalam na abertura seguinte, então **não conseguem se atualizar sozinhas para a 8.0.1**. O dono instala a primeira versão com atualização automática **uma vez, à mão** (rodar o `AlertaIBT-win-Setup.exe` dela por cima; as configurações em `%AppData%\AlertaIBT` ficam). Daí em diante todas chegam sozinhas, pré-lançamentos incluídos. Para um canal só estável no futuro, basta trocar `VelopackUpdateClient.IncludePreReleases` para `false`.

## Pequenos itens do app do PC que ficaram de fora

- Instalador sem assinatura digital: o Windows mostra o aviso "O Windows protegeu o computador" na primeira instalação. O caminho grátis (SignPath Foundation) e o que só o dono pode fazer estão em [ASSINATURA.md](ASSINATURA.md); ele exige uma licença de código aberto, que ainda não foi escolhida.
- Os ícones Lucide foram escritos à mão a partir da memória; conferir se estão bonitos (item no checklist).

## Adiado na revisão final do branch (Plano 1)

Os problemas importantes da revisão final já foram corrigidos (janelas fora do LED e da captura, log do Gemini, placa com espaço, limite de 200 letras antes da correção, trava de cor, validação do atalho, mais modelos de carro, log do dicionário, memória de placas repetidas). Ficou para depois, com o detalhe em `.superpowers/sdd/2026-10-19-alerta-ibt-v8-pc/final-review.md`:

- **Texto ofensivo com o Gemini fora do ar** (M1): o corretor offline não recusa palavrão, então ele chega ao LED. Hoje só o grupo do Telegram, de confiança, manda avisos. **Decidir a política (lista de palavras proibidas no caminho offline) antes de abrir o site aos membros no Plano 2.**
- **API local aceita qualquer página do navegador** (M9, igual à v7): sem checagem de `Origin`/`Host`, qualquer página aberta no PC da igreja pode pausar ou retomar a barra. Recusar origens que não sejam `127.0.0.1`/`localhost`.
- **A trava de segurança ainda deixa passar** (M3): troca de modelo (gol virar golf), a palavra "não" que some, motivo descartado. A cor já está travada; faltam modelo e negações.
- **Memória na versão Release** (M14): o dicionário sozinho usa cerca de 50 MB; a meta agora é **até 350 MB** (decisão do dono), então não há pressa; medir no PC da igreja. Opcional, só se um dia passar disso: carregar o dicionário só quando o Gemini falhar.
- **Ordem das palavras ao redor da placa sem o Gemini** (M5), **relógio do PC adiantado derruba mensagens do Telegram** (M11), **sair do app pode travar até 3 s e respostas do Telegram na fila se perdem** (M12), **campos de segredo travam um texto digitado pela metade** (M13), **acentos soltos e caracteres invisíveis no texto** (M15), **a barra pode piscar no monitor do operador se o LED for desligado durante uma passada** (M16), **o workflow de Release nunca rodou e os testes do app precisam de uma área de trabalho** (M17).
