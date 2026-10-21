# Alerta-IBT 8 — Plano 2A: regras do Firebase e site PWA Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Site PWA `alertaibt.web.app` (React + Vite + TypeScript) com login Google, envio de avisos com prévia por IA, status ao vivo com mini-telão e aba Equipe, mais as regras do Realtime Database (spec 9.2/9.3) testadas no emulador.

**Architecture:** `web/` é um app React em camadas: `alerts/` (porta TypeScript pura do formato/regra da spec 5.2), `correction/` (IA atrás da interface `AiCorrector`, trava de segurança e reserva local), `data/` (contrato do banco, escritas e funções puras de estado), `firebase/` (adaptadores finos de Auth, RTDB, AI Logic + App Check) e as telas (`ui/`, `send/`, `status/`, `team/`), que só falam com as interfaces `Backend` e `AuthPort` e são testadas com fakes. `database.rules.json` implementa a 9.3 e é testado no emulador usando as mesmas funções de escrita do app. Fora deste plano: conector do PC no Firebase (Plano 2B) e APK (Plano 3).

**Tech Stack:** React 19, Vite 8, TypeScript 7, Firebase JS SDK 12 (Auth, Database, AI Logic, App Check), lucide-react, vite-plugin-pwa, Vitest 5 + Testing Library + jsdom, `@firebase/rules-unit-testing` 5, Firebase Emulator Suite (firebase-tools 14.27.0), Playwright.

**Spec:** `docs/superpowers/specs/2026-10-19-alerta-ibt-csharp-design.md` (seções 3, 5.2–5.6, 9, 10 e 14) + mockup `docs/superpowers/specs/mockups/site-v5.html` (alvo visual: tokens de cor, componentes, textos e o tutorial). Ler os dois junto com este plano.

## Global Constraints

- Todo comando `npm`/`npx` roda dentro de `web/`. Pastas proibidas: `desktop/` e `shared/` (são do Plano 1; só a Task 14 mexe em `shared/`, depois do merge).
- **Nuvem proibida:** nunca rodar `firebase login`, `firebase deploy`, `firebase use`, nem nada que fale com um projeto real. Emulador sempre com `--project demo-alerta-ibt` (projeto `demo-*` não toca na nuvem). Nunca dar push.
- Versões: `firebase ^12.19.0` (não usar a 13), `react`/`react-dom ^19.3.1`, `typescript ^7.0.2`, `vite ^8.3.4`, `@vitejs/plugin-react ^6.1.2`, `vite-plugin-pwa ^1.3.0`, `vitest ^5.0.5`, `lucide-react ^1.59.0`, `@firebase/rules-unit-testing ^5.0.2`, `firebase-tools` **exato `14.27.0`** (última versão cujos emuladores rodam no Java 17 instalado; a 15.x exige Java 21).
- Interface em pt-BR; tema **segue o sistema** (`prefers-color-scheme`, claro e escuro com os tokens do mockup); **escala 90%** (`html { font-size: 90% }` e todo tamanho do app em `rem` = px do mockup ÷ 16); fonte Marble (pesos 400/500/600/700 de `assets/fonts`); ícones Lucide numa cor só.
- Texto final sempre **MAIÚSCULAS**; formato `MODELO COR | PLACA: ABC-1234 | MOTIVO`; Mercosul exibida `ABC-1D23`; partes que faltam somem com o separador.
- Membro: modelo, cor e placa obrigatórios (Enviar bloqueado). Admin: qualquer texto; sem placa vira **aviso livre**.
- `raw` até **300** caracteres; prévia com espera de **700 ms** depois que a pessoa para de digitar; IA com tempo máximo de **3 s**, erro → parser local (`preview.by = "local"`).
- Repetido: mesma placa na fila, passando, ou exibida nos últimos **10 min** (membro bloqueado; admin só avisado). Limite do membro: **1 aviso a cada 20 s** (nas regras); admin sem limite. "PC offline" quando o heartbeat tem mais de **90 s** (sempre pela hora do servidor, `.info/serverTimeOffset`).
- Urgente só **segurando 1 s** (vibração de 60 ms); toque rápido mostra "Segure o ícone 1 s para urgente".
- Sem push, sem fila offline (sem sinal = erro na hora e a pessoa tenta de novo), sem câmera, sem modo uma mão.
- Textos de interface exatamente como neste plano, na spec 5.6 e no mockup.
- Commits em pt-BR no estilo `feat(site): …`, terminando com a linha `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Contrato com o PC (Plano 2B)

Decisões deste plano sobre os dados da 9.2 que o conector do PC precisa seguir:

- Pedido novo vai **sem `status`**: ausente = pendente. Só a conta do PC escreve `status` (o PC trata `status` ausente ou `state: "pending"` como pendente).
- No mesmo envio do pedido o site grava `users/{uid}/lastSentAt` (hora do servidor), `users/{uid}/lastRequestId` (= id do pedido, campo novo: amarra **um** pedido por envio para o limite de 20 s não ser furado) e `users/{uid}/sentCount` (+1).
- `status.reason` é texto curto pt-BR pronto para mostrar (ex.: `faltou placa`, `repetido`, `já passou há 4 min`, `pregação`, `conteúdo impróprio`, `limite de 10 por hora`). `status.position` começa em 1. Horários (`at`, `createdAt`, `heartbeatAt`, `startedAt`) em ms, hora do servidor.
- `/live/queue[].source` e `/live/now.source` ∈ `"telegram" | "site" | "pc"`; para item do site, `id` = id do pedido (admin cancela gravando `requests/{id}/cancel = true`). `/live/now.plate` é opcional (sem ele o site lê `PLACA: XXX-XXXX` do texto). `/live/recent[].state` ∈ `shown | rejected | cancelled | expired`. `/live/bar` = `{ position: "top"|"bottom", height, fontSize?, speed }` (sem `fontSize` = 64% da altura).
- Pessoas só entram com Google (`sign_in_provider == "google.com"`). A conta do PC (e-mail/senha) não tem registro em `/users`; só `/devices/{uid} = true`.

## Review Focus

1. Enviar sem conexão: o SDK do RTDB guarda escritas offline em silêncio; a pessoa tem de ver erro na hora e nada fica na fila escondido (teste na Task 8).
2. PC caiu com um aviso em `/live/now`: depois de 90 s sem heartbeat o site mostra "PC offline" e o mini-telão para de mostrar o aviso velho (testes nas Tasks 3 e 10).
3. Texto colado com quebras de linha, emoji ou mais de 300 caracteres: vira uma linha, cortado em 300, a prévia funciona e as regras aceitam exatamente 300 (testes nas Tasks 2, 4 e 7).
4. Toque duplo em Enviar (ou toque durante o segurar do Urgente): sai **um** pedido só (teste na Task 8).
5. Respostas da IA chegando fora de ordem enquanto a pessoa digita: a prévia sempre reflete o texto mais novo (teste na Task 7).

---

### Task 1: Projeto `web/` (Vite + React + TS + Vitest), fonte e tema

**Files:**
- Create: `web/package.json`, `web/tsconfig.json`, `web/vite.config.ts`, `web/index.html`, `web/.gitignore`, `web/src/main.tsx`, `web/src/App.tsx`, `web/src/App.test.tsx`, `web/src/styles/fonts.css`, `web/src/styles/tokens.css`, `web/src/styles/base.css`, `web/src/test/setup.ts`

**Interfaces:**
- Produces: scripts `dev` (`vite`), `build` (`tsc -b && vite build`), `test` (`vitest run --project unit`); projeto Vitest `unit` (jsdom, `src/**/*.test.{ts,tsx}`, setup `src/test/setup.ts` importando `@testing-library/jest-dom/vitest`); tokens CSS com os nomes do mockup (`--bg --pn --pn2 --ln --tx --mu --field --ac --acs --gl --cr --dg --ok --wn --bad --blue`); família `'Marble'`.

- [ ] **Step 1: Criar `web/package.json`** — `name: "alerta-ibt-site"`, `version: "8.0.0"`, `private: true`, `type: "module"`; dependencies `firebase`, `react`, `react-dom`, `lucide-react`; devDependencies `typescript`, `vite`, `@vitejs/plugin-react`, `vitest`, `jsdom ^30.1.3`, `@testing-library/react ^16.3.3`, `@testing-library/user-event ^14.6.8`, `@testing-library/jest-dom ^7.0.1`, `@types/react ^19.3.0`, `@types/react-dom ^19.3.0`, `@types/node ^24` (versões das Global Constraints). Rodar `npm install`.
- [ ] **Step 2: Escrever o teste que falha** — `web/src/App.test.tsx`: `render(<App />)` e `expect(screen.getByRole('heading', { name: 'Alerta IBT' })).toBeInTheDocument()`.
- [ ] **Step 3: Rodar** `npm test` → FAIL (App não existe).
- [ ] **Step 4: Implementar** — `tsconfig.json` como o do Metrônomo (`ES2022`, `moduleResolution: bundler`, `jsx: react-jsx`, `strict`, `noEmit`, `isolatedModules`, `noUnusedLocals`, `skipLibCheck`, `types: ["vite/client", "node"]`, include `src`, `tests`, `*.config.ts`); `vite.config.ts` com `react()`, `server.fs.allow: [searchForWorkspaceRoot(process.cwd()), resolve(__dirname, '../assets')]` e `test.projects` com o projeto `unit`; `index.html` (`lang="pt-BR"`, viewport com `viewport-fit=cover`, `<meta name="color-scheme" content="dark light">`, `theme-color` `#0a0a0c` no escuro e `#f5f4ef` no claro via `media`, título `Alerta IBT`); `App.tsx` provisório com `<h1>Alerta IBT</h1>`; `fonts.css` com `@font-face` `'Marble'` 400 (`Marble Regular.ttf`), 500 (`Medium`), 600 (`SemiBold`), 700 (`Bold`) apontando para `../../../assets/fonts/` com `font-display: swap`; `tokens.css` com os tokens escuros do mockup (`site-v5.html` linha 7) em `:root` e os claros (linha 9) em `@media (prefers-color-scheme: light)`; `base.css` com `html { font-size: 90% }`, `body { margin: 0; background: var(--bg); color: var(--tx); font-family: 'Marble', 'Segoe UI', system-ui, sans-serif; -webkit-text-size-adjust: 100% }`; `web/.gitignore`: `node_modules`, `dist`, `dev-dist`, `.env*.local`, `test-results`, `playwright-report`, `*.log`.
- [ ] **Step 5: Rodar** `npm test` → PASS (1 teste). Rodar `npm run build` → sem erros, e `web/dist/assets/` contém 4 arquivos `Marble*.ttf` com hash.
- [ ] **Step 6: Commit** `chore(site): projeto web com Vite, React, TS, Vitest, fonte Marble e tema`

### Task 2: Placas, parser, formato e regra (porta TypeScript da spec 5.2)

**Files:**
- Create: `web/src/alerts/{types.ts, plate.ts, vocabulary.ts, parser.ts, format.ts, rules.ts, parser-cases.json}`
- Test: `web/src/alerts/{plate.test.ts, parser.test.ts, cases.test.ts}`

**Interfaces:**
- Produces (`types.ts`): `type Field = 'modelo' | 'cor' | 'placa'`; `type Role = 'member' | 'admin'`; `interface ParsedAlert { model: string; color: string; plate: string | null; reason: string; freeText: string }`; `type RuleKind = 'vehicle' | 'free' | 'reject'`; `interface RuleDecision { kind: RuleKind; missing: Field[] }`.
- Produces: `normalizePlate(candidate: string): string | null` e `extractPlate(text: string): { plate: string; rest: string } | null` (`plate.ts`); `COLORS: readonly string[]`, `isColor(word: string): boolean`, `isReasonStart(word: string): boolean` (`vocabulary.ts`); `parseAlert(raw: string): ParsedAlert` (`parser.ts`); `MAX_RAW = 300`, `upper(text: string): string` (junta espaços, apara, `toLocaleUpperCase('pt-BR')`), `formatVehicle(a: ParsedAlert): string`, `formatFree(text: string): string`, `fieldValue(input: string): string` (troca `\r?\n` por espaço e corta em 300, **sem aparar**: é chamado a cada tecla), `cleanRaw(input: string): string` (junta espaços, apara, corta em 300) (`format.ts`); `missingFields(a: ParsedAlert): Field[]` (ordem `modelo`, `cor`, `placa`), `decide(a: ParsedAlert, role: Role): RuleDecision`, `alertText(a: ParsedAlert, d: RuleDecision): string` (`free` → `formatFree(a.freeText)`, senão `formatVehicle(a)`) (`rules.ts`).

- [ ] **Step 1: Escrever os testes que falham**
  - `plate.test.ts` — `normalizePlate`: `'abc1234'→'ABC-1234'`, `'abc-1d23'→'ABC-1D23'`, `'QWE 1B23'→'QWE-1B23'`, `'gbr- 9003'→'GBR-9003'`, `'a8c1234'→'ABC-1234'`, `'abcI234'→'ABC-1234'`, `'abc-1o23'→'ABC-1O23'`, `'abc123'→null`, `'corolla'→null`. `extractPlate('gol branco placa ffo4745 farol aceso')` → `{ plate: 'FFO-4745', rest: 'gol branco farol aceso' }`; `extractPlate('corolla prata vidro aberto')` → `null`; `extractPlate('creta preto | placa:gbr- 9003 / farol aceso')?.plate` → `'GBR-9003'`.
  - `parser.test.ts` — `fieldValue('a\nb\r\nc')` → `'a b c'`; `fieldValue('x'.repeat(400)).length` → `300`; `fieldValue('gol ')` → `'gol '`; `cleanRaw('  #gol   branco \n ffo4745 ')` → `'#gol branco ffo4745'`; admin com 400 caracteres (`'aviso '.repeat(67)`) → `decide(...).kind === 'free'` e `formatFree` sem espaço duplo, igual a `upper` do texto; `parseAlert('farol aceso | gol branco | ffo4745')` → `model 'gol'`, `color 'branco'`, `reason 'farol aceso'` (primeiro segmento que não começa com motivo é o veículo).
  - `cases.test.ts` — lê `./parser-cases.json` (`Array<{ raw: string; role: Role; expectedText: string; missing: Field[]; kind: RuleKind }>`, mesmo formato previsto para `shared/parser-cases.json`) e, para cada caso, confere `decide(parseAlert(raw), role)` (`kind`, `missing`) e `alertText(...)` contra `expectedText`. Casos no JSON:

    | raw | role | expectedText | missing | kind |
    |---|---|---|---|---|
    | `#creta preto \| placa:gbr- 9003 / farol aceso` | member | `CRETA PRETO \| PLACA: GBR-9003 \| FAROL ACESO` | — | vehicle |
    | `prata corolla bra2e19 vidro aberto` | member | `COROLLA PRATA \| PLACA: BRA-2E19 \| VIDRO ABERTO` | — | vehicle |
    | `onix preto qwe1b23` | member | `ONIX PRETO \| PLACA: QWE-1B23` | — | vehicle |
    | `hb20 vermelho abc1234 parado em frente à garagem` | member | `HB20 VERMELHO \| PLACA: ABC-1234 \| PARADO EM FRENTE À GARAGEM` | — | vehicle |
    | `gol branco farol aceso` | member | `GOL BRANCO \| FAROL ACESO` | placa | reject |
    | `corolla abc1234 vidro aberto` | member | `COROLLA \| PLACA: ABC-1234 \| VIDRO ABERTO` | cor | reject |
    | `prata abc1d23` | member | `PRATA \| PLACA: ABC-1D23` | modelo | reject |
    | `abc1234` | member | `PLACA: ABC-1234` | modelo, cor | reject |
    | `corolla abc1234 vidro aberto` | admin | `COROLLA \| PLACA: ABC-1234 \| VIDRO ABERTO` | cor | vehicle |
    | `pais do pedro henrique da sala kids favor comparecer na recepção` | admin | `PAIS DO PEDRO HENRIQUE DA SALA KIDS FAVOR COMPARECER NA RECEPÇÃO` | cor, placa | free |
    | `gol branco\nffo4745\n\nfarol aceso 🚗` | member | `GOL BRANCO \| PLACA: FFO-4745 \| FAROL ACESO 🚗` | — | vehicle |
    | `Gol Branco placa FFO 4745 farol aceso` | member | `GOL BRANCO \| PLACA: FFO-4745 \| FAROL ACESO` | — | vehicle |
    | `#Ônix preta qwe-1b23` | member | `ÔNIX PRETA \| PLACA: QWE-1B23` | — | vehicle |

- [ ] **Step 2: Rodar** `npm test -- src/alerts` → FAIL (módulos não existem).
- [ ] **Step 3: Implementar** — placa: 7 alfanuméricos depois de tirar espaço, hífen e pontuação; posições 0–2 letras (`0→O, 1→I, 5→S, 8→B`), 3/5/6 números (`O→0, I→1, S→5, B→8`), 4 fica como veio; saída `LLL-XXXX`. Cores (spec 5.2): `preto preta branco branca prata cinza grafite vermelho vermelha azul verde amarelo amarela bege marrom dourado dourada vinho laranja rosa roxo roxa champanhe`, comparando em minúsculas sem acento. Início de motivo (o mesmo `REASON` do mockup): `farol farois vidro vidros porta portas pisca luz luzes alarme som pneu dono dona bloqueando`, ou palavra que começa com `estacionad`, `parad`, `comparec`/`compares`. `parseAlert`:
  1. Trocar `\r?\n` por ` | ` e tirar `#` do começo (`/^\s*#+\s*/`).
  2. Tirar a palavra `placa` com `:` opcional (`/\bplaca\b\s*:?\s*/gi` → espaço).
  3. Procurar candidatos `/(^|[\s|/;,.:])([a-z]{3}\s*-?\s*[0-9oisb][0-9a-z][0-9oisb]{2})(?=$|[\s|/;,.:!?])/gi`; o primeiro com `normalizePlate` não nulo é a placa e vira ` | ` no texto.
  4. Segmentos por `/[|/;]+/`, aparados, sem vazios.
  5. O primeiro segmento cuja primeira palavra **não** é início de motivo dá o veículo: as palavras até o primeiro início de motivo; o resto desse segmento abre o motivo. Os demais segmentos vão para o motivo, juntados com espaço.
  6. `model` = palavras do veículo que não são cor (na ordem); `color` = as que são cor (na ordem).
  7. `freeText` = raw sem `#` inicial, quebras de linha viram espaço, espaços juntados.
  `decide`: membro → `reject` se faltar campo, senão `vehicle`; admin → `vehicle` com placa, `free` sem placa; `missing` sempre preenchido com `missingFields`.
- [ ] **Step 4: Rodar** `npm test -- src/alerts` → PASS.
- [ ] **Step 5: Commit** `feat(site): parser, formato MODELO COR | PLACA | MOTIVO e regra em TypeScript`

### Task 3: Contrato do banco e funções puras de estado

**Files:**
- Create: `web/src/data/{types.ts, writes.ts, live.ts, requestView.ts, time.ts, backend.ts}`
- Test: `web/src/data/{writes.test.ts, live.test.ts, requestView.test.ts, time.test.ts}`

**Interfaces:**
- Consumes: `Role` de `alerts/types.ts`; `upper` de `alerts/format.ts`.
- Produces (`types.ts`): `interface UserRecord { name: string; email: string; role: Role; blocked: boolean; createdAt: number; lastSeenAt?: number; lastSentAt?: number; lastRequestId?: string; sentCount?: number }`; `type RequestState = 'pending' | 'queued' | 'showing' | 'shown' | 'rejected' | 'cancelled' | 'expired'`; `interface RequestPreview { text: string; kind: 'vehicle' | 'free'; plate?: string; by: 'gemini' | 'local' }`; `interface RequestStatus { state: RequestState; position?: number; reason?: string; text?: string; at: number }`; `interface RequestRecord { uid: string; name: string; raw: string; urgent: boolean; createdAt: number; preview?: RequestPreview; cancel: boolean; status?: RequestStatus }`; `type LiveSource = 'telegram' | 'site' | 'pc'`; `interface LiveItem { id: string; text: string; source: LiveSource; urgent?: boolean; plate?: string }`; `interface LiveNow { id: string; text: string; source: LiveSource; startedAt: number; plate?: string }`; `interface LiveRecent { id: string; text: string; state: 'shown' | 'rejected' | 'cancelled' | 'expired'; at: number; plate?: string }`; `interface LiveBar { position: 'top' | 'bottom'; height: number; fontSize?: number; speed: number }`; `interface LiveState { heartbeatAt: number; version?: string; led: boolean; paused: boolean; gemini?: string; now: LiveNow | null; queue: LiveItem[]; recent: LiveRecent[]; bar: LiveBar }`; `interface SessionUser { uid: string; name: string; email: string }`; `interface SendInput { uid: string; name: string; raw: string; urgent: boolean; preview: RequestPreview | null }`; `type WithId<T> = T & { id: string }`.
- Produces (`writes.ts`): `buildUserRecord(u: SessionUser): Record<string, unknown>`; `buildSendUpdate(id: string, input: SendInput): Record<string, unknown>` (update de vários caminhos a partir da raiz).
- Produces (`live.ts`): `PC_OFFLINE_MS = 90_000`, `DUPLICATE_WINDOW_MS = 600_000`, `RATE_LIMIT_MS = 20_000`; `normalizeLive(value: unknown): LiveState | null`; `isPcOnline(live: LiveState | null, serverNow: number): boolean`; `plateFromText(text: string): string | null`; `interface Duplicate { where: 'queue' | 'now' | 'recent'; label: string; minutesAgo?: number }` (`label` = texto antes do primeiro ` | `); `findDuplicate(plate: string | null, live: LiveState | null, serverNow: number): Duplicate | null`; `type Tone = 'ok' | 'warn' | 'bad'`; `headerPill(s: { connected: boolean; live: LiveState | null; serverNow: number }): { tone: Tone; label: string }`; `rateLimitLeft(record: UserRecord | null, role: Role, serverNow: number): number` (segundos, arredondado para cima).
- Produces (`requestView.ts`): `type BadgeTone = 'q' | 'now' | 'ok' | 'no' | 'cx'`; `type BadgeIcon = 'clock' | 'monitor-play' | 'check' | 'x' | 'ban' | 'hourglass'`; `interface RequestBadge { tone: BadgeTone; icon: BadgeIcon; label: string; canCancel: boolean; struck: boolean }`; `requestBadge(r: RequestRecord, live: LiveState | null): RequestBadge`; `requestText(r: RequestRecord): string` (`status.text` → `preview.text` → `upper(raw)`).
- Produces (`time.ts`): `formatHm(ms: number): string` (`HH:mm`, pt-BR, 24 h); `formatDayTime(ms: number): string` (`dd/MM HH:mm`).
- Produces (`backend.ts`): `type Unsubscribe = () => void`; `interface AuthPort { onUserChanged(cb: (u: SessionUser | null) => void): Unsubscribe; signIn(): Promise<void>; signOut(): Promise<void> }`; `interface Backend { ensureUser(u: SessionUser): Promise<void>; watchUser(uid: string, cb: (r: UserRecord | null) => void): Unsubscribe; watchLive(cb: (l: LiveState | null) => void): Unsubscribe; watchMyRequests(uid: string, cb: (list: WithId<RequestRecord>[]) => void): Unsubscribe; watchUsers(cb: (list: WithId<UserRecord>[]) => void): Unsubscribe; watchConnected(cb: (online: boolean) => void): Unsubscribe; serverNow(): number; send(input: SendInput): Promise<string>; cancel(id: string): Promise<void>; setRole(uid: string, role: Role): Promise<void>; setBlocked(uid: string, blocked: boolean): Promise<void> }` (em `watchUsers`, `id` = uid).

- [ ] **Step 1: Escrever os testes que falham**
  - `writes.test.ts` — `buildUserRecord({ uid: 'u1', name: 'Maria', email: 'maria@example.com' })` `toEqual` `{ name: 'Maria', email: 'maria@example.com', role: 'member', blocked: false, createdAt: serverTimestamp(), lastSeenAt: serverTimestamp() }` (`serverTimestamp` de `firebase/database`); nome vazio → usa o e-mail; nome de 100 caracteres → 80. `buildSendUpdate('r1', { uid: 'u1', name: 'Maria', raw: 'gol branco ffo4745', urgent: true, preview: { text: 'GOL BRANCO | PLACA: FFO-4745', kind: 'vehicle', plate: 'FFO-4745', by: 'local' } })` tem exatamente as chaves `requests/r1`, `users/u1/lastSentAt` (`serverTimestamp()`), `users/u1/lastRequestId` (`'r1'`), `users/u1/sentCount` (`increment(1)`), e `requests/r1` = `{ uid: 'u1', name: 'Maria', raw: 'gol branco ffo4745', urgent: true, createdAt: serverTimestamp(), cancel: false, preview: { … } }`; `preview: null` → sem a chave `preview`; preview sem placa → sem a chave `plate`.
  - `live.test.ts` — `normalizeLive(null)` → `null`; `queue` como objeto `{ 0: a, 1: b }` → `[a, b]`; sem `queue`/`recent` → `[]`; sem `bar` → `{ position: 'top', height: 56, speed: 90 }`; sem `now` → `null`. `isPcOnline` com `heartbeatAt: 1_000_000`: `1_090_000` → `true`, `1_090_001` → `false`; `null` → `false`. `plateFromText('CRETA PRETO | PLACA: GBR-9003 | FAROL ACESO')` → `'GBR-9003'`; `'AVISO LIVRE'` → `null`. `findDuplicate('FFO-4745', …)`: na fila → `{ where: 'queue', label: 'GOL BRANCO' }`; no `now` só pelo texto → `{ where: 'now', label: 'CRETA PRETO' }`; `recent` exibido há 9 min → `{ where: 'recent', label: …, minutesAgo: 9 }`; há 11 min → `null`; `recent` recusado há 1 min → `null`; placa `null` → `null`. `headerPill`: desconectado → `{ tone: 'bad', label: 'Sem conexão' }`; heartbeat de 91 s → `{ 'bad', 'PC offline' }`; `paused` → `{ 'warn', 'Barra pausada' }`; `led: false` → `{ 'bad', 'Telão desligado' }`; tudo certo → `{ 'ok', 'Telão ligado' }` (prioridade nessa ordem). `rateLimitLeft`: membro com `lastSentAt` há 5 s → `15`; há 25 s → `0`; admin → `0`; registro `null` → `0`.
  - `requestView.test.ts` — sem `status` → `{ tone: 'q', icon: 'clock', label: 'enviado', canCancel: true }`; `queued` posição 2 → `'2º na fila'` (com `live.led === false` ou sem `position` → `'na fila'`); `showing` → `{ 'now', 'monitor-play', 'passando', canCancel: false }`; `shown` → `{ 'ok', 'check', formatHm(at) }`; `rejected` com `reason: 'faltou placa'` → `{ 'no', 'x', 'faltou placa' }` (sem `reason` → `'recusado'`); `cancelled` com `reason: 'pregação'` → `{ 'cx', 'ban', 'cancelado: pregação', struck: true }` (sem `reason` → `'cancelado'`); `expired` → `{ 'cx', 'hourglass', 'expirou' }`; `cancel: true` ainda pendente → `label 'cancelando…'`, `canCancel: false`. `requestText` segue a precedência das Interfaces.
  - `time.test.ts` — `formatHm(new Date(2026, 9, 19, 19, 4).getTime())` → `'19:04'`; `formatDayTime(new Date(2026, 9, 19, 9, 5).getTime())` → `'19/10 09:05'`.
- [ ] **Step 2: Rodar** `npm test -- src/data` → FAIL.
- [ ] **Step 3: Implementar** as funções com as assinaturas acima (`minutesAgo = Math.max(1, Math.round((serverNow - at) / 60000))`; nome do pedido e do usuário cortado em 80).
- [ ] **Step 4: Rodar** `npm test -- src/data` → PASS.
- [ ] **Step 5: Commit** `feat(site): contrato do banco, escritas e estado derivado do /live`

### Task 4: `database.rules.json` com testes no emulador

**Files:**
- Create: `database.rules.json`, `firebase.json`, `.firebaserc` (na raiz do repositório), `web/tests/emulator/rules.test.ts`, `web/tests/support/rulesEnv.ts`
- Modify: `web/package.json` (devDependencies `@firebase/rules-unit-testing`, `firebase-tools` exato `14.27.0`; script `test:emu`), `web/vite.config.ts` (projeto Vitest `emulator`)

**Interfaces:**
- Consumes: `buildUserRecord`, `buildSendUpdate` (Task 3).
- Produces: `firebase.json` com `database.rules = "database.rules.json"` e `emulators` = `{ auth: { host: "127.0.0.1", port: 9099 }, database: { host: "127.0.0.1", port: 9000 }, ui: { enabled: false }, singleProjectMode: true }`; `.firebaserc` provisório `{ "projects": { "default": "alertaibt" } }`; script `test:emu` = `firebase emulators:exec --only auth,database --project demo-alerta-ibt "vitest run --project emulator"`; projeto Vitest `emulator` (node, `tests/emulator/**/*.test.ts`, `fileParallelism: false`, `testTimeout: 20000`).

Desenho das regras (sem funções no RTDB: repetir as expressões). Com `U = root.child('users').child(auth.uid)`: **ativo** = `U.exists() && U.child('blocked').val() !== true`; **admin** = `U.child('role').val() === 'admin' && U.child('blocked').val() !== true`; **PC** = `root.child('devices').child(auth.uid).val() === true`. Lembrar: `.write` concedido num nó vale para tudo abaixo, e `.validate` não roda em exclusão.

| Nó | `.read` | `.write` | `.validate` |
|---|---|---|---|
| `users` | admin ou PC | — | — |
| `users/$uid` | o próprio | só criação: o próprio, `!data.exists()`, `auth.token.firebase.sign_in_provider === 'google.com'` | tem `name, email, role, blocked, createdAt`; `$other` false |
| `…/name`, `lastSeenAt`, `lastSentAt`, `lastRequestId`, `sentCount` | — | o próprio, ativo, registro existe, `newData.exists()` | name 1–80; `lastSeenAt`/`lastSentAt` `=== now`; `lastRequestId` string ≤ 40; `sentCount === (antigo ou 0) + 1` |
| `…/email`, `createdAt` | — | — (só na criação) | email `=== auth.token.email`; createdAt `=== now` e não existia |
| `…/role`, `blocked` | — | admin, `auth.uid !== $uid`, existe, `newData.exists()` | `member`/`admin` e boolean; na criação só `member` e `false` |
| `requests` | PC, admin, ou ativo com `query.orderByChild === 'uid' && query.equalTo === auth.uid`; `.indexOn: ["uid", "createdAt"]` | — | — |
| `requests/$id` | o dono (ativo) | criação: ativo, `uid === auth.uid`, `createdAt === now`, no novo estado `users/{auth.uid}/lastSentAt === now` e `lastRequestId === $id`, e (admin ou `lastSentAt` antigo ausente ou `<= now - 20000`); ou PC apagando (`data.exists() && !newData.exists()`) | tem `uid, name, raw, urgent, createdAt, cancel`; `name` 1–80; `raw` 1–300; `urgent` boolean; `preview` = `{ text ≤ 400, kind vehicle/free, plate? /^[A-Z]{3}-[0-9][A-Z0-9][0-9]{2}$/, by gemini/local }`; `$other` false |
| `…/cancel` | — | `newData.val() === true` e: dono ativo com `data.val() === false` e `status` ausente, `pending` ou `queued`; ou admin | boolean; na criação `false` |
| `…/status` | — | PC | PC (bloqueia `status` na criação); tem `state, at`; `state` na lista da 9.2; `position` ≥ 1; `reason` ≤ 200; `text` ≤ 400; `$other` false |
| `live` | ativo ou PC | PC | — |
| `devices/$uid` | o próprio | — (CLI grava na instalação) | — |

- [ ] **Step 1: Escrever os testes que falham** — `rulesEnv.ts`: `initializeTestEnvironment({ projectId: 'demo-alerta-ibt', database: { rules: <database.rules.json lido do disco> } })`; `asUser(uid, { email = `${uid}@example.com`, provider = 'google.com' } = {})` → `authenticatedContext(uid, { email, firebase: { sign_in_provider: provider } }).database()`; `seed(path, value)` com `withSecurityRulesDisabled`; `beforeEach`: `clearDatabase()` e semente `alice` (member), `bob` (admin), `carol` (member, `blocked: true`), `devices/pc = true`, todos com registro completo. `rules.test.ts`, com `assertSucceeds` (✓) e `assertFails` (✗):
  - **users:** dave cria o próprio com `buildUserRecord` ✓; com `role: 'admin'` ✗; com `blocked: true` ✗; registro de outro uid ✗; e-mail diferente do token ✗; `provider: 'password'` ✗; campo extra ✗. Alice atualiza `name` + `lastSeenAt: serverTimestamp()` ✓; muda o próprio `role` ✗, `blocked` ✗, `createdAt` ✗, `email` ✗; apaga o próprio registro ✗; `lastSentAt: null` ✗; `sentCount: 100` ✗. Carol (bloqueada) atualiza `lastSeenAt` ✗. Bob promove, rebaixa, bloqueia e desbloqueia Alice ✓; Bob muda o próprio `role`/`blocked` ✗; Alice muda o `role` de Dave ✗; admin bloqueado promove ✗. Leitura: Bob lê `/users` ✓; PC lê `/users` ✓; Alice lê `/users` ✗; Alice lê o próprio ✓, o de Bob ✗; Carol lê o próprio ✓.
  - **requests:** Alice envia com `buildSendUpdate` ✓; segundo envio logo depois ✗; com `lastSentAt` semeado 21 s atrás ✓; Bob envia duas vezes seguidas ✓; `requests/x` sem `lastSentAt` no mesmo envio ✗; dois pedidos num envio só ✗; `raw` com 300 caracteres ✓, 301 ✗, vazio ✗; `uid` de outra pessoa ✗; Carol ✗; usuário sem registro ✗; com `status` na criação ✗; `cancel: true` na criação ✗; `createdAt` numérico do cliente ✗; campo extra ✗; `preview.kind: 'reject'` ✗; `preview.plate: 'ABC1234'` ✗. Cancelar: Alice cancela o próprio sem `status` ✓, `queued` ✓, `showing` ✗; volta `cancel` para `false` ✗; Dave cancela o de Alice ✗; Bob cancela o de Alice em `showing` ✓. `status`: Alice escreve ✗; PC escreve `{ state: 'queued', position: 1, at: Date.now() }` ✓; PC com `state: 'done'` ✗. Apagar: PC ✓; Alice apaga o próprio ✗. Alice muda `raw` depois de criado ✗. Leitura: Alice com `orderByChild('uid').equalTo('alice')` ✓; Alice lê `/requests` sem consulta ✗; Alice consulta `equalTo('bob')` ✗; Bob lê `/requests` ✓; PC lê ✓; Carol consulta os próprios ✗.
  - **live:** Alice lê ✓; Carol ✗; sem login ✗; usuário sem registro ✗; Alice escreve ✗; Bob escreve ✗; PC escreve ✓.
  - **devices:** PC lê `devices/pc` ✓; Alice lê `devices/pc` ✗; PC escreve em `devices/pc` ✗.
- [ ] **Step 2: Rodar** `npm run test:emu` → FAIL (regras ainda não existem; o emulador sobe com Java 17). Se o emulador não subir, parar e reportar o erro exato.
- [ ] **Step 3: Implementar** `database.rules.json` conforme a tabela, `firebase.json`, `.firebaserc`.
- [ ] **Step 4: Rodar** `npm run test:emu` → PASS (todos). `npm test` continua PASS.
- [ ] **Step 5: Commit** `feat(firebase): regras do Realtime Database com testes no emulador`

### Task 5: Correção por IA atrás de uma interface, com trava e reserva local

**Files:**
- Create: `web/src/correction/{types.ts, guard.ts, correct.ts, prompt.ts, firebaseAi.ts}`
- Test: `web/src/correction/{guard.test.ts, correct.test.ts, prompt.test.ts}`

**Interfaces:**
- Consumes: `parseAlert`, `normalizePlate`, `extractPlate`, `ParsedAlert`.
- Produces: `interface AiAlert { kind: 'vehicle' | 'free'; modelo: string; cor: string; placa: string; motivo: string; texto: string }` (mesmo schema do Gemini do PC); `interface AiCorrector { correct(raw: string, signal: AbortSignal): Promise<AiAlert | null> }` (`null` = não deu); `type CorrectedBy = 'gemini' | 'local'`; `interface Correction { alert: ParsedAlert; by: CorrectedBy; refused: boolean }`; `guardAiResult(raw: string, ai: AiAlert | null, local: ParsedAlert): Correction`; `editDistance(a: string, b: string): number`; `AI_TIMEOUT_MS = 3000`; `correctAlert(raw: string, corrector: AiCorrector | null, timeoutMs?: number): Promise<Correction>`; `SYSTEM_PROMPT: string`; `parseAiJson(text: string): AiAlert | null`; `createFirebaseAiCorrector(app: FirebaseApp, opts: { model: string; recaptchaSiteKey: string }): AiCorrector` (em `firebaseAi.ts`, carregado com `import()` só quando há chave do App Check).

- [ ] **Step 1: Escrever os testes que falham**
  - `guard.test.ts` — raw `'prata corolla bra2e19 vidro abrto'` + IA `{ kind: 'vehicle', modelo: 'Corolla', cor: 'prata', placa: 'BRA2E19', motivo: 'vidro aberto', texto: '' }` → `by: 'gemini'`, `alert.model 'Corolla'`, `alert.reason 'vidro aberto'`, `alert.plate 'BRA-2E19'`; IA com `placa: 'ABC9999'` para raw `'gol branco abc1234'` → `by: 'local'`; IA sem placa quando o raw tem → `'local'`; IA com `modelo: 'Corolla'` para raw `'gol branco abc1234 farol aceso'` → `'local'` (palavra inventada); IA `free` com `texto: 'Pais do Pedro Henrique, da sala kids, favor comparecer à recepção'` para raw `'pais do pedro henrique da sala kids favor comparecer na recepssao'` → `'gemini'` e `alert.freeText` igual ao texto da IA; IA `free` com tudo vazio → `refused: true`, `by: 'gemini'`; IA `null` → `'local'`. `editDistance('recepssao', 'recepcao')` → `2`.
  - `correct.test.ts` (timers falsos) — corrector `null` → `'local'` sem esperar; corrector que lança → `'local'`; corrector que não responde → depois de 3000 ms `'local'` e o `signal` recebido está `aborted`; corrector ok → `'gemini'`.
  - `prompt.test.ts` — `parseAiJson` de JSON válido → objeto; JSON inválido → `null`; `kind: 'other'` → `null`; campo faltando → `''`; array ou número → `null`.
- [ ] **Step 2: Rodar** `npm test -- src/correction` → FAIL.
- [ ] **Step 3: Implementar** — trava (spec 5.3): placa da IA normalizada tem de ser igual à de `extractPlate(raw)` (as duas ausentes também vale); toda palavra da IA (`modelo`, `cor`, `motivo`, `texto`, em minúsculas, sem acento, sem pontuação e sem a placa) precisa estar a distância de edição ≤ 2 de alguma palavra do raw; falhou → `local`. IA `vehicle` → `{ model: modelo, color: cor, plate: local.plate, reason: motivo, freeText: texto || local.freeText }`; IA `free` → `{ model: '', color: '', plate: local.plate, reason: '', freeText: texto }`. `correctAlert`: `parseAlert` local sempre; corrida com o tempo máximo; ao estourar, `abort()`. `SYSTEM_PROMPT` em pt-BR: corrigir ortografia, acentos e concordância de avisos curtos de estacionamento sem mudar o sentido; extrair `modelo`, `cor`, `placa` (exatamente como veio) e `motivo` com `kind: "vehicle"`; aviso que não é de veículo → `kind: "free"` com `texto` corrigido; não inventar nada; texto ofensivo ou que não é aviso → `kind: "free"` com `texto` vazio. `firebaseAi.ts`: `initializeAppCheck(app, { provider: new ReCaptchaV3Provider(siteKey), isTokenAutoRefreshEnabled: true })` (em `import.meta.env.DEV` ligar `self.FIREBASE_APPCHECK_DEBUG_TOKEN = true`), `getAI(app, { backend: new GoogleAIBackend(), useLimitedUseAppCheckTokens: true })` (tirar a opção se a versão instalada não tiver), `getGenerativeModel(ai, { model, systemInstruction: SYSTEM_PROMPT, generationConfig: { responseMimeType: 'application/json', responseSchema: Schema.object({ properties: { kind: Schema.enumString({ enum: ['vehicle', 'free'] }), modelo: Schema.string(), cor: Schema.string(), placa: Schema.string(), motivo: Schema.string(), texto: Schema.string() } }), temperature: 0 } }, { timeout: AI_TIMEOUT_MS })`; `correct` → `parseAiJson(result.response.text())`, erro → `null`. Conferir os nomes nos tipos de `node_modules/firebase/ai`.
- [ ] **Step 4: Rodar** `npm test -- src/correction` → PASS; `npm run build` → sem erros de tipo.
- [ ] **Step 5: Commit** `feat(site): correção com IA (AI Logic + App Check), trava e reserva local`

### Task 6: Sessão, entrar, bloqueado, tutorial e casca com abas

**Files:**
- Create: `web/src/test/fakes.ts`, `web/src/session/{SessionProvider.tsx, authErrors.ts}`, `web/src/state/AppData.tsx`, `web/src/ui/{Root.tsx, Login.tsx, Blocked.tsx, Tutorial.tsx, Shell.tsx, Avatar.tsx, Pill.tsx, useToast.ts}`, `web/src/styles/app.css`, placeholders `web/src/send/SendTab.tsx`, `web/src/status/StatusTab.tsx`, `web/src/team/TeamTab.tsx`
- Test: `web/src/ui/{Root.test.tsx, Tutorial.test.tsx, Shell.test.tsx}`, `web/src/session/authErrors.test.ts`

**Interfaces:**
- Consumes: `AuthPort`, `Backend`, `SessionUser`, `UserRecord`, `LiveState`, `headerPill`, `AiCorrector`.
- Produces: `interface RootProps { auth: AuthPort; backend: Backend; corrector: Promise<AiCorrector | null> }`, `Root(props: RootProps)`; `type SessionState = { status: 'loading' } | { status: 'signedOut' } | { status: 'blocked'; user: SessionUser } | { status: 'active'; user: SessionUser; record: UserRecord; role: Role }`; `useSession(): SessionState & { signIn(): Promise<void>; signOut(): Promise<void> }`; `authErrorMessage(e: unknown): string`; `useBackend(): Backend`, `useCorrector(): AiCorrector | null`, `useLive(): LiveState | null`, `useConnected(): boolean`, `useServerNow(tickMs?: number): number` (de `state/AppData.tsx`, montado só com sessão ativa); `TUTORIAL_KEY = 'alertaibt.tutorial.v1'`; `useToast(): { message: string; show(msg: string, ms?: number): void }` (some depois de 1800 ms) e `<Toast message />`; `FakeBackend implements Backend` e `FakeAuth implements AuthPort` (em `test/fakes.ts`, sem importar `vitest`; guardam chamadas em arrays, têm `emitUser`, `emitLive`, `emitRequests`, `emitUsers`, `setConnected`, `now`, e `sendImpl` substituível). As abas Status e Equipe entram com `React.lazy`; `SendTab`, `StatusTab`, `TeamTab` são placeholders que as Tasks 7–11 substituem.

- [ ] **Step 1: Escrever os testes que falham**
  - `Root.test.tsx` — sem login → botão `Entrar com Google`; clicar chama `auth.signIn`; `signIn` rejeitado com `{ code: 'auth/popup-closed-by-user' }` → texto `Login cancelado.`. Login feito → `backend.ensureUser` chamado com o usuário; registro com `blocked: true` → `Seu acesso foi removido` e botão `Sair`; bloqueado no meio do uso (`emitUser` com `blocked: true`) → troca para essa tela.
  - `Tutorial.test.tsx` — primeiro acesso (localStorage vazio) mostra `Como mandar um aviso`; `Próximo` → `O que não pode faltar`; `Próximo` → `Acompanhe e urgente` com botão `Começar`; `Começar` → abas e `localStorage[TUTORIAL_KEY] === '1'`; `Pular` no passo 1 também conclui; com a chave gravada o tutorial não aparece; localStorage que lança erro não quebra (tutorial aparece e some ao concluir).
  - `Shell.test.tsx` — membro: abas `Enviar` e `Status`, sem `Equipe`; título `Enviar` na aba Enviar e `Agora` na Status; avatar com a inicial do nome abre menu com `Sair` → `auth.signOut`. Admin: aba `Equipe` e avatar com o ponto amarelo (classe `adm`); promovido no meio do uso → `Equipe` aparece. Pílula do cabeçalho com os 5 casos de `headerPill` (`Sem conexão`, `PC offline`, `Barra pausada`, `Telão desligado`, `Telão ligado`).
  - `authErrors.test.ts` — `auth/popup-closed-by-user` e `auth/cancelled-popup-request` → `Login cancelado.`; `auth/network-request-failed` → `Sem conexão com a internet.`; `auth/unauthorized-domain` → `Este endereço não está autorizado para login com Google.`; `auth/too-many-requests` → `Muitas tentativas. Espere alguns minutos e tente de novo.`; desconhecido `auth/x` → `Não deu certo. Tente de novo. (auth/x)`.
- [ ] **Step 2: Rodar** `npm test -- src/ui src/session` → FAIL.
- [ ] **Step 3: Implementar** — `Root` decide: carregando (logo), `Login` (logo `assets/icons/logo.svg`, `Alerta IBT`, `Avisos para o telão`, botão `Entrar com Google`), `Blocked`, `Tutorial` (textos e ilustrações dos 3 passos de `STEPS` do mockup, botões `Pular`/`Próximo`/`Começar`, pontos de progresso) ou `Shell`. Sessão: `onUserChanged` → `ensureUser` (erro é ignorado) → `watchUser`. `Shell`: cabeçalho (título, pílula com bolinha da cor do `tone`, avatar) e barra de abas com ícones Lucide `Send`, `Activity`, `Users`; para membro, `Sair` fica no menu do avatar; para admin, tocar no avatar abre a aba Equipe (o `Sair` dele fica lá, Task 11). `app.css` com os componentes do mockup (`.ab .avatar .ct .tb .pill .dot .cd .lb .pre .tag .sendrow .send .urg .toast .msg .src .it .st .xb .list .info .led .tut`) em `rem`, cores só por token.
- [ ] **Step 4: Rodar** `npm test` → PASS.
- [ ] **Step 5: Commit** `feat(site): sessão com Google, bloqueio, tutorial e abas`

### Task 7: Enviar — campo e prévia ao vivo

**Files:**
- Create: `web/src/send/{evaluate.ts, usePreview.ts, PreviewBox.tsx}`
- Modify: `web/src/send/SendTab.tsx`
- Test: `web/src/send/{evaluate.test.ts, usePreview.test.tsx, SendTab.preview.test.tsx}`

**Interfaces:**
- Consumes: `correctAlert`, `Correction`, `CorrectedBy`, `decide`, `alertText`, `missingFields`, `fieldValue`, `findDuplicate`, `Duplicate`, `useSession`, `useLive`, `useServerNow`, `useCorrector`.
- Produces: `interface PreviewResult { text: string; kind: RuleKind; missing: Field[]; plate: string | null; by: CorrectedBy; refused: boolean; duplicate: Duplicate | null; canSend: boolean }`; `evaluatePreview(c: Correction, role: Role, live: LiveState | null, serverNow: number): PreviewResult` (`canSend` = não recusado e: admin → `kind !== 'reject'` e texto não vazio; membro → `kind === 'vehicle'` e sem repetido); `usePreview(raw: string, role: Role, corrector: AiCorrector | null, opts?: { debounceMs?: number; timeoutMs?: number }): { status: 'empty' | 'busy' | 'ready'; result: PreviewResult | null }` (lê `live`/hora do servidor do contexto; com IA: `busy` até a resposta, espera de 700 ms; sem IA: `ready` na hora com o parser local; resposta velha é descartada); `SendTab` expõe `raw`/`setRaw` e o `PreviewResult` atual para a Task 8.

- [ ] **Step 1: Escrever os testes que falham**
  - `evaluate.test.ts` — membro com tudo → `canSend: true`; membro sem placa → `kind: 'reject'`, `canSend: false`; membro com a placa na fila → `duplicate.where === 'queue'`, `canSend: false`; admin com repetido → `canSend: true` e `duplicate` preenchido; admin sem placa → `kind: 'free'`, `canSend: true`; recusado → `canSend: false`.
  - `usePreview.test.tsx` (timers falsos, corrector falso com promessas controladas) — sem corrector: `ready` logo depois de digitar; com corrector: `busy`, chamada só depois de 700 ms parado, `ready` com `by: 'gemini'`; Review Focus 5: digita `gol branco ffo4745`, 700 ms (chamada 1 pendente), digita ` farol aceso`, 700 ms (chamada 2 pendente), resolve a 2 e depois a 1 → o resultado continua o da 2.
  - `SendTab.preview.test.tsx` — sem corrector: membro digita `gol branco farol aceso` → `Não vai para o telão:` e `FALTA PLACA`; `prata corolla bra2e19 vidro aberto` → `Vai aparecer assim:` e `COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO`; placa na fila de `/live` → `Não precisa mandar:` e `ESSE CARRO JÁ ESTÁ NA FILA (GOL BRANCO)`; admin digita `pais do pedro chegou` → selo `aviso livre`; Review Focus 3: colar `gol branco\nffo4745\n\nfarol aceso 🚗` deixa o campo numa linha e a prévia `GOL BRANCO | PLACA: FFO-4745 | FAROL ACESO 🚗`; colar 400 caracteres deixa 300 no campo.
- [ ] **Step 2: Rodar** `npm test -- src/send` → FAIL.
- [ ] **Step 3: Implementar** — campo `textarea` de 3 linhas, `maxLength={300}`, `onChange` com `fieldValue`, Enter não quebra linha; rótulo `Novo aviso` com (i) e balão (membro: `Precisa de modelo, cor e placa.\nEx.: corolla prata abc1d23 vidro aberto\nSegure o ícone amarelo 1 s para urgente.`; admin: `Admin pode mandar qualquer texto.\nSem placa: vai como aviso livre, corrigido.`); placeholder membro `Ex.: corolla prata abc1d23 vidro aberto`, admin `Qualquer aviso…`. `PreviewBox` (com `aria-live="polite"`): vazio → ícone `WandSparkles`, `Vai aparecer assim:` + `…`; `busy` → `LoaderCircle` girando + `Corrigindo com IA…`; veículo → `Vai aparecer assim:` + texto; livre → `Megaphone` + `Vai aparecer assim` + selo `aviso livre` + texto; recusa de regra → `CircleAlert` + `Não vai para o telão:` + `FALTA {CAMPOS EM MAIÚSCULAS, separados por vírgula}`; repetido (membro) → `CopyX` + `Não precisa mandar:` + `ESSE CARRO JÁ ESTÁ NA FILA ({label})` / `ESSE CARRO ESTÁ PASSANDO NO TELÃO ({label})` / `ESSE CARRO JÁ PASSOU HÁ {n} MIN ({label})`; repetido (admin) → mesmo texto com `Atenção, pode mandar mesmo assim:`; recusado pela IA → `Não vai para o telão:` + `TEXTO NÃO ACEITO`. Classes `pre`, `pre busy`, `pre err`, `pre dup`, `pre free` do mockup.
- [ ] **Step 4: Rodar** `npm test` → PASS.
- [ ] **Step 5: Commit** `feat(site): campo do aviso com prévia ao vivo por IA e regra`

### Task 8: Enviar — Enviar, Urgente (segurar 1 s) e microfone

**Files:**
- Create: `web/src/send/{sendFlow.ts, useHold.ts, UrgentButton.tsx, useSpeech.ts, MicButton.tsx, SendRow.tsx}`
- Modify: `web/src/send/SendTab.tsx`
- Test: `web/src/send/{sendFlow.test.ts, UrgentButton.test.tsx, MicButton.test.tsx, SendTab.send.test.tsx}`

**Interfaces:**
- Consumes: `PreviewResult`, `cleanRaw`, `rateLimitLeft`, `Backend.send`, `useConnected`, `useToast`.
- Produces: `type SendOutcome = { ok: true; id: string } | { ok: false; message: string }`; `sendAlert(backend: Backend, input: SendInput, opts: { connected: boolean; timeoutMs?: number }): Promise<SendOutcome>` (padrão 10 000 ms); `useHold(onComplete: () => void, opts: { ms: number; enabled: boolean; onShortTap: () => void }): { holding: boolean; bind: { onPointerDown; onPointerUp; onPointerLeave; onPointerCancel; onKeyDown; onKeyUp } }`; `getSpeechRecognition(win?: Window): SpeechRecognitionCtor | null` (`SpeechRecognition` ou `webkitSpeechRecognition`; tipos mínimos declarados no próprio arquivo); `useSpeech(onText: (t: string) => void): { supported: boolean; listening: boolean; start(): void }`.

- [ ] **Step 1: Escrever os testes que falham**
  - `sendFlow.test.ts` — `connected: false` → `{ ok: false, message: 'Sem conexão. Confira a internet e tente de novo.' }` e `send` **não** é chamado (Review Focus 1); `send` que nunca resolve → depois de 10 s `Não deu para enviar. Confira a internet e tente de novo.`; `send` rejeitado com erro de permissão (`code` ou `message` contendo `permission_denied`, sem diferenciar maiúsculas; testar `{ code: 'PERMISSION_DENIED' }` e `new Error('permission_denied')`) → `Espere alguns segundos para mandar outro aviso.`; outro erro → `Não deu para enviar. Tente de novo.`; sucesso → `{ ok: true, id }`.
  - `UrgentButton.test.tsx` (timers falsos, `navigator.vibrate` falso) — `pointerDown`, 999 ms, `pointerUp` → nada enviado e toast `Segure o ícone 1 s para urgente`; `pointerDown`, 1000 ms → `vibrate(60)` e envio com `urgent: true`; desabilitado → `pointerDown` não faz nada; segurar Enter/Espaço 1 s também envia.
  - `MicButton.test.tsx` — sem `SpeechRecognition` na `window` → sem botão `Ditar`; com uma classe falsa → clicar começa com `lang 'pt-BR'`, botão com classe `rec` e toast `Ouvindo…`; resultado `corolla preto qwe2f45` com campo vazio → campo `corolla preto qwe2f45`; com texto já digitado → acrescenta depois de um espaço; `onend` tira a classe `rec`.
  - `SendTab.send.test.tsx` (`FakeBackend`) — com prévia ok, clicar `Enviar` → `send` com `{ uid, name, raw: cleanRaw(campo), urgent: false, preview: { text, kind, plate, by } }`, campo limpo e toast `Enviado`; dois cliques seguidos → `send` uma vez só (Review Focus 4); depois do urgente segurado → toast `Enviado como URGENTE`; `/live.paused` → `Enviar` e Urgente desabilitados e `⏸️ Pregação em andamento: o telão não está mostrando avisos.`; membro com `lastSentAt` há 5 s → `Aguarde 15 s para mandar outro aviso.` e `Enviar` desabilitado; admin nas mesmas condições não espera; Enter no campo com prévia ok envia.
- [ ] **Step 2: Rodar** `npm test -- src/send` → FAIL.
- [ ] **Step 3: Implementar** — linha `[Enviar][Urgente]` do mockup (`.sendrow`, `.send` com ícone `Send`, `.urg` com ícone `Siren`, enchimento de 1 s pela classe `holding`, `aria-label="Segure 1 s para urgente"`, `aria-disabled` e classe `dis` quando não pode); microfone `.mic` pequeno e apagado dentro do campo (`Mic`, `aria-label="Ditar"`), escondido sem suporte; trava de envio em andamento; `.toast` abaixo da linha.
- [ ] **Step 4: Rodar** `npm test` → PASS.
- [ ] **Step 5: Commit** `feat(site): enviar, urgente segurando 1 s e ditado discreto`

### Task 9: "Seus avisos"

**Files:**
- Create: `web/src/send/MyAlerts.tsx`
- Modify: `web/src/send/SendTab.tsx`
- Test: `web/src/send/MyAlerts.test.tsx`

**Interfaces:**
- Consumes: `Backend.watchMyRequests`, `Backend.cancel`, `requestBadge`, `requestText`, `useLive`, `useToast`.
- Produces: `MyAlerts({ uid }: { uid: string })` — os 10 mais recentes (`createdAt` decrescente), rótulo `Seus avisos`, vazio → `Nenhum aviso ainda.`.

- [ ] **Step 1: Escrever o teste que falha** — com `emitRequests`: `queued` posição 2 → `2º na fila`; `showing` → `passando`; `shown` → hora `formatHm(at)`; `rejected` `faltou placa` → `faltou placa`; `cancelled` `pregação` → `cancelado: pregação` com texto riscado (classe `cx`); `expired` → `expirou`; sem `status` → `enviado`; botão `Cancelar aviso` (`title="Cancelar (o carro já saiu)"`) só em pendente e `queued`; clicar → `backend.cancel('id')`; `cancel` rejeitado → toast `Não deu para cancelar. Tente de novo.`; ordem do mais novo para o mais velho; 12 pedidos → mostra 10.
- [ ] **Step 2: Rodar** `npm test -- MyAlerts` → FAIL.
- [ ] **Step 3: Implementar** com os itens `.it`, pílulas `.st q|now|ok|no|cx` e ícones Lucide `Clock`, `MonitorPlay`, `Check`, `X`, `Ban`, `Hourglass`.
- [ ] **Step 4: Rodar** `npm test` → PASS.
- [ ] **Step 5: Commit** `feat(site): "Seus avisos" com estados e cancelamento`

### Task 10: Aba Status com mini-telão

**Files:**
- Create: `web/src/status/{ticker.ts, MiniLed.tsx}`
- Modify: `web/src/status/StatusTab.tsx`
- Test: `web/src/status/{ticker.test.ts, StatusTab.test.tsx}`

**Interfaces:**
- Consumes: `useLive`, `useServerNow`, `useSession`, `useBackend`, `isPcOnline`, `formatHm`, `LiveBar`.
- Produces: `LED_WIDTH = 2560`; `tickerX(elapsedMs: number, speed: number, textWidth: number, screenWidth?: number): number` (posição do texto em px do LED, em laço: `screenWidth - ((speed * elapsedMs / 1000) % (screenWidth + textWidth))`); `barMetrics(bar: LiveBar): { height: number; fontSize: number; top: boolean }` (`fontSize` ausente = `Math.round(height * 0.64)`); `MiniLed({ text, bar, startedAt }: { text: string | null; bar: LiveBar; startedAt?: number })` (proporção 2560/768, unidades `cqw`, `requestAnimationFrame` com o tempo do quadro, decorrido a partir de `startedAt` na hora do servidor, sem texto = sem barra; `prefers-reduced-motion: reduce` → texto parado).

- [ ] **Step 1: Escrever os testes que falham**
  - `ticker.test.ts` — `tickerX(0, 90, 1000)` → `2560`; `tickerX(10_000, 90, 1000)` → `1660`; `tickerX(ms de 3560 px, 90, 1000)` volta a `2560`. `barMetrics({ position: 'top', height: 56, speed: 90 })` → `{ height: 56, fontSize: 36, top: true }`; com `fontSize: 40` → `40`; `position: 'bottom'` → `top: false`.
  - `StatusTab.test.tsx` — `/live` com `now` (`CRETA PRETO | PLACA: GBR-9003 | FAROL ACESO`, `source: 'telegram'`) → título `Agora`, pílula `PC online`, `No telão agora`, selo `Telegram`, o texto; pílulas `LED ligado` e `Barra ativa` (`led: false` → `LED desligado`; `paused` → `Barra pausada`); `Fila (2)` com selos `Site`/`PC` e `1º`/`2º`; `Recentes` com a hora de um exibido; admin vê `Cancelar aviso` só no item `source: 'site'` e clicar chama `backend.cancel(id)`; membro não vê; Review Focus 2: heartbeat de 91 s → pílula `PC offline` e o texto do `now` some do card.
- [ ] **Step 2: Rodar** `npm test -- src/status` → FAIL.
- [ ] **Step 3: Implementar** conforme o mockup (card `.cd` com `.led`, `.bar`, `.tick`; fundo do mini-telão só com o gradiente do mockup, sem letra de música inventada).
- [ ] **Step 4: Rodar** `npm test` → PASS.
- [ ] **Step 5: Commit** `feat(site): aba Status com mini-telão, fila e recentes`

### Task 11: Aba Equipe (admin)

**Files:**
- Modify: `web/src/team/TeamTab.tsx`
- Test: `web/src/team/TeamTab.test.tsx`

**Interfaces:**
- Consumes: `Backend.watchUsers`, `Backend.setRole`, `Backend.setBlocked`, `useSession`, `useServerNow`, `formatDayTime`, `useToast`.
- Produces: `TeamTab()` — lista por `lastSeenAt` decrescente: inicial, nome, e-mail, `Último uso: {formatDayTime}`, `{n} avisos` (`sentCount` ou 0), selos `novo` (criado há menos de 7 dias), `admin`, `removido`, `você`; botões (nunca na própria linha) `Tornar admin`/`Tirar admin` e `Remover`/`Restaurar`; botão `Sair` no fim.

- [ ] **Step 1: Escrever o teste que falha** — 3 usuários em ordem de último uso; selo `novo` para criado há 2 dias e não para 10 dias; `Tornar admin` → `setRole(uid, 'admin')`; `Tirar admin` → `setRole(uid, 'member')`; `Remover` → `setBlocked(uid, true)`; em removido, `Restaurar` → `setBlocked(uid, false)`; a própria linha tem `você` e nenhum botão; `Sair` → `auth.signOut`; erro em `setRole` → toast `Não deu certo. Tente de novo.`.
- [ ] **Step 2: Rodar** `npm test -- src/team` → FAIL.
- [ ] **Step 3: Implementar** com os mesmos componentes visuais (`.it`, `.pill`, `.xb`, `.avatar`).
- [ ] **Step 4: Rodar** `npm test` → PASS.
- [ ] **Step 5: Commit** `feat(site): aba Equipe para admin`

### Task 12: Adaptadores do Firebase e ligação do app

**Files:**
- Create: `web/src/firebase/{config.ts, init.ts, auth.ts, backend.ts}`, `web/src/ui/ConfigMissing.tsx`, `web/src/vite-env.d.ts`, `web/.env.example`, `web/.env.emulator`, `web/tests/support/rest.ts`
- Modify: `web/src/main.tsx` (renderiza `Root` com os adaptadores)
- Delete: `web/src/App.tsx`, `web/src/App.test.tsx`
- Test: `web/src/firebase/config.test.ts`, `web/tests/emulator/backend.test.ts`

**Interfaces:**
- Consumes: `Backend`, `AuthPort`, `buildUserRecord`, `buildSendUpdate`, `normalizeLive`, `Root`, `createFirebaseAiCorrector`.
- Produces: `interface AppConfig { firebase: FirebaseOptions; useEmulators: boolean; recaptchaSiteKey?: string; aiModel: string }`; `readFirebaseConfig(env: Record<string, string | undefined>): { ok: true; config: AppConfig } | { ok: false; missing: string[] }` (obrigatórias `VITE_FIREBASE_API_KEY`, `VITE_FIREBASE_AUTH_DOMAIN`, `VITE_FIREBASE_PROJECT_ID`, `VITE_FIREBASE_DATABASE_URL`, `VITE_FIREBASE_APP_ID`; opcionais `VITE_RECAPTCHA_SITE_KEY`, `VITE_AI_MODEL` (padrão `gemini-3.5-flash-lite`), `VITE_USE_EMULATORS === 'true'`); `initFirebase(cfg: AppConfig): { app: FirebaseApp; auth: Auth; db: Database }` (emulador: `connectAuthEmulator(auth, 'http://127.0.0.1:9099', { disableWarnings: true })` e `connectDatabaseEmulator(db, '127.0.0.1', 9000)`; em host `*.web.app`/`*.firebaseapp.com`, `authDomain` = o próprio host, como no Metrônomo); `createFirebaseAuth(auth: Auth, opts: { useEmulators: boolean }): AuthPort` (Google com `prompt: 'select_account'`, `languageCode = 'pt-BR'`, popup e, se bloqueado ou no iOS instalado, redirecionamento + `getRedirectResult`; no emulador expõe `window.__alertaE2E = { signIn(email: string, name: string) }` com `signInWithCredential(auth, GoogleAuthProvider.credential(JSON.stringify({ sub: email, email, email_verified: true, name })))`); `class FirebaseBackend implements Backend` (`constructor(db: Database)`; `send` = `push` para gerar o id + `update(ref(db), buildSendUpdate(id, input))`; `watchMyRequests` = `query(ref(db, 'requests'), orderByChild('uid'), equalTo(uid))`; `serverNow` com `.info/serverTimeOffset`; `watchConnected` com `.info/connected`; `ensureUser` = `get` do próprio registro, `set(buildUserRecord)` se não existir, senão `update({ name, lastSeenAt: serverTimestamp() })` se não bloqueado, erros de corrida ignorados); `rest.ts`: `dbUrl(path)`, `restPut(path, value)`, `restGet(path)`, `restPatch(path, value)` contra `http://127.0.0.1:9000/{path}.json?ns=demo-alerta-ibt-default-rtdb` com `Authorization: Bearer owner`.

- [ ] **Step 1: Escrever os testes que falham**
  - `config.test.ts` — env vazio → `{ ok: false, missing: [as 5 obrigatórias] }`; o conteúdo de `.env.emulator` → `ok` com `useEmulators: true`, `projectId: 'demo-alerta-ibt'` e `aiModel: 'gemini-3.5-flash-lite'`; `VITE_AI_MODEL` definido → usado.
  - `tests/emulator/backend.test.ts` (Auth + RTDB do emulador, dois apps nomeados) — Alice entra com credencial Google falsa; `ensureUser` cria registro `member`; `send` cria o pedido e `watchMyRequests` devolve 1 item com o `raw`; segundo `send` logo em seguida rejeita (`PERMISSION_DENIED`); `cancel(id)` grava `cancel: true`; Bob entra, `ensureUser`, vira admin por `restPatch('users/{bobUid}', { role: 'admin' })`, `setRole(aliceUid, 'admin')` funciona; `restPut('live', …)` com `queue` como objeto → `watchLive` entrega `queue` como array; `serverNow()` fica a menos de 5 s de `Date.now()`.
- [ ] **Step 2: Rodar** `npm test -- src/firebase` e `npm run test:emu` → FAIL.
- [ ] **Step 3: Implementar** — `main.tsx`: `readFirebaseConfig(import.meta.env)`; sem configuração → `ConfigMissing` (`Configuração do Firebase ausente:` + lista de variáveis); com → `initFirebase`, `FirebaseBackend`, `createFirebaseAuth` e `corrector` = `import('./correction/firebaseAi')` só com `recaptchaSiteKey` e sem emulador (senão `Promise.resolve(null)`). `.env.emulator` (versionado): `VITE_USE_EMULATORS=true`, `VITE_FIREBASE_API_KEY=demo-key`, `VITE_FIREBASE_AUTH_DOMAIN=demo-alerta-ibt.firebaseapp.com`, `VITE_FIREBASE_PROJECT_ID=demo-alerta-ibt`, `VITE_FIREBASE_DATABASE_URL=https://demo-alerta-ibt-default-rtdb.firebaseio.com`, `VITE_FIREBASE_APP_ID=1:000000000000:web:0000000000000000`. `.env.example`: as mesmas chaves vazias + `VITE_RECAPTCHA_SITE_KEY=` e `VITE_AI_MODEL=gemini-3.5-flash-lite`, com comentário de que o dono preenche `web/.env.production` depois de criar o projeto. Scripts novos: `dev:emu` = `vite --mode emulator`, `emulators` = `firebase emulators:start --only auth,database --project demo-alerta-ibt`.
- [ ] **Step 4: Rodar** `npm test`, `npm run test:emu` e `npm run build` → PASS / sem erros.
- [ ] **Step 5: Commit** `feat(site): adaptadores do Firebase (Auth, RTDB, IA) e ligação do app`

### Task 13: PWA, Hosting, divisão de código e teste de ponta a ponta

**Files:**
- Create: `web/scripts/make-icons.mjs`, `web/public/{favicon.svg, icon-192.png, icon-512.png, icon-maskable-512.png, apple-touch-icon.png}`, `web/playwright.config.ts`, `web/tests/e2e/send-status.spec.ts`
- Modify: `web/vite.config.ts` (`VitePWA`), `web/index.html` (ícones, `apple-mobile-web-app-*`), `web/package.json` (devDependencies `sharp ^0.35.5`, `@playwright/test ^1.64.1`; scripts `icons`, `test:e2e`), `firebase.json` (`hosting` e `auth`)

**Interfaces:**
- Consumes: `window.__alertaE2E`, `rest.ts`, toda a UI.
- Produces: `firebase.json` final = Task 4 + `auth.providers` = `{ emailPassword: true, googleSignIn: { oAuthBrandDisplayName: "Alerta IBT", supportEmail: "gutnikandrey01@protonmail.com", authorizedRedirectUris: ["https://alertaibt.web.app/__/auth/handler", "https://alertaibt.firebaseapp.com/__/auth/handler"] } }` + `hosting` = `{ site: "alertaibt", public: "web/dist", ignore: ["firebase.json", "**/.*", "**/node_modules/**"], predeploy: ["npm --prefix web run build"], rewrites: [{ source: "**", destination: "/index.html" }], headers: [no-cache em "**"; "public, max-age=31536000, immutable" em "/assets/**"] }` (igual ao Metrônomo); script `test:e2e` = `firebase emulators:exec --only auth,database --project demo-alerta-ibt "playwright test"`.

- [ ] **Step 1: Escrever o teste que falha** — `send-status.spec.ts` (Playwright, Chromium, `devices['Pixel 7']`, servidor `vite --mode emulator --port 5174 --strictPort --host 127.0.0.1` no `webServer`): semeia `/live` pelo REST (`heartbeatAt: { '.sv': 'timestamp' }`, `led: true`, `paused: false`, fila vazia, `bar` padrão); abre o app, espera `window.__alertaE2E` e entra como `maria@example.com`; `Pular` no tutorial; pílula `Telão ligado`; digita `prata corolla bra2e19 vidro aberto` → prévia `COROLLA PRATA | PLACA: BRA-2E19 | VIDRO ABERTO`; `Enviar` → toast `Enviado` e item com `enviado`; como PC (REST): lê `/requests`, grava `status { state: 'queued', position: 1, at }` e `/live/queue` → `1º na fila`; grava `status showing` + `/live/now` (`source: 'site'`, `startedAt`) → `passando`, e na aba Status o mini-telão e o card mostram o texto; grava `status shown` → item com a hora `HH:mm`.
- [ ] **Step 2: Rodar** `npm run test:e2e` → FAIL (falta Playwright/config). Se o Chromium do Playwright não estiver instalado, `npx playwright install chromium`.
- [ ] **Step 3: Implementar** — `make-icons.mjs` (como o do Metrônomo, com `sharp`): desenho de `assets/icons/logo.svg` (cor `#fffdf2`) centrado em fundo `#0a0a0c`, gerando `favicon.svg`, `icon-192.png`, `icon-512.png`, `apple-touch-icon.png` (180) e `icon-maskable-512.png` (logo menor); rodar `npm run icons` e versionar os PNGs. `VitePWA({ registerType: 'autoUpdate', manifest: { name: 'Alerta IBT', short_name: 'Alerta IBT', description: 'Avisos para o telão da IBT', lang: 'pt-BR', theme_color: '#0a0a0c', background_color: '#0a0a0c', display: 'standalone', start_url: '/', icons: [...] }, workbox: { globPatterns: ['**/*.{js,css,html,svg,png,ttf}'], navigateFallbackDenylist: [/^\/__\//] } })`. Conferir que Status, Equipe e a IA ficam em pedaços separados do JS inicial.
- [ ] **Step 4: Rodar** `npm run test:e2e` → PASS; `npm test` e `npm run test:emu` → PASS; `npm run build` → gera `dist/sw.js` e `dist/manifest.webmanifest`, e o JS inicial (entrada) fica abaixo de 300 KB gzip (anotar os tamanhos no relatório).
- [ ] **Step 5: Commit** `feat(site): PWA, Hosting e teste de ponta a ponta no emulador`

### Task 14: Depois do merge com `v8-csharp` — casos compartilhados e prompt

**Pré-condição:** `v8-site` e `v8-csharp` juntos num branch (feito pelo dono/coordenador). Não executar antes disso.

**Files:**
- Modify: `web/src/alerts/cases.test.ts`, `shared/parser-cases.json` (só acrescentar casos), `web/src/correction/prompt.ts` (se precisar alinhar)
- Delete: `web/src/alerts/parser-cases.json`

**Interfaces:**
- Consumes: `shared/parser-cases.json` do Plano 1 (`[{ raw, role, expectedText, missing, kind }]`) e `desktop/src/AlertaIBT.Core/Correction/GeminiPrompt.cs`.

- [ ] **Step 1:** Apontar `cases.test.ts` para `../../../shared/parser-cases.json`, adaptando só a leitura se o Plano 1 gravou `role`/`kind` com outra caixa (ex.: `"Member"`, `"Operator"`; `operator` conta como admin no site).
- [ ] **Step 2: Rodar** `npm test -- src/alerts` → anotar cada diferença; para cada uma, decidir pela spec 5.2 qual lado está errado, corrigir o TypeScript ou registrar o erro do C# no relatório (sem mexer em `desktop/`).
- [ ] **Step 3:** Passar para `shared/parser-cases.json` os casos de `web/src/alerts/parser-cases.json` que ainda não estão lá, apagar o arquivo local e rodar `npm test` e `dotnet test --project desktop/tests/AlertaIBT.Core.Tests` → PASS nos dois.
- [ ] **Step 4:** Comparar `SYSTEM_PROMPT`, o schema e o modelo padrão do site com `GeminiPrompt.cs` e a configuração do PC; alinhar o texto para que o PC e o celular façam o mesmo pedido (spec 5.1).
- [ ] **Step 5: Commit** `test: site e PC usam shared/parser-cases.json`
