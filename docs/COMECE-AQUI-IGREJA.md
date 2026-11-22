# COMECE AQUI (PC da igreja já configurado)

> Pen drive gerado em **{{DATA}}** · app **{{VERSAO}}** · commit **{{COMMIT}}**.
> O guia completo está em `LEIA-ME.md`. Esta página é o caminho curto.

## 1. Ligue o Claude Code com o contexto (2 minutos)

1. Abra a pasta do projeto no VS Code (a que você clonou do GitHub) e abra o Claude Code.
2. Abra o arquivo `COLE-NO-CLAUDE.txt` desta pasta, copie **tudo** e cole no Claude. Ele já sabe o que fazer: lê a memória, protege o que você já mudou lá e propõe os testes.
3. Se o Claude disser que não lembra do projeto, faça o passo 5 do `LEIA-ME.md` (copiar `1-memoria\` para a pasta de memória do Claude) e cole o texto de novo.

## 2. Scripts do Holyrics (o que muda hoje)

Os scripts **não** estão no GitHub; estão em `2-api-holyrics\`.

- **Fade da luz (`10s-f2`):** abra `PATCH-fade-em-degraus.txt` e aplique as 2 trocas no Startup do Holyrics **que você já tem lá** (não substitua o arquivo inteiro: o de lá é mais novo). Antes, teste na linha de comando da MA2: `Master 2.1 At 0`, `At 50`, `At 100`.
- Se quiser o arquivo completo de casa como referência: `api-holyrics-novo`.
- Na igreja: `EM_CASA = false` no Startup **e** no Shutdown.

## 3. Se algo der errado

- Peça ao Claude: "me mostra o que mudou em relação ao GitHub" (`git status` e `git diff`).
- Para **mandar o trabalho de lá para o GitHub**: peça "faz commit e push do que mudei aqui". Os scripts do Holyrics (`API Holyrics\`) ficam de fora do git de propósito.
- Para **trazer o trabalho de lá para casa**: peça "atualiza a pasta levar-para-a-igreja a partir daqui" e copie de volta para o pen drive.

## 3b. Ao ir embora (para ter tudo em casa)

O `COLE-NO-CLAUDE.txt` já manda o Claude fazer commit a cada alteração e push no fim. Antes de sair, diga **"vou embora"**: ele confere que nada ficou sem commit nem push e copia para o pen drive o que não vai para o git (scripts do Holyrics e memórias). Em casa:

1. Abra o projeto e peça "puxa o que veio da igreja" (`git pull`).
2. Copie `2-api-holyrics\` do pen drive de volta para a pasta `API Holyrics` do projeto, conferindo antes qual versão é a mais nova.

## 4. O que tem no pen drive

| Pasta / arquivo | Para que serve |
|---|---|
| `COLE-NO-CLAUDE.txt` | Texto para colar no Claude ao chegar. |
| `LEIA-ME.md` | Guia completo e perguntas em aberto. |
| `1-memoria\` | Memórias do Claude sobre o projeto. |
| `2-api-holyrics\` | Scripts do Holyrics + o patch do fade. **Tem IPs e senha da MA2.** |
| `3-instalador\` | Instalador do IBT Controller. |
| `4-checklists\` | Checklist de testes na igreja e passos da MA2. |
| `6-guarda-git\` | Hook que avisa o Claude, a cada resposta, se há trabalho sem commit/push (o `COLE-NO-CLAUDE.txt` manda instalar). |
| `5-codigo-offline\` | Cópia do código (`git bundle`), para o caso de o GitHub não abrir. |

## 5. Segurança

Tokens (Holyrics, Gemini, Telegram) **não** estão aqui: digite de novo. Apague o pen drive depois.
