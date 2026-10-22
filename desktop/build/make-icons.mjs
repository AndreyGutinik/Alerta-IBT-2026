// Gera TODOS os icones do Alerta-IBT a partir do desenho vetorial assets/icons/logo.svg (o mesmo de
// Logo_IBT/Creme Claro_3_Brand-Mark.svg: marca creme #fffdf2 sobre fundo transparente).
// Cada tamanho e renderizado a partir do vetor (nunca ampliando um bitmap). Desenho: quadrado escuro
// arredondado (cor --bg de web/src/styles/tokens.css) com a marca centralizada; nos tamanhos pequenos a marca
// fica maior e e encaixada na grade de pixels para ler bem na bandeja.
//
// Uso (a partir de desktop/build): npm install && npm run icons
// Saidas (versionadas):
//   assets/icons/app.ico         quadros PNG em 16,20,24,32,40,48,64,96,128,256
//   assets/icons/app-256.png, app-512.png
//   web/public/icon-192.png, icon-512.png, icon-maskable-512.png, apple-touch-icon.png, favicon.ico, favicon.svg
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import sharp from 'sharp'

const here = dirname(fileURLToPath(import.meta.url))
const root = resolve(here, '../..')
const logoPath = resolve(root, 'assets/icons/logo.svg')
const iconsDir = resolve(root, 'assets/icons')
const publicDir = resolve(root, 'web/public')

export const ICO_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
const RADIUS = 0.22

// Cor do fundo do app (token --bg do tema escuro).
const tokens = await readFile(resolve(root, 'web/src/styles/tokens.css'), 'utf8')
const BACKGROUND = /--bg:\s*(#[0-9a-fA-F]{6})/.exec(tokens)?.[1]
if (!BACKGROUND) throw new Error('--bg nao encontrado em web/src/styles/tokens.css')

const source = await readFile(resolve(logoPath), 'utf8')
const viewBox = /viewBox="([^"]+)"/.exec(source)?.[1]
const paths = [...source.matchAll(/<path[^>]*\sd="([^"]+)"/g)].map((m) => m[1])
const fill = /fill:\s*(#[0-9a-fA-F]{3,8})/.exec(source)?.[1] ?? '#fffdf2'
if (!viewBox || paths.length === 0) throw new Error(`logo.svg inesperado: ${logoPath}`)
const [vx, vy, vw, vh] = viewBox.split(/\s+/).map(Number)
const aspect = vh / vw

/** Largura da marca (fracao do lado) conforme o tamanho: maior (86% a 80%) e na grade de pixels nos pequenos. */
function markWidth(size, scale) {
  if (scale !== undefined) return size * scale
  if (size <= 24) return Math.round(size * 0.86)
  if (size <= 32) return Math.round(size * 0.8)
  if (size <= 48) return Math.round(size * 0.7)
  return size * 0.64
}

/** SVG quadrado `size` x `size`: tile escuro (com `radius` da lateral) e a marca centralizada. */
function composition({ size, scale, radius = RADIUS, snap = size <= 48 }) {
  let w = markWidth(size, scale)
  let h = w * aspect
  let x = (size - w) / 2
  let y = (size - h) / 2
  if (snap) {
    h = Math.round(h)
    x = Math.round(x)
    y = Math.round(y)
  }
  const s = w / vw
  const sy = h / vh
  const rx = +(size * radius).toFixed(3)
  const d = paths.map((p) => `<path d="${p}"/>`).join('')
  return (
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 ${size} ${size}">` +
    `<rect width="${size}" height="${size}" rx="${rx}" fill="${BACKGROUND}"/>` +
    `<g fill="${fill}" transform="translate(${x} ${y}) scale(${+s.toFixed(6)} ${+sy.toFixed(6)}) translate(${-vx} ${-vy})">${d}</g>` +
    `</svg>`
  )
}

/** Renderiza o vetor direto no tamanho final (sem reamostrar bitmap). */
const render = (options) =>
  sharp(Buffer.from(composition(options)), { density: 72 })
    .png({ compressionLevel: 9, adaptiveFiltering: true })
    .toBuffer()

/** Monta um .ico com quadros PNG (Vista+). Largura/altura 256 sao gravadas como 0. */
function buildIco(frames) {
  const header = Buffer.alloc(6)
  header.writeUInt16LE(0, 0)
  header.writeUInt16LE(1, 2)
  header.writeUInt16LE(frames.length, 4)
  const dir = Buffer.alloc(16 * frames.length)
  let offset = 6 + dir.length
  frames.forEach(({ size, data }, i) => {
    const o = i * 16
    dir.writeUInt8(size >= 256 ? 0 : size, o)
    dir.writeUInt8(size >= 256 ? 0 : size, o + 1)
    dir.writeUInt8(0, o + 2)
    dir.writeUInt8(0, o + 3)
    dir.writeUInt16LE(1, o + 4)
    dir.writeUInt16LE(32, o + 6)
    dir.writeUInt32LE(data.length, o + 8)
    dir.writeUInt32LE(offset, o + 12)
    offset += data.length
  })
  return Buffer.concat([header, dir, ...frames.map((f) => f.data)])
}

await mkdir(iconsDir, { recursive: true })
await mkdir(publicDir, { recursive: true })
const out = async (dir, name, data) => {
  await writeFile(resolve(dir, name), data)
  console.log(`${dir === iconsDir ? 'assets/icons' : 'web/public'}/${name}`)
}

// Aplicativo
const frames = []
for (const size of ICO_SIZES) frames.push({ size, data: await render({ size }) })
await out(iconsDir, 'app.ico', buildIco(frames))
await out(iconsDir, 'app-256.png', frames.find((f) => f.size === 256).data)
await out(iconsDir, 'app-512.png', await render({ size: 512 }))

// Site (PWA). Maskable: tile sem cantos (o sistema recorta) e marca dentro dos 60% centrais.
await out(publicDir, 'icon-192.png', await render({ size: 192 }))
await out(publicDir, 'icon-512.png', await render({ size: 512 }))
await out(publicDir, 'icon-maskable-512.png', await render({ size: 512, scale: 0.5, radius: 0 }))
await out(publicDir, 'apple-touch-icon.png', await render({ size: 180, radius: 0 }))
await out(publicDir, 'favicon.ico', buildIco([16, 32, 48].map((size) => ({ size, data: frames.find((f) => f.size === size).data }))))
await out(publicDir, 'favicon.svg', composition({ size: 512, snap: false }))
