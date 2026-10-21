// Gera os ícones do PWA a partir do desenho de assets/icons/logo.svg (cor #fffdf2) centrado num fundo
// #0a0a0c. Uso: `npm run icons` (os PNGs gerados ficam versionados em web/public).
import { mkdir, readFile, writeFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import sharp from 'sharp'

const here = dirname(fileURLToPath(import.meta.url))
const logoPath = resolve(here, '../../assets/icons/logo.svg')
const outDir = resolve(here, '../public')
const BACKGROUND = '#0a0a0c'

const source = await readFile(logoPath, 'utf8')
const viewBox = /viewBox="([^"]+)"/.exec(source)?.[1]
const inner = /<svg[^>]*>([\s\S]*)<\/svg>/.exec(source)?.[1]
if (!viewBox || !inner) throw new Error(`logo.svg inesperado: ${logoPath}`)
const [, , logoW, logoH] = viewBox.split(/\s+/).map(Number)

/** SVG quadrado `size` x `size`: fundo e o logo ocupando `scale` da largura, centrado. `radius` arredonda o fundo. */
function composition({ size, scale, radius = 0 }) {
  const w = size * scale
  const h = w * (logoH / logoW)
  const x = (size - w) / 2
  const y = (size - h) / 2
  return (
    `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 ${size} ${size}">` +
    `<rect width="${size}" height="${size}" rx="${radius}" fill="${BACKGROUND}"/>` +
    `<svg x="${x}" y="${y}" width="${w}" height="${h}" viewBox="${viewBox}">${inner}</svg>` +
    `</svg>`
  )
}

const png = (name, options) => ({ name, svg: composition(options), size: options.size })

const outputs = [
  png('icon-192.png', { size: 192, scale: 0.62 }),
  png('icon-512.png', { size: 512, scale: 0.62 }),
  // Maskable: o sistema recorta até um círculo de 80%; o logo fica bem dentro da zona segura.
  png('icon-maskable-512.png', { size: 512, scale: 0.46 }),
  png('apple-touch-icon.png', { size: 180, scale: 0.62 }),
]

await mkdir(outDir, { recursive: true })
for (const { name, svg, size } of outputs) {
  await sharp(Buffer.from(svg), { density: 384 }).resize(size, size).png({ compressionLevel: 9 }).toFile(resolve(outDir, name))
  console.log(`public/${name}`)
}
await writeFile(resolve(outDir, 'favicon.svg'), composition({ size: 512, scale: 0.62, radius: 96 }))
console.log('public/favicon.svg')
