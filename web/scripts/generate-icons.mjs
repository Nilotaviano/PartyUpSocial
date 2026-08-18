// Regenerates the PWA raster icons in web/public from web/public/favicon.svg.
//
//   node scripts/generate-icons.mjs
//
// The favicon is the single source of the BrandMark geometry; it carries a
// prefers-color-scheme <style> block that a rasterizer cannot resolve, so the
// <rect> elements are lifted out and re-emitted with the light-theme ink
// (--foreground) over the light-theme ground (--background). Both hex values
// are the sRGB resolution of the oklch tokens in src/index.css :root.

import { readFile, writeFile } from 'node:fs/promises'
import { fileURLToPath } from 'node:url'
import { dirname, join } from 'node:path'
import sharp from 'sharp'

const publicDir = join(dirname(fileURLToPath(import.meta.url)), '..', 'public')

/** src/index.css :root --background: oklch(0.96 0.02 94.5) */
const BACKGROUND = '#f6f2e3'
/** src/index.css :root --foreground: oklch(0.289 0.041 139.5) */
const INK = '#20301c'

/** BrandMark bounding box inside the favicon's 24x24 viewBox. */
const MARK = { x: 1, y: 2, width: 22, height: 20 }

async function readMarkShapes() {
  const svg = await readFile(join(publicDir, 'favicon.svg'), 'utf8')
  const rects = svg.match(/<rect\b[^>]*\/>/g)
  if (!rects || rects.length === 0) {
    throw new Error('favicon.svg contained no <rect> geometry')
  }
  return rects
    .map((rect) => rect.replace('<rect', `<rect stroke="${INK}"`))
    .join('\n    ')
}

/**
 * @param shapes {string} the favicon's <rect> geometry, ink already resolved
 * @param size {number} output edge length in px
 * @param markFraction {number} share of the edge the mark's wider axis occupies
 */
function composeSvg(shapes, size, markFraction) {
  const scale = (size * markFraction) / MARK.width
  const dx = (size - MARK.width * scale) / 2 - MARK.x * scale
  const dy = (size - MARK.height * scale) / 2 - MARK.y * scale
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${size}" height="${size}" viewBox="0 0 ${size} ${size}">
  <rect width="${size}" height="${size}" fill="${BACKGROUND}"/>
  <g transform="translate(${dx.toFixed(3)} ${dy.toFixed(3)}) scale(${scale.toFixed(6)})" fill="none">
    ${shapes}
  </g>
</svg>`
}

// markFraction for the maskable icon keeps the mark's diagonal inside the
// 80%-of-edge safe circle Android crops to: 0.56 * sqrt(22^2 + 20^2) / 22 = 0.76.
const TARGETS = [
  { file: 'icon-192.png', size: 192, markFraction: 0.72 },
  { file: 'icon-512.png', size: 512, markFraction: 0.72 },
  { file: 'icon-maskable-512.png', size: 512, markFraction: 0.56 },
  { file: 'apple-touch-icon.png', size: 180, markFraction: 0.68 },
]

const shapes = await readMarkShapes()

for (const { file, size, markFraction } of TARGETS) {
  const svg = composeSvg(shapes, size, markFraction)
  const png = await sharp(Buffer.from(svg), { density: 384 })
    .resize(size, size)
    .flatten({ background: BACKGROUND })
    .png({ compressionLevel: 9 })
    .toBuffer()
  await writeFile(join(publicDir, file), png)
  const meta = await sharp(png).metadata()
  console.log(
    `${file}: ${meta.width}x${meta.height}, ${png.length} bytes, alpha=${meta.hasAlpha}`,
  )
}
