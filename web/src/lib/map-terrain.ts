// Terrain palette, shore/depth ramps and directional-light approach adapted from
// h0tw1r3/valheim-webmap (MIT), Copyright 2021 Jeffrey Clark and Kyle Paulsen.
// See docs/licenses/valheim-webmap-MIT.txt. Generation uses the running game's APIs.
export type Terrain = { worldId: string; revision?: string; name: string; size: number; worldSize: number; extent: number; x: number; y: number; biomes: string; heights: string; forests: string; waterLevel: number }
export const biomePalette = ['#92a75c', '#a37258', '#ffffff', '#6b743f', '#e7ab78', '#b03030', '#ffffff', '#5c5c6e', '#5c3866']
const deep = [.36105883, .36105883, .43137255], shallow = [.574, .50709206, .47892025], shore = [.1981132, .12241901, .1503943]
const clamp = (v: number) => Math.max(0, Math.min(1, v))
const mix = (a: number[], b: number[], weight: number) => a.map((v, i) => v + (b[i] - v) * clamp(weight))
const bytes = (base64: string) => Uint8Array.from(atob(base64), c => c.charCodeAt(0))
export function terrainPixels(terrain: Terrain, preview: boolean): Uint8ClampedArray {
  const n = terrain.size, biomes = bytes(terrain.biomes), rawHeights = bytes(terrain.heights), forests = bytes(terrain.forests)
  if (n !== 256 || terrain.worldSize !== 2048 || terrain.extent !== 12288 || biomes.length !== n * n || forests.length !== n * n || rawHeights.length !== n * n * 4 || !Number.isFinite(terrain.waterLevel)) throw new Error('Invalid terrain data')
  const view = new DataView(rawHeights.buffer), heights = new Float32Array(n * n)
  for (let i = 0; i < heights.length; i++) { heights[i] = view.getFloat32(i * 4, true); if (!Number.isFinite(heights[i])) throw new Error('Invalid terrain height') }
  const pixels = new Uint8ClampedArray(n * n * 4), step = terrain.extent * 2 / (preview ? n : terrain.worldSize)
  for (let i = 0; i < heights.length; i++) {
    const x = i % n, y = Math.floor(i / n), height = heights[i]
    const west = heights[x ? i - 1 : i], east = heights[x < n - 1 ? i + 1 : i], north = heights[y ? i - n : i], south = heights[y < n - 1 ? i + n : i]
    const nx = (west - east) * (x === 0 || x === n - 1 ? 2 : 1), ny = 2 * step, nz = (south - north) * (y === 0 || y === n - 1 ? 2 : 1), norm = Math.hypot(nx, ny, nz)
    const light = .75 + .25 * (-nx + ny + nz) / norm / Math.sqrt(3)
    const color = biomePalette[biomes[i]] || biomePalette[7]
    let base = [parseInt(color.slice(1, 3), 16) / 255, parseInt(color.slice(3, 5), 16) / 255, parseInt(color.slice(5, 7), 16) / 255]
    // Native forest mask distinguishes wooded meadows/plains from open terrain.
    const forest = forests[i] / 255
    base = base.map(v => v * (1 - forest * .18))
    let shade = mix(shore, base, height - terrain.waterLevel)
    shade = mix(shallow, shade, (height - terrain.waterLevel + 2.5) * .5)
    shade = mix(deep, shade, (height - terrain.waterLevel + 12.5) * .1)
    for (let c = 0; c < 3; c++) pixels[i * 4 + c] = shade[c] * light * 255
    pixels[i * 4 + 3] = 255
  }
  return pixels
}
export function terrainImage(terrain: Terrain, preview = false) {
  const canvas = document.createElement('canvas'); canvas.width = canvas.height = terrain.size
  const context = canvas.getContext('2d')!, pixels = context.createImageData(terrain.size, terrain.size)
  pixels.data.set(terrainPixels(terrain, preview)); context.putImageData(pixels, 0, 0)
  return canvas.toDataURL('image/png')
}
