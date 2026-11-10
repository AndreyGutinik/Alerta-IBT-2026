# Levar o trabalho para o PC da igreja

> Pasta gerada em **{{DATA}}** · app na versão **{{VERSAO}}** · commit **{{COMMIT}}**.
> Para atualizar esta pasta, peça ao Claude: "atualiza a pasta levar-para-a-igreja".

Este guia explica como continuar o trabalho com o Claude Code **no PC da igreja**, com acesso real à MA2, à mesa WING, ao painel de LED e ao Holyrics de verdade.

## O que tem nesta pasta

| Pasta / arquivo | Para que serve |
|---|---|
| `LEIA-ME.md` | Este guia. |
| `1-memoria\` | As memórias do Claude sobre o projeto (7 arquivos pequenos). Vão para dentro da pasta de projetos do Claude no PC da igreja (passo 5). |
| `2-api-holyrics\` | Seus scripts do Holyrics: `api-holyrics` e `Shutdown` (os atuais de uso) e `api-holyrics-novo` e `Shutdown-novo` (as versões novas, sem log, com painel rápido e luz antecipada). **Contém IPs e a senha da MA2**: não suba para a internet. |
| `3-instalador\` | O instalador do IBT Controller da versão acima, para o caso de o PC da igreja estar sem internet. Fica vazio se o instalador dessa versão não estiver na pasta `artifacts` de casa. |

O código do projeto **não** está nesta pasta: ele vem do GitHub (passo 3), que é sempre a versão mais nova.

## Passo a passo no PC da igreja

1. **Instale o necessário** (uma vez só):
   - Git: https://git-scm.com/download/win
   - .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0 (para compilar e rodar os testes do app)
   - Node.js LTS: https://nodejs.org (para os simuladores de teste dos scripts do Holyrics)
   - GitHub CLI (`gh`), só se for publicar versões de lá: https://cli.github.com
2. **Entre no VS Code** e confirme que o plugin do Claude Code está instalado e logado **na mesma conta** de casa (a cota de uso é a mesma).
3. **Baixe o projeto** (PowerShell):
   ```powershell
   cd $env:USERPROFILE\Documents
   git clone https://github.com/AndreyGutinik/Alerta-IBT-2026 Alerta-IBT
   ```
   Abra a pasta `Alerta-IBT` no VS Code. Se o PC não tiver internet, instale pelo `3-instalador\` e peça ao Claude, em casa, um pacote offline do código.
4. **Copie a pasta `2-api-holyrics\`** para dentro de `Alerta-IBT\`, com o nome `API Holyrics` (é ela que o Claude espera). Ela não está no git de propósito.
5. **Restaure a memória do Claude:**
   1. No VS Code, com a pasta `Alerta-IBT` aberta, abra o Claude Code e escreva só "oi". Isso cria a pasta de projetos do Claude.
   2. Feche. Abra `C:\Users\<seu usuário>\.claude\projects\` e entre na subpasta cujo nome termina em `Alerta-IBT`.
   3. Dentro dela, abra (ou crie) a subpasta `memory` e copie para lá os arquivos de `1-memoria\`.
6. **Digite de novo os segredos no app** (não existe cópia deles; ficam protegidos pelo Windows de cada PC): token do Holyrics, chave do Gemini, token do bot do Telegram. Nunca os cole em arquivos do repositório.
7. **Abra o Claude Code e diga: "estou na igreja".** Ele relê a memória e o repositório, confere a versão instalada do app e propõe os testes pendentes (lista abaixo).

## Antes de mexer em qualquer coisa lá

- Faça os testes **fora do horário do culto**, com a igreja vazia.
- Nos scripts do Holyrics, **`EM_CASA = false`** (igreja). Em casa fica `true`. Os ouvintes falsos de teste só servem em casa.
- Combine com o Claude antes de qualquer comando que mude a luz ou a mesa de verdade.
- O Claude **não vê** as luzes, o painel de LED nem ouve o som: você confirma o que viu e ouviu. Ele consegue ler as telas do PC (inclusive a MA2 onPC), falar com o Holyrics pela API e mandar comandos à MA2 (Telnet) e à WING (OSC).

## Pendências para testar na igreja

- Luz antecipada com fade nos vídeos (`10s-f2`): conferir a sintaxe do Grand Master na MA2 (`FADE_GM_BAIXO`, `FADE_GM_SOBE`, `FADE_GM_SEGURANCA` no topo do script) e se o fade ficou suave.
- Shutdown novo (`Shutdown-novo`): só roda quando o Holyrics fecha.
- Logo: presets pelo comando novo e o `SetLogo` (liga/desliga) contra o `Ctrl+K` do Holyrics.
- Tema só pela tag: um tema sem tag não pode acender nenhuma cor.
- Controle da MA2 pelo app (Art-Net, universo 100): passos em `docs\CONTROLE-MA2.md`, escritos de memória, ainda não confirmados numa MA2 real.
- Modo apresentação, NDI na LED, equalização de volume e backup: itens em `docs\CHECKLIST-IGREJA.md`.

## O que ainda falta o Claude saber (responda quando estiver lá)

- [ ] O PC da igreja tem internet? (decide entre clonar do GitHub e instalar offline)
- [ ] Qual o nome de usuário do Windows lá?
- [ ] O Holyrics de lá é o mesmo (versão 2.30.0) e o ambiente aparece como `PC TEMPLO MAIOR`?
- [ ] A MA2 é console física, onPC, ou os dois? Qual o IP dela na rede do PC (hoje usamos `2.0.0.2`)?
- [ ] A WING está em `10.10.2.1` (porta OSC `2223`) e o PC enxerga essa rede?
- [ ] Em que monitor está o painel de LED (resolução 2560x768) e como ele aparece no Windows?

## Resumo de segurança

- Esta pasta tem **IPs da igreja e a senha da MA2**. Guarde o pen drive com cuidado e apague depois de usar.
- Os tokens (Holyrics, Gemini, Telegram) **não** vão no pen drive.
- O repositório do GitHub é público: nada daqui deve ir para ele.
