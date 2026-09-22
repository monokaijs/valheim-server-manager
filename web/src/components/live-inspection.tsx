import { useEffect, useMemo, useRef, useState } from "react"
import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr"
import { Activity, Backpack, Circle, Clock3, Eye, HeartPulse, Pause, Play, Search, Shield, Sparkles, WifiOff } from "lucide-react"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Button } from "@/components/ui/button"
import { Badge } from "@/components/ui/badge"
import { Input } from "@/components/ui/input"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { cn } from "@/lib/utils"
import { request } from "../api"

export type InspectablePlayer = { peerId: number; peerKey: string; name: string; platformId: string; companion: boolean; inventoryAllowed: boolean }
type Item = { prefab: string; name: string; description: string; type: string; stack: number; maxStack: number; quality: number; maxQuality: number; durability: number; maxDurability: number; weight: number; equipped: boolean; x: number; y: number; variant: number; crafterName: string; crafterId: string; teleportable: boolean; iconKey: string }
type Snapshot = { capturedAt: string; character: { id: string; name: string; biome: string; health: number; maxHealth: number; stamina: number; maxStamina: number; eitr: number; maxEitr: number; armor: number; inventoryWidth: number; inventoryHeight: number; weight: number; skills: { name: string; level: number }[] }; items: Item[]; icons: Record<string, string> }
type Frame = { sequence: number; peerId: string; receivedAt: string; status: string; error?: string; snapshot?: Snapshot }
type Change = { id: string; at: number; text: string }
const finite = (value: number) => Number.isFinite(value) ? value : 0
const number = (value: number, digits = 0) => finite(value).toFixed(digits)
const position = (item: Item) => `${item.x}:${item.y}`
const fingerprint = (item?: Item) => item ? `${item.prefab}|${item.stack}|${item.quality}|${item.equipped}|${Math.round(item.durability)}` : ""
const image = (source?: string) => source && /^data:image\/png;base64,[A-Za-z0-9+/=]+$/.test(source) && source.length <= 200_000 ? source : undefined

function Meter({ label, value, max, tone, icon: Icon }: { label: string; value: number; max: number; tone: string; icon: typeof Activity }) {
  const percent = max > 0 ? Math.min(100, Math.max(0, finite(value) / max * 100)) : 0
  return <div className="rounded-xl border bg-card p-4"><div className="flex items-center justify-between text-xs text-muted-foreground"><span className="flex items-center gap-2"><Icon className={cn("size-4", tone)} />{label}</span><span>{number(max)} max</span></div><p className="my-2 text-2xl font-semibold tabular-nums tracking-tight">{number(value)}<span className="ml-2 text-xs font-normal text-muted-foreground">/ {number(max)}</span></p><div role="meter" aria-label={label} aria-valuemin={0} aria-valuemax={Math.max(1, finite(max))} aria-valuenow={Math.min(Math.max(0, finite(value)), Math.max(1, finite(max)))} className="h-1.5 overflow-hidden rounded-full bg-muted"><div className={cn("h-full rounded-full bg-current transition-[width] duration-500", tone)} style={{ width: `${percent}%` }} /></div></div>
}

export function LiveInspection({ player, onClose }: { player: InspectablePlayer | null; onClose: () => void }) {
  return <Dialog open={!!player} onOpenChange={open => !open && onClose()}><DialogContent className="h-[92dvh] max-h-[960px] overflow-hidden p-0 sm:max-w-[1440px]">{player && <InspectionSession key={player.peerKey} initial={player} />}</DialogContent></Dialog>
}

function InspectionSession({ initial }: { initial: InspectablePlayer }) {
  const [players, setPlayers] = useState<InspectablePlayer[]>([initial])
  const [target, setTarget] = useState(initial)
  const [rosterQuery, setRosterQuery] = useState("")
  const [query, setQuery] = useState("")
  const [paused, setPaused] = useState(false)
  const [visible, setVisible] = useState(!document.hidden)
  const [phase, setPhase] = useState("connecting")
  const [error, setError] = useState("")
  const [frame, setFrame] = useState<Frame | null>(null)
  const [slot, setSlot] = useState<string | null>(null)
  const [changed, setChanged] = useState<Set<string>>(new Set())
  const [changes, setChanges] = useState<Change[]>([])
  const [now, setNow] = useState(Date.now())
  const [retry, setRetry] = useState(0)
  const previous = useRef<Snapshot | null>(null)
  const lastSequence = useRef(-1)
  const received = useRef(0)
  const key = target.peerKey

  useEffect(() => {
    const tick = window.setInterval(() => setNow(Date.now()), 500)
    const visibility = () => setVisible(!document.hidden)
    document.addEventListener("visibilitychange", visibility)
    return () => { window.clearInterval(tick); document.removeEventListener("visibilitychange", visibility) }
  }, [])
  useEffect(() => {
    setFrame(null); setSlot(null); setChanged(new Set()); setChanges([]); setError(""); setQuery("")
    previous.current = null; lastSequence.current = -1; received.current = 0
  }, [key])
  useEffect(() => {
    if (paused || !visible) return
    let disposed = false
    let subscription: { dispose(): void } | undefined
    const connection = new HubConnectionBuilder().withUrl("/hubs/live").withAutomaticReconnect([0, 2000, 5000, 10000]).configureLogging(LogLevel.Warning).build()
    setPhase("connecting"); setError("")
    const subscribe = () => {
      if (disposed) return
      setPhase("waiting")
      subscription = connection.stream<Frame>("WatchPlayer", key).subscribe({
        next: incoming => {
          if (disposed || incoming.peerId !== key) return
          setPhase(incoming.status); setError(incoming.error || "")
          if (!incoming.snapshot || incoming.status !== "live") return
          if (incoming.sequence === lastSequence.current) return
          lastSequence.current = incoming.sequence; received.current = Date.now()
          const current = incoming.snapshot
          const before = previous.current
          const modified = new Set<string>()
          const events: Change[] = []
          if (before) {
            const old = new Map(before.items.map(item => [position(item), item]))
            const next = new Map(current.items.map(item => [position(item), item]))
            for (const id of new Set([...old.keys(), ...next.keys()])) {
              const a = old.get(id), b = next.get(id)
              if (fingerprint(a) === fingerprint(b)) continue
              modified.add(id)
              // Slot changes, not claims of authoritative transactions (moves can touch two slots).
              if (a?.prefab !== b?.prefab || a?.stack !== b?.stack || a?.equipped !== b?.equipped)
                events.push({ id: `${incoming.sequence}:${id}`, at: Date.now(), text: b ? `${b.name} ×${b.stack}${b.equipped ? " · equipped" : ""} · slot ${b.x + 1},${b.y + 1}` : `${a?.name || "Item"} left slot ${id.split(":").map(Number).map(n => n + 1).join(",")}` })
            }
          }
          previous.current = current
          setChanged(modified); setChanges(old => [...events, ...old].slice(0, 24)); setFrame(incoming)
        },
        error: reason => { if (!disposed) { setPhase("disconnected"); setError(reason.message || "Inspection disconnected.") } },
        complete: () => { if (!disposed) setPhase(current => current === "offline" ? current : "disconnected") },
      })
    }
    connection.on("players", (list: InspectablePlayer[]) => { if (!disposed) setPlayers(list) })
    connection.onreconnecting(() => { if (!disposed) { setPhase("reconnecting"); subscription?.dispose() } })
    connection.onreconnected(() => { lastSequence.current = -1; subscribe() })
    connection.onclose(() => { if (!disposed) setPhase("disconnected") })
    void connection.start().then(() => {
      if (disposed) { void connection.stop(); return }
      subscribe()
      void request<InspectablePlayer[]>("/api/v1/players").then(list => { if (!disposed) setPlayers(list) }).catch(() => {})
    }).catch(cause => { if (!disposed) { setPhase("disconnected"); setError((cause as Error).message) } })
    return () => { disposed = true; subscription?.dispose(); void connection.stop() }
  }, [key, paused, visible, retry])

  const age = received.current ? Math.max(0, (now - received.current) / 1000) : null
  const live = !paused && visible && phase === "live" && age !== null && age < 5
  const label = paused ? "Paused" : !visible ? "Tab hidden" : live ? "Live" : phase === "live" ? "Stale" : phase[0].toUpperCase() + phase.slice(1)
  const snapshot = frame?.peerId === key ? frame.snapshot : undefined, character = snapshot?.character
  const width = Math.max(1, Math.min(16, Math.floor(character?.inventoryWidth || 8)))
  const height = Math.max(1, Math.min(16, Math.floor(character?.inventoryHeight || 4)))
  const item = snapshot?.items.find(entry => position(entry) === slot)
  const slots = useMemo(() => new Map(snapshot?.items.map(entry => [position(entry), entry]) || []), [snapshot])
  const filtered = (entry: Item) => `${entry.name} ${entry.prefab} ${entry.type}`.toLowerCase().includes(query.toLowerCase())
  const roster = players.some(p => p.peerKey === key) ? players : [target, ...players]
  return <div className="flex h-full min-h-0 flex-col">
    <DialogHeader className="shrink-0 border-b bg-card px-6 py-5 pr-14"><div className="flex flex-wrap items-center gap-3"><span className="flex items-center gap-2 text-xs font-semibold uppercase tracking-[.16em] text-primary"><Eye className="size-4" />Inspection desk</span><Badge variant={live ? "secondary" : "outline"} className="gap-1.5"><Circle className={cn("size-2 fill-current", live ? "text-emerald-400" : "text-amber-400")} />{label}</Badge><Badge variant="outline">Read only</Badge></div><DialogTitle className="mt-2 text-2xl tracking-tight">{target.name}</DialogTitle><DialogDescription className="flex flex-wrap items-center gap-x-4 gap-y-1"><span className="font-mono text-xs">{target.platformId}</span><span className="flex items-center gap-1.5"><Clock3 className="size-3" />{age === null ? "Waiting for first sample" : `Last received ${age.toFixed(1)}s ago`}</span></DialogDescription></DialogHeader>
    <div className="grid min-h-0 flex-1 grid-cols-1 lg:grid-cols-[220px_minmax(0,1fr)]">
      <aside className="hidden min-h-0 flex-col border-r bg-muted/10 lg:flex"><div className="space-y-3 p-4"><div className="flex justify-between text-xs font-semibold uppercase tracking-wider text-muted-foreground"><span>Online players</span><span>{players.length}</span></div><Input aria-label="Search players" value={rosterQuery} onChange={event => setRosterQuery(event.target.value)} placeholder="Find a player…" className="h-8" /></div><div className="flex-1 overflow-y-auto px-2 pb-3">{roster.filter(p => p.name.toLowerCase().includes(rosterQuery.toLowerCase())).map(p => <button key={p.peerKey} onClick={() => { setTarget(p); setPaused(false) }} className={cn("mb-1 flex w-full items-center gap-3 rounded-lg border border-transparent px-3 py-3 text-left transition-colors hover:bg-muted/40", p.peerKey === key && "border-primary/25 bg-primary/10")}><span className="grid size-8 shrink-0 place-items-center rounded-lg border bg-card text-xs font-semibold">{p.name.slice(0, 2).toUpperCase()}</span><span className="min-w-0"><span className="block truncate text-sm font-medium">{p.name}</span><span className="text-[11px] text-muted-foreground">{p.inventoryAllowed ? "Inspection ready" : "Sharing unavailable"}</span></span></button>)}</div><div className="border-t p-4 text-[11px] leading-5 text-muted-foreground"><Shield className="mb-2 size-4" />Client-reported game data.<br />Not anti-cheat proof.<br />No snapshots are stored.</div></aside>
      <main className="min-h-0 overflow-y-auto bg-background p-4 sm:p-6"><div className="mb-5 flex flex-wrap items-center justify-between gap-3"><div className="flex items-center gap-2 text-xs text-muted-foreground"><span className="rounded-md border px-2 py-1">{character?.biome || "Unknown biome"}</span><span>Sampling ≈ 1.25s</span></div><div className="flex gap-2"><Button size="sm" variant="outline" onClick={() => setPaused(value => !value)}>{paused ? <Play /> : <Pause />}{paused ? "Resume" : "Pause"}</Button><Button size="sm" variant="outline" onClick={() => { setPaused(false); setRetry(value => value + 1) }}>Reconnect</Button></div></div>
        <select className="mb-4 w-full rounded-md border bg-card p-2 text-sm lg:hidden" aria-label="Inspect player" value={key} onChange={event => { const selected = roster.find(p => p.peerKey === event.target.value); if (selected) setTarget(selected) }}>{roster.map(p => <option key={p.peerKey} value={p.peerKey}>{p.name}</option>)}</select>
        {!live && <div role="status" className="mb-4 flex items-start gap-3 rounded-lg border border-amber-500/25 bg-amber-500/5 p-3 text-sm"><WifiOff className="mt-0.5 size-4 shrink-0 text-amber-400" /><div><p className="font-medium">{label}{snapshot ? " · showing last received data" : " · no current data"}</p><p className="mt-1 text-xs text-muted-foreground">{paused ? "Sampling is stopped. Resume to request fresh data." : error || "This view will update when a fresh player sample arrives."}</p></div></div>}
        {character && snapshot ? <div className={cn("space-y-5", !live && "opacity-70")}>
          <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4"><Meter label="Health" value={character.health} max={character.maxHealth} tone="text-red-400" icon={HeartPulse} /><Meter label="Stamina" value={character.stamina} max={character.maxStamina} tone="text-amber-300" icon={Activity} /><Meter label="Eitr" value={character.eitr} max={character.maxEitr} tone="text-violet-400" icon={Sparkles} /><div className="rounded-xl border bg-card p-4"><div className="flex items-center gap-2 text-xs text-muted-foreground"><Shield className="size-4 text-sky-400" />Armor & carry</div><p className="my-2 text-2xl font-semibold tabular-nums">{number(character.armor, 1)}<span className="ml-2 text-xs font-normal text-muted-foreground">armor</span></p><p className="text-xs text-muted-foreground">Carrying <span className="font-medium text-foreground">{number(character.weight, 1)}</span> · {snapshot.items.length} occupied slots</p></div></div>
          <Tabs defaultValue="inventory"><div className="flex flex-wrap items-center justify-between gap-3"><TabsList><TabsTrigger value="inventory">Inventory</TabsTrigger><TabsTrigger value="equipment">Equipment</TabsTrigger><TabsTrigger value="skills">Skills</TabsTrigger></TabsList><div className="relative max-w-xs"><Search className="absolute left-2.5 top-2.5 size-4 text-muted-foreground" /><Input aria-label="Filter inventory or skills" value={query} onChange={event => setQuery(event.target.value)} className="h-9 pl-9" placeholder="Find item, prefab or skill…" /></div></div>
            <TabsContent value="inventory" className="pt-3"><div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_280px]"><section className="min-w-0 rounded-xl border bg-card"><div className="flex items-center justify-between border-b px-4 py-3 text-xs"><h3 className="font-semibold">Backpack <span className="ml-1 font-normal text-muted-foreground">{width} × {height}</span></h3><span className="text-muted-foreground">Outlined slots changed</span></div><div className="overflow-x-auto p-3 sm:p-4"><div role="group" aria-label="Inventory slots" className="grid min-w-[400px] gap-1.5" style={{ gridTemplateColumns: `repeat(${width}, minmax(44px, 1fr))` }}>{Array.from({ length: width * height }, (_, i) => { const x = i % width, y = Math.floor(i / width), id = `${x}:${y}`, entry = slots.get(id), icon = image(entry && snapshot.icons[entry.iconKey]); return <button key={id} title={entry ? `${entry.name} ×${entry.stack}` : "Empty slot"} aria-label={`Slot ${x + 1}, ${y + 1}: ${entry ? `${entry.name}, ${entry.stack}` : "empty"}`} aria-pressed={id === slot} onClick={() => setSlot(id)} className={cn("relative aspect-square rounded-lg border bg-muted/20 p-1.5 transition-colors hover:bg-muted/50 focus-visible:outline-2 focus-visible:outline-primary", changed.has(id) && "border-amber-300/80", slot === id && "border-primary bg-primary/10 ring-1 ring-primary", entry && !filtered(entry) && "opacity-20")}><span className="absolute left-1.5 top-1 text-[9px] text-muted-foreground/60">{y === 0 ? x + 1 : ""}</span>{entry && <>{icon ? <img src={icon} alt="" className="size-full object-contain" /> : <Backpack className="m-auto size-6 text-muted-foreground" />}{entry.equipped && <span className="absolute right-1 top-1 rounded bg-primary px-1 text-[8px] font-bold text-primary-foreground">E</span>}<span className="absolute bottom-1 right-1.5 text-[11px] font-semibold tabular-nums text-foreground [text-shadow:0_1px_3px_black]">{entry.stack > 1 ? entry.stack : ""}</span>{entry.maxDurability > 0 && <div className="absolute bottom-0 left-1 right-1 h-0.5 bg-muted"><div className="h-full bg-primary/80" style={{ width: `${Math.max(0, Math.min(100, finite(entry.durability) / entry.maxDurability * 100))}%` }} /></div>}</>}</button> })}</div></div><div className="border-t px-4 py-3 text-[11px] text-muted-foreground">First row is the hotbar. <span className="font-semibold text-primary">E</span> marks equipped items. Select a slot to inspect its contents.</div></section>
              <section className="rounded-xl border bg-card p-4"><h3 className="mb-4 text-xs font-semibold uppercase tracking-wider text-muted-foreground">Item detail</h3>{item ? <><div className="mb-3 flex items-center gap-3"><div className="grid size-16 shrink-0 place-items-center rounded-xl border bg-muted/25">{image(snapshot.icons[item.iconKey]) ? <img src={image(snapshot.icons[item.iconKey])} alt="" className="size-14 object-contain" /> : <Backpack className="size-6" />}</div><div className="min-w-0"><h4 className="break-words font-semibold">{item.name}</h4><p className="mt-1 text-xs text-muted-foreground">{item.type}</p></div></div><p className="mb-4 break-all rounded-md bg-muted/30 p-2 font-mono text-[10px] text-muted-foreground">{item.prefab}</p><div className="mb-4 flex flex-wrap gap-1">{item.equipped && <Badge variant="secondary">Equipped</Badge>}{!item.teleportable && <Badge variant="destructive">No portal</Badge>}</div><dl className="space-y-2.5 text-xs">{[["Stack", `${item.stack} / ${item.maxStack}`], ["Quality", `${item.quality} / ${item.maxQuality}`], ["Durability", item.maxDurability > 0 ? `${number(item.durability)} / ${number(item.maxDurability)}` : "Not applicable"], ["Weight", number(item.weight, 1)], ["Variant", String(item.variant)], ["Crafter", item.crafterName || "—"], ["Slot", `${item.x + 1}, ${item.y + 1}`]].map(([label, value]) => <div key={label} className="flex justify-between gap-3"><dt className="text-muted-foreground">{label}</dt><dd className="break-words text-right tabular-nums">{value}</dd></div>)}</dl>{item.description && <p className="mt-4 border-t pt-3 text-xs leading-5 text-muted-foreground">{item.description.replace(/<[^>]+>/g, "")}</p>}</> : <div className="grid min-h-52 place-content-center gap-3 text-center text-sm text-muted-foreground"><Backpack className="mx-auto size-8 opacity-50" /><p>{slot ? "This slot is empty" : "Select an inventory slot"}</p></div>}</section></div></TabsContent>
            <TabsContent value="equipment" className="pt-3"><div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">{snapshot.items.filter(entry => entry.equipped && filtered(entry)).map(entry => <div key={position(entry)} className="flex items-center gap-3 rounded-xl border bg-card p-4">{image(snapshot.icons[entry.iconKey]) ? <img src={image(snapshot.icons[entry.iconKey])} alt="" className="size-12 object-contain" /> : <Shield className="size-10" />}<div><p className="text-sm font-semibold">{entry.name}</p><p className="mt-1 text-xs text-muted-foreground">Quality {entry.quality} · {entry.maxDurability > 0 ? `${number(entry.durability)} durability` : entry.type}</p></div></div>)}</div>{!snapshot.items.some(entry => entry.equipped && filtered(entry)) && <p className="p-8 text-center text-sm text-muted-foreground">No matching equipped items.</p>}</TabsContent>
            <TabsContent value="skills" className="pt-3"><div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">{character.skills.filter(skill => skill.name.toLowerCase().includes(query.toLowerCase())).sort((a, b) => b.level - a.level).map(skill => <div key={skill.name} className="rounded-xl border bg-card p-4"><div className="mb-3 flex justify-between text-sm"><span>{skill.name}</span><span className="font-mono text-primary">{number(skill.level, 1)}</span></div><div className="h-1 rounded-full bg-muted"><div className="h-full rounded-full bg-primary" style={{ width: `${Math.min(100, Math.max(0, finite(skill.level)))}%` }} /></div></div>)}</div></TabsContent>
          </Tabs>
          <section className="rounded-xl border bg-card"><div className="flex justify-between border-b px-4 py-3"><h3 className="text-xs font-semibold">Session changes</h3><span className="text-[11px] text-muted-foreground">Last 24 · kept in this view only</span></div><div className="max-h-40 overflow-auto px-4 py-2">{changes.length ? changes.map(change => <div key={change.id} className="flex gap-4 border-b border-border/40 py-2 text-xs last:border-0"><time className="shrink-0 font-mono text-muted-foreground">{new Date(change.at).toLocaleTimeString()}</time><span>{change.text}</span></div>) : <p className="py-3 text-xs text-muted-foreground">Changes will appear as items move, stacks change, or equipment is updated.</p>}</div></section>
        </div> : <div className="grid min-h-80 place-content-center gap-3 text-center"><Eye className="mx-auto size-10 text-muted-foreground/40" /><p className="text-sm font-medium">Waiting for character data</p><p className="max-w-sm text-xs leading-5 text-muted-foreground">The player must be online with inventory sharing enabled. Only authenticated administrators can open this view.</p></div>}
      </main>
    </div>
    <footer className="flex shrink-0 flex-wrap justify-between gap-2 border-t px-6 py-3 text-[10px] text-muted-foreground"><span>Live inspection · no inventory writes · no webhook delivery</span><span>{character ? `Character ${character.id}` : "Client-reported data"}{frame?.snapshot ? ` · sample ${new Date(frame.receivedAt).toLocaleTimeString()}` : ""}</span></footer>
  </div>
}
