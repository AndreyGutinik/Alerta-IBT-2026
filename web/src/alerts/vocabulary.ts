import { trimEdges } from './format'

// As listas seguem o Vocabulary.cs do app do PC (desktop/src/AlertaIBT.Core/Alerts): todas em
// minúsculas e sem acento; a palavra digitada passa por `fold` antes de ser comparada.

/** Cores que o parser reconhece (spec 5.2), como palavras inteiras. */
export const COLORS: readonly string[] = [
  'preto',
  'preta',
  'branco',
  'branca',
  'prata',
  'cinza',
  'grafite',
  'vermelho',
  'vermelha',
  'azul',
  'verde',
  'amarelo',
  'amarela',
  'bege',
  'marrom',
  'dourado',
  'dourada',
  'vinho',
  'laranja',
  'rosa',
  'roxo',
  'roxa',
  'champanhe',
]

const COLOR_SET: ReadonlySet<string> = new Set(COLORS)

/** Palavras que grudam na cor logo antes delas ("azul marinho", "cinza metalico"). */
const COLOR_MODIFIERS: ReadonlySet<string> = new Set([
  'escuro',
  'escura',
  'claro',
  'clara',
  'metalico',
  'metalica',
  'fosco',
  'fosca',
  'marinho',
])

/** Começos de palavra que abrem o motivo ("estacionad" cobre estacionado, estacionada...). */
export const REASON_STARTS: readonly string[] = [
  'farol',
  'farois',
  'vidro',
  'estacionad',
  'parad',
  'porta',
  'pisca',
  'luz',
  'alarme',
  'pneu',
  'dono',
  'comparecer',
  'bloqueando',
  'vaga',
  'contramao',
  'proibid',
  'aceso',
  'acesa',
]

/** Palavras que abrem o motivo só quando são a palavra inteira ("somente" e "sombra" não são som). */
const REASON_WORDS: ReadonlySet<string> = new Set(['som', 'sons'])

/**
 * Minúsculas, sem acento e sem pontuação nas pontas: "Metálico", "metalico", "(metalico" e
 * "metalico." viram a mesma palavra. Como no .NET: decompõe (NFD), tira as marcas Mn e passa cada
 * caractere para minúscula.
 */
export function fold(word: string): string {
  let folded = ''
  for (const ch of word.normalize('NFD')) {
    if (!/^\p{Mn}$/u.test(ch)) folded += ch.toLowerCase()
  }
  return trimEdges(folded)
}

export function isColor(word: string): boolean {
  return COLOR_SET.has(fold(word))
}

export function isColorModifier(word: string): boolean {
  return COLOR_MODIFIERS.has(fold(word))
}

export function isReasonStart(word: string): boolean {
  const folded = fold(word)
  return REASON_WORDS.has(folded) || REASON_STARTS.some((start) => folded.startsWith(start))
}
