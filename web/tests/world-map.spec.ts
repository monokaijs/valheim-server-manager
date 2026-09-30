import { test, expect, type Page } from '@playwright/test'
const peer = '9223372036854775806'
const viking = { peerKey: peer, platformId: 'Steam_76561198000000001', session: 'first-session', name: 'Test Viking', position: { x: 0, y: 50, z: 0 }, state: 'ready', teleportCapable: true }
async function setup(page: Page, role = 'admin') {
  let session = viking.session, world = 'test-world', status = 'live', calls = 0, delayed = false, detailed = false, tileDelay = 0, frameReads = 0, authorized = true, revision = ''
  const fixtureTerrain = (tx?: number, ty?: number) => {
    const pixels = Buffer.alloc(65536), heights = Buffer.alloc(65536 * 4), forests = Buffer.alloc(65536)
    for (let i = 0; i < pixels.length; i++) {
      const resolution = tx === undefined ? 256 : 2048
      const x = ((tx || 0) * 256 + i % 256 + .5) / resolution * 2 - 1
      const z = ((ty || 0) * 256 + Math.floor(i / 256) + .5) / resolution * 2 - 1
      const hill = Math.sin(x * 9) + Math.cos(z * 8) + Math.sin((x + z) * 11)
      const height = 30 + 40 * (hill - .3) + 3 * Math.sin(x * 173) * Math.cos(z * 157)
      pixels[i] = height < 30 ? 7 : height > 110 ? 2 : Math.abs(x) < .3 ? 0 : x > 0 ? 3 : 4
      heights.writeFloatLE(height, i * 4); forests[i] = height > 30 && Math.sin(x * 60) + Math.cos(z * 40) > .2 ? 255 : 0
    }
    return { forests: forests.toString('base64'), biomes: pixels.toString('base64'), heights: heights.toString('base64') }
  }
  const preview = fixtureTerrain()
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url()), path = url.pathname
    let data: unknown = {}
    if (!authorized && path.includes('/world-map')) { await route.fulfill({ status: 403, json: { title: 'Forbidden' } }); return }
    if (path.endsWith('/auth/state')) data = { authenticated: true, role, steamId: '76561198000000001' }
    else if (path.endsWith('/auth/csrf')) data = { token: 'test-csrf' }
    else if (path.endsWith('/status')) data = { status: 'running', agentConnected: true, players: 1 }
    else if (path.endsWith('/monitor')) data = { samples: [], events: [] }
    else if (path.endsWith('/manager-update')) data = { currentVersion: 'test' }
    else if (path.endsWith('/world-map/terrain')) data = { worldId: world, revision, name: 'Fixture Realm', size: 256, worldSize: 2048, extent: 12288, x: 0, y: 0, waterLevel: 30, ...preview }
    else if (path.endsWith('/world-map/tiles')) data = { keys: detailed ? ['3:3', '4:3', '3:4', '4:4'] : [], total: 64 }
    else if (path.includes('/world-map/tiles/')) {
      const [tx, ty] = path.split('/').slice(-2).map(Number)
      if (tileDelay) await new Promise(resolve => setTimeout(resolve, tileDelay))
      data = { worldId: world, revision, name: 'Fixture Realm', size: 256, worldSize: 2048, extent: 12288, x: tx, y: ty, waterLevel: 30, ...fixtureTerrain(tx, ty) }
    }
    else if (path.endsWith('/world-map')) { frameReads++; data = { terrainRevision: revision, worldId: world, name: 'Fixture Realm', receivedAt: new Date().toISOString(), status, players: [{ ...viking, session }] } }
    else if (path.endsWith('/teleport')) {
      calls++; expect(route.request().headers()['x-csrf-token']).toBe('test-csrf')
      const body = route.request().postDataJSON(); expect(body.session).toBe(session); expect(body.worldId).toBe(world)
      if (delayed) await new Promise(resolve => setTimeout(resolve, 700))
      data = { ok: true, position: { x: body.x, y: 55.5, z: body.z } }
    }
    await route.fulfill({ json: data })
  })
  await page.route('**/hubs/**', route => route.abort())
  await page.goto('/');
  if (role === 'admin') await page.getByRole('button', { name: 'World map' }).click()
  return { calls: () => calls, reconnect: () => { session = 'second-session' }, changeWorld: () => { world = 'other-world' }, disconnect: () => { status = 'disconnected' }, delay: () => { delayed = true }, details: () => { detailed = true }, slowTiles: () => { detailed = true; tileDelay = 6000 }, frameReads: () => frameReads, revoke: () => { authorized = false }, regenerate: () => { revision = 'regenerated' } }
}
async function select(page: Page) { await page.getByRole('button', { name: /Test Viking.*Teleport client ready/ }).click() }
async function drag(page: Page, dx: number, dy: number) {
  const marker = page.locator(`[data-player="${peer}"] circle`).last(), box = await marker.boundingBox()
  if (!box) throw new Error('Marker unavailable')
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2)
  await page.mouse.down(); await page.mouse.move(box.x + box.width / 2 + dx, box.y + box.height / 2 + dy, { steps: 10 }); await page.mouse.up()
}
test('renders map, zooms, pans, and cancels drag without any command', async ({ page }) => {
  const state = await setup(page); await expect(page.locator('svg image')).toBeVisible()
  const map = page.getByRole('img', { name: /Valheim world map/ }), original = await map.getAttribute('viewBox')
  await page.getByRole('button', { name: 'Zoom in' }).click(); expect(await map.getAttribute('viewBox')).not.toBe(original)
  await page.getByRole('button', { name: 'Reset map view' }).click()
  const box = await map.boundingBox(); if (!box) throw new Error('Map unavailable')
  await page.mouse.move(box.x + 100, box.y + 200); await page.mouse.down(); await page.mouse.move(box.x + 130, box.y + 220); await page.mouse.up()
  expect(await map.getAttribute('viewBox')).not.toBe(original)
  await page.getByRole('button', { name: 'Reset map view' }).click(); await drag(page, 50, -40)
  await expect(page.getByRole('dialog')).toBeVisible(); await page.getByRole('button', { name: 'Cancel', exact: true }).click(); expect(state.calls()).toBe(0)
  await drag(page, 50, -40); await page.keyboard.press('Escape'); await expect(page.getByRole('dialog')).not.toBeVisible(); expect(state.calls()).toBe(0)
  const cancelled = await page.locator(`[data-player="${peer}"] circle`).last().boundingBox(); if (!cancelled) throw new Error('marker')
  await page.mouse.move(cancelled.x + cancelled.width / 2, cancelled.y + cancelled.height / 2); await page.mouse.down(); await page.mouse.move(cancelled.x + 70, cancelled.y - 40); await map.dispatchEvent('pointercancel'); await page.mouse.up(); await expect(page.getByRole('dialog')).not.toBeVisible(); expect(state.calls()).toBe(0)
  const marker = await page.locator(`[data-player="${peer}"] circle`).last().boundingBox(); if (!marker) throw new Error('marker')
  await page.mouse.move(marker.x + marker.width / 2, marker.y + marker.height / 2); await page.mouse.down(); await page.mouse.move(marker.x + 70, marker.y - 40); await page.keyboard.press('Escape'); await page.mouse.up(); expect(state.calls()).toBe(0); await expect(page.getByRole('dialog')).not.toBeVisible()
})
test('manual invalid values never send, finite values confirm once on repeated clicks', async ({ page }) => {
  const state = await setup(page); await select(page)
  for (const value of ['NaN', 'Infinity', '10001', '']) {
    await page.getByLabel('X · east').fill(value); await page.getByLabel('Z · north').fill('0'); await page.getByRole('button', { name: 'Review destination' }).click()
    await expect(page.getByRole('dialog')).not.toBeVisible(); expect(state.calls()).toBe(0)
  }
  await page.getByLabel('X · east').fill('100'); await page.getByLabel('Z · north').fill('200'); await page.getByLabel('Y · height (optional)').fill('501'); await page.getByRole('button', { name: 'Review destination' }).click(); expect(state.calls()).toBe(0)
  await page.getByLabel('Y · height (optional)').fill(''); await page.getByRole('button', { name: 'Review destination' }).click(); await expect(page.getByRole('dialog')).toBeVisible()
  state.delay(); await page.getByRole('button', { name: 'Confirm teleport' }).dblclick(); await expect(page.getByRole('dialog')).not.toBeVisible(); expect(state.calls()).toBe(1)
  await expect(page.getByText(/Teleported Test Viking to X 100.0/).first()).toBeVisible()
})
test('drag has correct north axis and reconnect invalidates a staged destination', async ({ page }) => {
  const state = await setup(page); await drag(page, 60, -60)
  const text = await page.getByRole('dialog').textContent(); expect(text).toMatch(/X [1-9]/); expect(text).toMatch(/Z [1-9]/)
  state.reconnect(); await expect(page.getByRole('dialog')).not.toBeVisible(); expect(state.calls()).toBe(0)
  await select(page); await page.getByRole('button', { name: 'Review destination' }).click(); state.changeWorld(); await expect(page.getByRole('dialog')).not.toBeVisible(); expect(state.calls()).toBe(0)
})
test('stale connection disables teleport and moderator has no map navigation', async ({ page }) => {
  const state = await setup(page); await select(page); state.disconnect()
  await expect(page.getByRole('button', { name: 'Review destination' })).toBeDisabled(); await expect(page.getByText('Last received').first()).toBeVisible()
  await setup(page, 'mod'); await expect(page.getByRole('button', { name: 'World map' })).toHaveCount(0)
})
test('desktop and mobile map layouts', async ({ page }) => {
  await setup(page); await select(page); await expect(page.locator('svg image')).toBeVisible()
  await page.screenshot({ path: '../artifacts/world-map-desktop.png', fullPage: true })
  await page.setViewportSize({ width: 390, height: 844 }); await page.screenshot({ path: '../artifacts/world-map-mobile.png', fullPage: true })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy()
})

test('detail tiles align to native scale and authorization loss clears private data', async ({ page }) => {
  const state = await setup(page); state.details()
  await expect(page.locator('svg image')).toHaveCount(5)
  await expect(page.locator('svg image').nth(1)).toHaveAttribute('x', '-3072')
  await expect(page.locator('svg image').nth(1)).toHaveAttribute('y', '-3072')
  await expect(page.locator('svg image').nth(2)).toHaveAttribute('x', '0')
  await expect(page.locator('svg image').nth(2)).toHaveAttribute('width', '3072')
  for (let i = 0; i < 4; i++) await page.getByRole('button', { name: 'Zoom in' }).click()
  await page.screenshot({ path: '../artifacts/world-map-detail.png', fullPage: true })
  await drag(page, 40, -40); await page.screenshot({ path: '../artifacts/world-map-teleport-review.png', fullPage: true }); state.regenerate(); await expect(page.getByRole('dialog')).not.toBeVisible()
  state.revoke(); await expect(page.locator('[data-player]')).toHaveCount(0); await expect(page.locator('svg image')).toHaveCount(0)
})
test('slow terrain transfer does not stall live position polling', async ({ page }) => {
  const state = await setup(page); state.slowTiles(); const before = state.frameReads()
  await expect.poll(state.frameReads, { timeout: 4500 }).toBeGreaterThan(before + 2)
  await expect(page.getByText('Live · 1s samples')).toBeVisible()
})
