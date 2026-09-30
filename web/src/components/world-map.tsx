import { useEffect, useRef, useState, type FormEvent, type PointerEvent } from 'react'
import { Crosshair, Map, Minus, Plus, RotateCcw } from 'lucide-react'
import { toast } from 'sonner'
import { request, post, ApiError } from '@/api'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { terrainImage, biomePalette as palette, type Terrain } from '@/lib/map-terrain'
import { mapPoint, validDestination, type Point } from '@/lib/map-coordinates'

type Player = { peerKey: string; platformId: string; session: string; name: string; position: Point | null; state: string; teleportCapable: boolean }
type Frame = { worldId: string; name: string; status: string; receivedAt: string; terrainStatus?: string; terrainRevision?: string; players: Player[] }
type Destination = { player: Player; worldId: string; terrainRevision: string; x: number; y: number | null; z: number; commandId: string }
const legend = ['Meadows', 'Swamp', 'Mountain', 'Black forest', 'Plains', 'Ashlands', 'Deep north', 'Ocean', 'Mistlands']
export function WorldMapPage() {
  const [frame, setFrame] = useState<Frame | null>(null), [error, setError] = useState('')
  const [terrain, setTerrain] = useState<{ world: string; image: string } | null>(null)
  const [tiles, setTiles] = useState<Record<string, { world: string; image: string }>>({})
  const [selected, setSelected] = useState(''), [view, setView] = useState({ x: 0, z: 0, span: 24576 })
  const [x, setX] = useState(''), [y, setY] = useState(''), [z, setZ] = useState('')
  const [pending, setPending] = useState<Destination | null>(null), [busy, setBusy] = useState(false)
  const [feedback, setFeedback] = useState(''), [now, setNow] = useState(Date.now()), [received, setReceived] = useState(0)
  const [mapPixels, setMapPixels] = useState(700)
  const [preview, setPreview] = useState<{ x: number; z: number } | null>(null)
  const svg = useRef<SVGSVGElement>(null), sending = useRef(false)
  const gesture = useRef<{ player?: Player; sx: number; sy: number; origin: typeof view; world: string; revision: string; moved: boolean } | null>(null)
  const mapIdentity = frame ? `${frame.worldId}:${frame.terrainRevision || ''}` : ''
  const fresh = frame?.status === 'live' && now - received < 5000 && !error
  const player = frame?.players.find(p => p.peerKey === selected)
  const canTeleport = (p?: Player) => !!fresh && !!p && p.state === 'ready' && p.teleportCapable && !!p.position
  useEffect(() => {
    const observer = new ResizeObserver(entries => { const { width, height } = entries[0].contentRect; setMapPixels(Math.max(1, Math.min(width, height))) })
    if (svg.current) observer.observe(svg.current)
    return () => observer.disconnect()
  }, [])
  useEffect(() => {
    let stopped = false, timer = 0
    const controller = new AbortController()
    const load = async () => {
      try {
        if (!document.hidden) {
          const next = await request<Frame>('/api/v1/world-map', { signal: controller.signal })
          if (stopped) return
          setFrame(next); setReceived(Date.now()); setError('')
        }
      } catch (cause) {
        if (!stopped) {
          setError((cause as Error).message)
          if (cause instanceof ApiError && [401, 403].includes(cause.status)) { setFrame(null); setTerrain(null); setTiles({}); setPending(null); setSelected('') }
        }
      } finally { if (!stopped) timer = window.setTimeout(load, 1000) }
    }
    void load()
    const clock = window.setInterval(() => setNow(Date.now()), 1000)
    return () => { stopped = true; controller.abort(); clearTimeout(timer); clearInterval(clock) }
  }, [])
  useEffect(() => {
    // Terrain transfer/rendering has a separate lifetime; slow tiles cannot stall positions.
    setTiles({}); setTerrain(null)
    if (!frame?.worldId) return
    const world = frame.worldId, revision = frame.terrainRevision || '', identity = `${world}:${revision}`
    const controller = new AbortController(), loaded = new Set<string>()
    let stopped = false, timer = 0, previewLoaded = false
    const matches = (data: Terrain) => data.worldId === world && (data.revision || '') === revision
    const load = async () => {
      try {
        if (!document.hidden) {
          if (!previewLoaded) {
            try {
              const data = await request<Terrain>(`/api/v1/world-map/terrain?worldId=${encodeURIComponent(world)}`, { signal: controller.signal })
              if (!stopped && matches(data)) { setTerrain({ world: identity, image: terrainImage(data, true) }); previewLoaded = true }
            } catch { /* The server creates its preview before detail tiles. */ }
          }
          const index = await request<{ keys: string[] }>(`/api/v1/world-map/tiles?worldId=${encodeURIComponent(world)}`, { signal: controller.signal })
          for (const key of index.keys.filter(key => !loaded.has(key)).slice(0, 2)) {
            const [tx, ty] = key.split(':').map(Number)
            const data = await request<Terrain>(`/api/v1/world-map/tiles/${tx}/${ty}?worldId=${encodeURIComponent(world)}`, { signal: controller.signal })
            if (stopped || !matches(data)) continue
            const image = terrainImage(data); loaded.add(key)
            setTiles(previous => ({ ...previous, [key]: { world: identity, image } }))
          }
        }
      } catch { /* A failed tile is retried; the position feed independently reports connection status. */ }
      finally { if (!stopped && loaded.size < 64) timer = window.setTimeout(load, 1000) }
    }
    void load()
    return () => { stopped = true; controller.abort(); clearTimeout(timer) }
  }, [mapIdentity])
  useEffect(() => {
    // Reconnect/world change invalidates selections and staged destinations, even with the same peer key.
    if (pending && (frame?.worldId !== pending.worldId || (frame?.terrainRevision || '') !== pending.terrainRevision || !frame.players.some(p => p.peerKey === pending.player.peerKey && p.session === pending.player.session && p.platformId === pending.player.platformId))) {
      setPending(null); setFeedback('Player reconnected or world changed. Select the current player again.')
    }
    const drag = gesture.current, dragging = drag?.player
    if (drag && (drag.world !== frame?.worldId || drag.revision !== (frame?.terrainRevision || '') || (dragging && !frame?.players.some(p => p.peerKey === dragging.peerKey && p.session === dragging.session)))) { gesture.current = null; setPreview(null) }
  }, [frame, pending])
  useEffect(() => {
    const cancel = (event: KeyboardEvent) => { if (event.key === 'Escape' && !sending.current) { gesture.current = null; setPreview(null); setPending(null) } }
    window.addEventListener('keydown', cancel); return () => window.removeEventListener('keydown', cancel)
  }, [])
  const point = (event: PointerEvent) => {
    const transform = svg.current?.getScreenCTM()?.inverse()
    if (!transform) return null
    const p = new DOMPoint(event.clientX, event.clientY).matrixTransform(transform)
    return mapPoint(p.x, p.y)
  }
  const start = (event: PointerEvent<SVGSVGElement>) => {
    if (event.button !== 0 || sending.current || pending) return
    const target = (event.target as Element).closest('[data-player]')?.getAttribute('data-player')
    const p = frame?.players.find(item => item.peerKey === target)
    if (p) { setSelected(p.peerKey); if (!canTeleport(p)) return }
    gesture.current = { player: p, sx: event.clientX, sy: event.clientY, origin: view, world: frame?.worldId || '', revision: frame?.terrainRevision || '', moved: false }
    event.currentTarget.setPointerCapture(event.pointerId)
  }
  const move = (event: PointerEvent<SVGSVGElement>) => {
    const drag = gesture.current
    if (!drag) return
    if (Math.hypot(event.clientX - drag.sx, event.clientY - drag.sy) > 8) drag.moved = true
    if (!drag.moved) return
    if (drag.player) setPreview(point(event))
    else {
      const scale = drag.origin.span / Math.min(event.currentTarget.clientWidth, event.currentTarget.clientHeight)
      setView({ ...drag.origin, x: drag.origin.x - (event.clientX - drag.sx) * scale, z: drag.origin.z + (event.clientY - drag.sy) * scale })
    }
  }
  const finish = (event: PointerEvent<SVGSVGElement>) => {
    const drag = gesture.current, destination = point(event)
    gesture.current = null; setPreview(null)
    if (event.currentTarget.hasPointerCapture(event.pointerId)) event.currentTarget.releasePointerCapture(event.pointerId)
    if (!drag?.player || !drag.moved || !destination || !canTeleport(drag.player) || !frame) return
    const bounds = event.currentTarget.getBoundingClientRect()
    if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom) return
    if (!validDestination(destination.x, null, destination.z)) { setFeedback('Drop inside the 10,000 m world boundary.'); return }
    setPending({ player: drag.player, worldId: frame.worldId, terrainRevision: frame.terrainRevision || '', x: Math.round(destination.x), y: null, z: Math.round(destination.z), commandId: crypto.randomUUID() }); setFeedback('')
  }
  const stage = (event: FormEvent) => {
    event.preventDefault()
    const nx = x.trim() ? Number(x) : NaN, nz = z.trim() ? Number(z) : NaN, ny = y.trim() ? Number(y) : null
    if (!validDestination(nx, ny, nz)) { setFeedback('Enter finite X/Z within the 10,000 m circle. Y must be −100 to 500, or empty for ground.'); return }
    if (!canTeleport(player) || !player || !frame) { setFeedback('Select an alive, ready player with the updated client and a fresh connection.'); return }
    setPending({ player, worldId: frame.worldId, terrainRevision: frame.terrainRevision || '', x: nx, y: ny, z: nz, commandId: crypto.randomUUID() }); setFeedback('')
  }
  const currentPendingPlayer = pending && frame?.players.find(p => p.peerKey === pending.player.peerKey && p.session === pending.player.session && p.platformId === pending.player.platformId)
  const confirm = async () => {
    if (!pending || sending.current || !canTeleport(currentPendingPlayer || undefined) || pending.worldId !== frame?.worldId) return
    sending.current = true; setBusy(true); setFeedback('')
    try {
      const result = await post<{ position: Point }>(`/api/v1/world-map/players/${pending.player.peerKey}/teleport`, { commandId: pending.commandId, worldId: pending.worldId, terrainRevision: pending.terrainRevision, platformId: pending.player.platformId, session: pending.player.session, x: pending.x, y: pending.y, z: pending.z })
      const message = `Teleported ${pending.player.name} to X ${result.position.x.toFixed(1)}, Y ${result.position.y.toFixed(1)}, Z ${result.position.z.toFixed(1)}`
      setFeedback(message); toast.success(message); setPending(null)
    } catch (cause) { setFeedback((cause as Error).message) }
    finally { sending.current = false; setBusy(false) }
  }
  const zoom = (factor: number) => setView(v => ({ ...v, span: Math.max(200, Math.min(30000, v.span * factor)) }))
  const unit = view.span / mapPixels
  return <div className="space-y-4">
    <div className="flex flex-wrap items-center justify-between gap-3"><div><h1 className="text-2xl font-semibold">World map</h1><p className="text-sm text-muted-foreground">{frame?.name || 'Waiting for server'} · X east / Z north · metres</p></div><Badge variant={fresh ? 'secondary' : 'destructive'}>{fresh ? 'Live · 1s samples' : error ? 'Disconnected' : frame?.status || 'Connecting'}</Badge></div>
    {error && <Alert variant="destructive"><AlertDescription>{error}</AlertDescription></Alert>}
    {feedback && !pending && <Alert><AlertDescription>{feedback}</AlertDescription></Alert>}
    <div className="grid items-start gap-4 xl:grid-cols-[minmax(0,1fr)_300px]">
      <Card className="overflow-hidden"><CardContent className="relative p-0">
        <div className="absolute top-3 left-3 z-10 flex gap-1"><Button size="icon" variant="secondary" aria-label="Zoom in" onClick={() => zoom(.65)}><Plus /></Button><Button size="icon" variant="secondary" aria-label="Zoom out" onClick={() => zoom(1.5)}><Minus /></Button><Button size="icon" variant="secondary" aria-label="Reset map view" onClick={() => setView({ x: 0, z: 0, span: 24576 })}><RotateCcw /></Button></div>
        <svg ref={svg} role="img" aria-label="Valheim world map. Drag a player to choose a teleport destination; confirmation is required." className="aspect-square max-h-[72vh] w-full touch-none select-none bg-[#0c1723]" viewBox={`${view.x - view.span / 2} ${-view.z - view.span / 2} ${view.span} ${view.span}`} onPointerDown={start} onPointerMove={move} onPointerUp={finish} onPointerCancel={() => { gesture.current = null; setPreview(null) }} onWheel={event => { event.preventDefault(); if (!gesture.current) zoom(event.deltaY > 0 ? 1.15 : .87) }}>
          <defs><clipPath id="world-circle"><circle r="10000" /></clipPath></defs>
          {terrain && terrain.world === mapIdentity && <image href={terrain.image} x="-12288" y="-12288" width="24576" height="24576" />}
          {Object.entries(tiles).map(([key, tile]) => { const [tx, ty] = key.split(':').map(Number); return tile.world === mapIdentity && <image key={key} href={tile.image} x={-12288 + tx * 3072} y={-12288 + ty * 3072} width="3072" height="3072" /> })}
          <circle r="10000" fill="none" stroke="#8096ab" strokeWidth={unit} strokeDasharray={`${unit * 8} ${unit * 5}`} />
          <path d="M-10000 0H10000 M0 -10000V10000" stroke="#d8e4ec" opacity=".2" strokeWidth={unit} />
          <text x="0" y="-9800" textAnchor="middle" fill="#eee" fontSize={unit * 16}>N · +Z</text>
          {frame?.players.map(p => p.position && <g key={`${p.platformId}:${p.session}`} data-player={p.peerKey} style={{ cursor: canTeleport(p) ? 'grab' : 'pointer' }} opacity={fresh && p.state === 'ready' ? 1 : .4}>
            <circle cx={p.position.x} cy={-p.position.z} r={unit * 22} fill="transparent" />
            <circle cx={p.position.x} cy={-p.position.z} r={unit * 6} fill={p.peerKey === selected ? '#facc15' : '#61dcf4'} stroke="#101a20" strokeWidth={unit * 2} />
            <text x={p.position.x + unit * 10} y={-p.position.z + unit * 4} fill="#fff" stroke="#101a20" paintOrder="stroke" strokeWidth={unit * 2} fontSize={unit * 13}>{p.name}</text>
          </g>)}
          {(preview || pending) && <g pointerEvents="none"><circle cx={(preview || pending)!.x} cy={-(preview || pending)!.z} r={unit * 12} fill="none" stroke="#facc15" strokeWidth={unit * 2} /><text x={(preview || pending)!.x + unit * 18} y={-(preview || pending)!.z} fill="#facc15" fontSize={unit * 13}>X {(preview || pending)!.x.toFixed(0)} / Z {(preview || pending)!.z.toFixed(0)}</text></g>}
        </svg>
        {terrain?.world !== mapIdentity && <div className="pointer-events-none absolute inset-x-0 top-20 text-center text-sm text-muted-foreground">{frame?.status === 'unsupported' ? 'Update the server agent to enable world map data.' : frame?.terrainStatus === 'failed' ? 'Terrain generation failed. Check the server plugin log.' : 'Terrain is being sampled by the server…'}</div>}
        <div className="border-t px-4 py-3 text-xs text-muted-foreground">Pan the map or zoom with the wheel. Drag a marker, then confirm its destination. Esc cancels. Terrain refines to 2048 × 2048 at 12 m/pixel with forest, depth and elevation shading. Buildings are checked in game.</div>
      </CardContent></Card>
      <div className="space-y-4"><Card><CardHeader><CardTitle className="flex items-center gap-2"><Map className="size-4" />Players</CardTitle><CardDescription>{frame?.players.length || 0} connected · retained positions fade when stale</CardDescription></CardHeader><CardContent className="space-y-2">
        {frame?.players.map(p => <button key={p.session} type="button" className={`w-full rounded-md border p-3 text-left text-sm ${selected === p.peerKey ? 'border-primary bg-primary/10' : ''}`} onClick={() => { setSelected(p.peerKey); if (p.position) { setX(p.position.x.toFixed(1)); setZ(p.position.z.toFixed(1)); setY('') } }}><span className="block font-medium">{p.name}</span><span className="text-xs text-muted-foreground">{!fresh ? 'Last received' : p.state} · {p.teleportCapable ? 'Teleport client ready' : 'Client update required'}</span><span className="block font-mono text-[11px] text-muted-foreground">{p.position ? `X ${p.position.x.toFixed(0)} · Y ${p.position.y.toFixed(0)} · Z ${p.position.z.toFixed(0)}` : 'Position unavailable'}</span></button>)}
        {!frame?.players.length && <p className="text-sm text-muted-foreground">No online players.</p>}
      </CardContent></Card>
      <Card><CardHeader><CardTitle>Teleport coordinates</CardTitle><CardDescription>{player?.name || 'Select a player above'}. Leave Y empty for automatic ground placement.</CardDescription></CardHeader><CardContent><form onSubmit={stage} className="space-y-3">{[['X · east', x, setX], ['Y · height (optional)', y, setY], ['Z · north', z, setZ]].map(([label, value, setter], i) => <div key={i}><Label htmlFor={`map-coordinate-${i}`}>{label as string}</Label><Input id={`map-coordinate-${i}`} value={value as string} inputMode="decimal" placeholder={i === 1 ? 'Automatic ground' : 'Metres'} onChange={event => (setter as (s: string) => void)(event.target.value)} disabled={busy} /></div>)}<Button type="submit" disabled={!canTeleport(player) || busy || !!pending} className="w-full"><Crosshair /> Review destination</Button><Button type="button" variant="outline" className="w-full" disabled={!player?.position} onClick={() => player?.position && setView(v => ({ ...v, x: player.position!.x, z: player.position!.z }))}>Centre selected player</Button></form><p className="mt-3 text-xs text-muted-foreground">Dry natural terrain only. Dead/loading players and unsafe destinations are refused. The updated companion client is required.</p></CardContent></Card>
      </div>
    </div>
    <div className="flex flex-wrap gap-x-4 gap-y-2 text-xs text-muted-foreground">{legend.map((name, i) => <span className="flex items-center gap-1.5" key={name}><i className="size-2.5 rounded-sm" style={{ background: palette[i] }} />{name}</span>)}<span>{Object.values(tiles).filter(t => t.world === mapIdentity).length}/64 detail tiles · 12 m/pixel</span><span>World ID: {frame?.worldId || 'unavailable'}</span></div>
    <Dialog open={!!pending} onOpenChange={open => { if (!open && !sending.current) { setPending(null); setFeedback('') } }}><DialogContent><DialogHeader><DialogTitle>Teleport {pending?.player.name}?</DialogTitle><DialogDescription>Confirm movement in {frame?.name}. The client will load and validate the destination floor.</DialogDescription></DialogHeader><div className="rounded-md border p-4 font-mono text-sm">X {pending?.x.toFixed(1)} · Y {pending?.y?.toFixed(1) ?? 'ground'} · Z {pending?.z.toFixed(1)}</div>{feedback && <Alert variant="destructive"><AlertDescription>{feedback}</AlertDescription></Alert>}<div className="flex justify-end gap-2"><Button variant="outline" disabled={busy} onClick={() => { setPending(null); setFeedback('') }}>Cancel</Button><Button disabled={busy || !canTeleport(currentPendingPlayer || undefined)} onClick={confirm}>{busy ? 'Waiting for client…' : feedback ? 'Retry same request' : 'Confirm teleport'}</Button></div><p className="text-xs text-muted-foreground">A repeated confirmation uses the same command ID. If the outcome is unknown, check the live position before starting a new request.</p></DialogContent></Dialog>
  </div>
}
