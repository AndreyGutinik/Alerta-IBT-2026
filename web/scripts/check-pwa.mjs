// Confere o resultado de `npm run build` (web/dist): manifesto instalável, ícones nos tamanhos certos,
// service worker sem cache de execução (nada de /live nem do tráfego do banco) e o firebase.json do Hosting.
// Uso: `npm run build && npm run check:pwa`. Sai com código 1 se algo estiver errado.
import { readFile } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'
import sharp from 'sharp'

const here = dirname(fileURLToPath(import.meta.url))
const dist = resolve(here, '../dist')
const problems = []
const check = (ok, message) => {
  if (!ok) problems.push(message)
}

const read = (name) => readFile(resolve(dist, name), 'utf8')

const manifest = JSON.parse(await read('manifest.webmanifest'))
check(manifest.name === 'Alerta IBT', 'manifesto: name deve ser "Alerta IBT"')
check(manifest.short_name, 'manifesto: falta short_name')
check(manifest.lang === 'pt-BR', 'manifesto: lang deve ser pt-BR')
check(manifest.display === 'standalone', 'manifesto: display deve ser standalone')
check(manifest.start_url === '/', 'manifesto: start_url deve ser "/"')
check(/^#0[0-9a-f]{5}$/i.test(manifest.theme_color), 'manifesto: theme_color escuro esperado')
check(manifest.background_color, 'manifesto: falta background_color')

const icons = manifest.icons ?? []
for (const [size, purpose] of [
  [192, 'any'],
  [512, 'any'],
  [512, 'maskable'],
]) {
  const icon = icons.find((i) => i.sizes === `${size}x${size}` && i.purpose === purpose)
  check(icon, `manifesto: falta ícone ${size}x${size} (${purpose})`)
  if (!icon) continue
  const file = await readFile(resolve(dist, icon.src))
  const meta = await sharp(file).metadata()
  check(meta.format === 'png', `${icon.src}: deve ser PNG`)
  check(meta.width === size && meta.height === size, `${icon.src}: deve ter ${size}x${size}, tem ${meta.width}x${meta.height}`)
}
const apple = await sharp(await readFile(resolve(dist, 'apple-touch-icon.png'))).metadata()
check(apple.width === 180 && apple.height === 180, 'apple-touch-icon.png deve ter 180x180')

const html = await read('index.html')
check(html.includes('rel="manifest"'), 'index.html: falta o link do manifesto')
check(html.includes('registerSW.js'), 'index.html: falta o registro do service worker')
check(html.includes('rel="apple-touch-icon"'), 'index.html: falta o apple-touch-icon')
check(/<html lang="pt-BR"/.test(html), 'index.html: lang pt-BR')
check(/theme-color/.test(html), 'index.html: falta theme-color')

const sw = await read('sw.js')
check(/precacheAndRoute/.test(sw), 'sw.js: sem pré-cache da casca do app')
check(!/firebaseio|googleapis|identitytoolkit|securetoken|\/live/.test(sw), 'sw.js: não pode citar banco, Auth ou /live')
const routes = sw.match(/registerRoute\(/g) ?? []
check(routes.length === 1 && /NavigationRoute/.test(sw), 'sw.js: só a rota de navegação (nada de cache de execução)')
check(sw.includes('denylist:[') && sw.includes('__'), 'sw.js: /__/ (login do Firebase) deve ficar fora da casca')
const precache = sw.match(/"url":"([^"]+)"|url:"([^"]+)"/g) ?? []
check(precache.some((u) => /index\.html/.test(u)), 'sw.js: index.html fora do pré-cache')
check(precache.some((u) => /\.ttf/.test(u)), 'sw.js: fontes fora do pré-cache')

const hosting = JSON.parse(await readFile(resolve(here, '../../firebase.json'), 'utf8')).hosting
check(hosting?.public === 'web/dist', 'firebase.json: hosting.public deve ser web/dist')
check(JSON.stringify(hosting?.rewrites) === JSON.stringify([{ source: '**', destination: '/index.html' }]), 'firebase.json: rewrite SPA')
const headers = JSON.stringify(hosting?.headers ?? [])
check(headers.includes('public, max-age=31536000, immutable') && headers.includes('/assets/**'), 'firebase.json: assets imutáveis')
check(headers.includes('no-cache'), 'firebase.json: no-cache para index.html e service worker')

if (problems.length > 0) {
  console.error(`check:pwa falhou:\n- ${problems.join('\n- ')}`)
  process.exit(1)
}
console.log(`check:pwa ok (${icons.length} ícones, ${precache.length} arquivos no pré-cache)`)
