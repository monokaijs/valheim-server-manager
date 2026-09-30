import { test, expect } from '@playwright/test'
import { terrainPixels, type Terrain } from '../src/lib/map-terrain'
const terrain = (height: number): Terrain => {
  const values = Buffer.alloc(65536 * 4); for (let i = 0; i < 65536; i++) values.writeFloatLE(height, i * 4)
  return { worldId: 'test', name: 'Test', size: 256, worldSize: 2048, extent: 12288, x: 0, y: 0, waterLevel: 30, biomes: Buffer.alloc(65536).toString('base64'), forests: Buffer.alloc(65536).toString('base64'), heights: values.toString('base64') }
}
test('native float heights preserve water depth, forest masks and uniform tile boundaries', () => {
  const dry = terrainPixels(terrain(50), false), sea = terrainPixels(terrain(0), false), shallow = terrainPixels(terrain(29), false)
  expect(Array.from(dry.slice(0, 4))).not.toEqual(Array.from(sea.slice(0, 4)))
  expect(Array.from(shallow.slice(0, 4))).not.toEqual(Array.from(sea.slice(0, 4)))
  expect(Array.from(dry.slice(0, 4))).toEqual(Array.from(dry.slice(1020, 1024)))
  const wooded = terrainPixels({ ...terrain(50), forests: Buffer.alloc(65536, 255).toString('base64') }, false)
  expect(wooded[0]).toBeLessThan(dry[0])
  const nan = terrain(NaN); expect(() => terrainPixels(nan, false)).toThrow('Invalid terrain height')
})
