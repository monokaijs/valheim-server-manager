import React, { FormEvent, useCallback, useEffect, useRef, useState } from "react"
import { createRoot } from "react-dom/client"
import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr"
import {
  Activity, Backpack, Ban, Boxes, ChevronRight, CircleGauge, Clock3, Command, Copy, Download,
  FileUp, HeartPulse, HelpCircle, LogOut, MoreHorizontal, PackageCheck, PlugZap, Plus,
  RadioTower, RefreshCw, ScrollText, Search, Server, Settings, ShieldCheck,
  Check, KeyRound, Shield, Sparkles, SquareTerminal, Trash2, UserPlus, UserRoundSearch, Users, Webhook, Wifi, WifiOff, X,
} from "lucide-react"
import { toast, Toaster } from "sonner"

import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel,
  DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { ScrollArea } from "@/components/ui/scroll-area"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { SidebarInset, SidebarProvider } from "@/components/ui/sidebar"
import { Switch } from "@/components/ui/switch"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { Textarea } from "@/components/ui/textarea"
import { TooltipProvider } from "@/components/ui/tooltip"
import { AppSidebar } from "@/components/app-sidebar"
import { SiteHeader } from "@/components/site-header"
import { ClientNoticePreview } from "@/components/client-notice-preview"
import { cn } from "@/lib/utils"
import { ensureCsrf, post, remove, request } from "./api"
import "./styles.css"

type AuthState = { authenticated: boolean; userName?: string; steamId?: string }
type Status = { status: string; startedAt?: string; uptimeSeconds: number; agentConnected: boolean; agentVersion: string; gameVersion: string; restartRequired: boolean; players: number }
type Player = { peerId: number; name: string; platformId: string; connectedAt: string; ping?: number; companion: boolean; inventoryAllowed: boolean; serverCharacter: boolean }
type Log = { timestamp: string; stream: string; message: string }
type Mod = { id: string; namespace: string; name: string; version: string; source: string; enabled: boolean; protected: boolean }
type ModConfigEntry = { section: string; key: string; value: string; description: string; settingType: string; defaultValue: string; acceptableValues: string; sensitive: boolean; hasValue: boolean }
type ModConfigFile = { file: string; pluginGuid: string; pluginName: string; revision: string; modifiedAt: string; entries: ModConfigEntry[] }
type Hook = { id: string; name: string; url: string; kind: string; secret: string; eventTypes: string[]; template: string; enabled: boolean; allowPrivateNetwork: boolean; consecutiveFailures: number }
type Delivery = { id: string; eventType: string; status: string; attempts: number; createdAt: string; responseStatus?: number; lastError: string }
type Audit = { id: number; occurredAt: string; actor: string; action: string; target: string; result: string; correlationId: string; detail: string }
type InventoryItem = { prefab: string; name: string; description: string; type: string; stack: number; maxStack: number; quality: number; maxQuality: number; durability: number; maxDurability: number; weight: number; equipped: boolean; x: number; y: number; variant: number; crafterName: string; crafterId: string; teleportable: boolean; iconKey: string }
type CharacterSnapshot = {
  ok: boolean
  status: string
  player: string
  capturedAt: string
  character: { id: string; name: string; biome: string; health: number; maxHealth: number; stamina: number; maxStamina: number; eitr: number; maxEitr: number; armor: number; inventoryWidth: number; inventoryHeight: number; weight: number; skills: { name: string; level: number }[] }
  items: InventoryItem[]
  icons: Record<string, string>
}
type ServerCharacter = { platformId: string; characterName: string; fileName: string; size: number; modifiedAt: string; sha256: string }
type CharacterStore = { installed: boolean; importAvailable: boolean; serverStatus: string; characters: ServerCharacter[] }
type ServerAccessSettings = { passwordEnabled: boolean; hasPassword: boolean; publicListing: boolean; serverName: string; port: number }
type ServerCharacterSettings = { enabled: boolean; acceptFirstJoinProfile: boolean; rejectPreviouslyUsedCharacters: boolean; backupsToKeep: number; clientGraceSeconds: number }
type ApiToken = { id: string; name: string; prefix: string; scopes: string[]; createdAt: string; lastUsedAt?: string; revokedAt?: string }
type CreatedApiToken = ApiToken & { token: string }
type JoinRequest = { id: string; platformId: string; playerName: string; status: "pending" | "approved" | "denied"; requestedAt: string; lastAttemptAt: string; attemptCount: number; resolvedAt?: string; resolvedBy?: string }
type AccessLists = { whitelistEnabled: boolean; permitted: string[]; banned: string[]; admins: string[] }
type ServerMessages = { welcome: string; kick: string; ban: string; restart: string; whitelistRejected: string; companionRequired: string }
type ManagerUpdate = { currentVersion: string; latestVersion?: string; updateAvailable: boolean; automaticUpdates: boolean; hostUpdaterAvailable: boolean; state: string; targetVersion?: string; detail: string; lastCheckedAt?: string; releaseUrl?: string }
type PageId = "overview" | "mods" | "players" | "characters" | "access" | "requests" | "webhooks" | "console" | "audit" | "settings"

const navigation: { id: PageId; label: string; icon: React.ElementType; hint: string }[] = [
  { id: "overview", label: "Overview", icon: CircleGauge, hint: "Health & runtime" },
  { id: "mods", label: "Mods", icon: Boxes, hint: "Packages & updates" },
  { id: "players", label: "Players", icon: Users, hint: "Online Vikings" },
  { id: "characters", label: "Characters", icon: UserRoundSearch, hint: "Consent-based inspection" },
  { id: "access", label: "Access", icon: ShieldCheck, hint: "Whitelist & bans" },
  { id: "requests", label: "Join requests", icon: UserPlus, hint: "Approve new Vikings" },
  { id: "webhooks", label: "Webhooks", icon: Webhook, hint: "Event delivery" },
  { id: "console", label: "Console", icon: SquareTerminal, hint: "Live server output" },
  { id: "audit", label: "Audit log", icon: ScrollText, hint: "Administrative history" },
  { id: "settings", label: "Settings", icon: Settings, hint: "Privacy & downloads" },
]

const pageCopy: Record<PageId, { eyebrow: string; title: string; description: string }> = {
  overview: { eyebrow: "Realm overview", title: "The hearth", description: "Live health and control for your dedicated server." },
  mods: { eyebrow: "Package control", title: "Mods", description: "Pinned packages with dependency-aware installs and safe rollback." },
  players: { eyebrow: "Live session", title: "Players", description: "Connected Vikings and server-authoritative moderation." },
  characters: { eyebrow: "Live character data", title: "Characters", description: "Inspect opted-in online characters, equipment, skills, and inventories." },
  access: { eyebrow: "Realm security", title: "Access", description: "Whitelist, bans, and vanilla administrator identities." },
  requests: { eyebrow: "Admission queue", title: "Join requests", description: "Review players rejected by the active whitelist and approve their exact platform identity." },
  webhooks: { eyebrow: "Automations", title: "Webhooks", description: "Signed, filtered event delivery with retries and history." },
  console: { eyebrow: "Observability", title: "Live console", description: "Streaming output and a deliberately small safe-command surface." },
  audit: { eyebrow: "Accountability", title: "Audit log", description: "Every administrative action, target, result, and correlation ID." },
  settings: { eyebrow: "Configuration", title: "Settings", description: "Deployment facts, privacy defaults, and the Server Manager package." },
}

function useLoad<T>(path: string, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null)
  const [error, setError] = useState("")
  const load = useCallback(async () => {
    try {
      setData(await request<T>(path))
      setError("")
    } catch (cause) {
      setError((cause as Error).message)
    }
  }, [path, ...deps])
  useEffect(() => { void load() }, [load])
  return { data, error, load, setData }
}

function ErrorAlert({ text }: { text?: string }) {
  if (!text) return null
  return <Alert variant="destructive"><Ban /><AlertTitle>Request failed</AlertTitle><AlertDescription>{text}</AlertDescription></Alert>
}

function PageHeader({ page, actions }: { page: PageId; actions?: React.ReactNode }) {
  const copy = pageCopy[page]
  return (
    <header className="flex flex-col gap-4 border-b border-border/60 pb-6 sm:flex-row sm:items-end sm:justify-between">
      <div className="max-w-2xl">
        <p className="mb-2 text-[11px] font-semibold uppercase tracking-[0.2em] text-primary">{copy.eyebrow}</p>
        <h1 className="font-heading text-3xl font-semibold tracking-tight text-foreground sm:text-4xl">{copy.title}</h1>
        <p className="mt-2 text-sm text-muted-foreground sm:text-base">{copy.description}</p>
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </header>
  )
}

function EmptyState({ icon: Icon = HelpCircle, title, detail }: { icon?: React.ElementType; title: string; detail: string }) {
  return (
    <div className="flex min-h-44 flex-col items-center justify-center gap-2 px-6 text-center">
      <div className="grid size-10 place-items-center rounded-xl border bg-muted/40"><Icon className="size-4 text-muted-foreground" /></div>
      <p className="text-sm font-medium">{title}</p>
      <p className="max-w-sm text-xs text-muted-foreground">{detail}</p>
    </div>
  )
}

function Auth() {
  const reason = new URLSearchParams(window.location.search).get("authError")
  const error = reason === "not_admin"
    ? "This Steam account is not listed in the server admin list."
    : reason === "verification_failed" ? "Steam could not verify this login. Please try again." : ""
  return (
    <main className="relative grid min-h-svh place-items-center overflow-hidden bg-background p-6">
      <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(circle_at_50%_20%,rgba(105,170,112,.16),transparent_34%)]" />
      <div className="pointer-events-none absolute inset-0 opacity-[.045] [background-image:linear-gradient(rgba(255,255,255,.35)_1px,transparent_1px),linear-gradient(90deg,rgba(255,255,255,.35)_1px,transparent_1px)] [background-size:40px_40px]" />
      <Card className="relative w-full max-w-md border-primary/15 bg-card/90 shadow-2xl shadow-black/40 backdrop-blur-xl">
        <CardHeader className="items-center px-8 pt-7 text-center">
          <div className="mb-3 grid size-12 place-items-center rounded-2xl bg-primary text-xl font-semibold text-primary-foreground shadow-lg shadow-primary/20">ᛉ</div>
          <Badge variant="outline" className="mb-2 border-primary/20 text-primary">Valheim control plane</Badge>
          <CardTitle className="text-2xl">Return to the hall</CardTitle>
          <CardDescription className="max-w-xs leading-6">Use a Steam account listed in this server&apos;s <code className="rounded bg-muted px-1.5 py-0.5 text-xs">adminlist.txt</code>.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-5 px-8 pb-8">
          {error && <Alert variant="destructive"><Ban /><AlertTitle>Access denied</AlertTitle><AlertDescription>{error}</AlertDescription></Alert>}
          <Button asChild size="lg" className="h-11 w-full bg-[#1b2838] text-white hover:bg-[#24394f]">
            <a href="/api/v1/auth/steam"><span className="text-lg">◉</span> Sign in through Steam</a>
          </Button>
          <div className="flex items-center justify-center gap-2 text-xs text-muted-foreground"><ShieldCheck className="size-3.5" /> Steam verifies identity; the manager stores no Steam password.</div>
        </CardContent>
      </Card>
    </main>
  )
}

function Overview({ status, refresh }: { status: Status; refresh: () => void }) {
  const action = async (name: "start" | "stop" | "restart") => {
    const promise = post(`/api/v1/server/${name}`).then(() => window.setTimeout(refresh, 500))
    toast.promise(promise, { loading: `${name[0].toUpperCase() + name.slice(1)}ing server…`, success: `Server ${name} requested`, error: (e) => (e as Error).message })
  }
  const stats = [
    { label: "Server", value: status.status, detail: status.agentConnected ? "Agent handshake healthy" : "Waiting for agent", icon: Server, good: status.status === "running" },
    { label: "Online", value: String(status.players), detail: "Players connected", icon: Users, good: true },
    { label: "Uptime", value: formatDuration(status.uptimeSeconds), detail: "Since last process start", icon: Clock3, good: true },
    { label: "Changes", value: status.restartRequired ? "Pending" : "Clean", detail: status.restartRequired ? "Restart required" : "No staged changes", icon: PackageCheck, good: !status.restartRequired },
  ]
  return <div className="space-y-6">
    <PageHeader page="overview" actions={<><Button variant="outline" onClick={() => action("restart")}><RefreshCw /> Restart</Button><Button onClick={() => action(status.status === "running" ? "stop" : "start")}>{status.status === "running" ? "Stop server" : "Start server"}</Button></>} />
    <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">{stats.map(({ label, value, detail, icon: Icon, good }) => <Card key={label} size="sm" className="bg-card/70"><CardHeader><CardDescription>{label}</CardDescription><CardAction><div className="grid size-8 place-items-center rounded-lg bg-muted"><Icon className={cn("size-4", good ? "text-primary" : "text-amber-300")} /></div></CardAction><CardTitle className="text-2xl capitalize tabular-nums">{value}</CardTitle></CardHeader><CardContent className="text-xs text-muted-foreground">{detail}</CardContent></Card>)}</div>
    <div className="grid gap-4 lg:grid-cols-[1.1fr_.9fr]">
      <Card><CardHeader><CardTitle className="flex items-center gap-2"><Activity className="size-4 text-primary" /> Runtime</CardTitle><CardDescription>Versions and current process metadata.</CardDescription></CardHeader><CardContent><div className="divide-y divide-border/60 rounded-lg border bg-background/35 px-4">{[["Game build", status.gameVersion || "Waiting for agent"], ["Server agent", status.agentVersion || "—"], ["Started", status.startedAt ? new Date(status.startedAt).toLocaleString() : "—"]].map(([label, value]) => <div className="flex items-center justify-between gap-4 py-3 text-sm" key={label}><span className="text-muted-foreground">{label}</span><span className="text-right font-mono text-xs">{value}</span></div>)}</div></CardContent></Card>
      <Card className="relative overflow-hidden border-primary/15 bg-gradient-to-br from-primary/[.09] to-card"><div className="pointer-events-none absolute -bottom-14 -right-4 text-[11rem] leading-none text-primary/[.035]">ᚱ</div><CardHeader><Badge variant="outline" className="mb-2 border-primary/20 text-primary">Safe operations</Badge><CardTitle>Changes land carefully</CardTitle><CardDescription className="max-w-md leading-6">Mod changes stay staged until you apply them. The manager saves the world, snapshots plugins, restarts, and rolls back if the agent does not return.</CardDescription></CardHeader><CardContent className="flex items-center gap-2 text-xs text-muted-foreground"><ShieldCheck className="size-4 text-primary" /> World state is saved before managed restarts.</CardContent></Card>
    </div>
  </div>
}

type InspectorState = { player: Player; snapshot: CharacterSnapshot | null; loading: boolean; error: string } | null

function CharacterDialog({ state, onClose }: { state: InspectorState; onClose: () => void }) {
  const [selected, setSelected] = useState(0)
  useEffect(() => { setSelected(0) }, [state?.snapshot?.capturedAt])
  const snapshot = state?.snapshot
  const item = snapshot?.items[selected]
  const character = snapshot?.character
  const slots = character ? Array.from({ length: character.inventoryWidth * character.inventoryHeight }) : []
  const stats = character ? [
    { label: "Health", value: `${Math.round(character.health)} / ${Math.round(character.maxHealth)}`, icon: HeartPulse, color: "text-red-300" },
    { label: "Stamina", value: `${Math.round(character.stamina)} / ${Math.round(character.maxStamina)}`, icon: Activity, color: "text-amber-300" },
    { label: "Eitr", value: `${Math.round(character.eitr)} / ${Math.round(character.maxEitr)}`, icon: Sparkles, color: "text-violet-300" },
    { label: "Armor", value: character.armor.toFixed(1), icon: Shield, color: "text-sky-300" },
  ] : []
  return (
    <Dialog open={state !== null} onOpenChange={open => !open && onClose()}>
      <DialogContent className="max-h-[92vh] overflow-hidden p-0 sm:max-w-5xl">
        <DialogHeader className="border-b px-6 py-5">
          <div className="flex flex-wrap items-center gap-2">
            <DialogTitle>{state?.player.name || "Character"}</DialogTitle>
            <Badge variant="secondary">Live snapshot</Badge>
            <Badge variant="outline">Not persisted</Badge>
          </div>
          <DialogDescription>{snapshot ? `Captured ${new Date(snapshot.capturedAt).toLocaleString()} from the opted-in client runtime.` : "Requesting a consent-based snapshot from the connected player."}</DialogDescription>
        </DialogHeader>
        {state?.loading && <div className="grid min-h-[420px] place-items-center"><div className="flex items-center gap-3 text-sm text-muted-foreground"><RefreshCw className="size-4 animate-spin" /> Reading character data and item icons…</div></div>}
        {state?.error && <div className="p-6"><Alert variant="destructive"><Ban /><AlertTitle>Character unavailable</AlertTitle><AlertDescription>{state.error}</AlertDescription></Alert></div>}
        {snapshot && character && <ScrollArea className="max-h-[calc(92vh-110px)]">
          <div className="space-y-5 p-6">
            <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">{stats.map(({ label, value, icon: Icon, color }) => <div key={label} className="rounded-xl border bg-muted/20 p-3"><div className="flex items-center gap-2 text-xs text-muted-foreground"><Icon className={cn("size-3.5", color)} />{label}</div><p className="mt-1 text-lg font-semibold tabular-nums">{value}</p></div>)}</div>
            <div className="flex flex-wrap gap-2 text-xs"><Badge variant="outline">Biome: {character.biome}</Badge><Badge variant="outline">Carry: {character.weight.toFixed(1)}</Badge><Badge variant="outline">Character ID: {character.id}</Badge></div>
            <Tabs defaultValue="inventory">
              <TabsList><TabsTrigger value="inventory">Inventory ({snapshot.items.length})</TabsTrigger><TabsTrigger value="equipment">Equipment</TabsTrigger><TabsTrigger value="skills">Skills</TabsTrigger></TabsList>
              <TabsContent value="inventory" className="pt-3"><div className="grid gap-4 xl:grid-cols-[1fr_280px]">
                <div className="overflow-x-auto rounded-xl border bg-black/20 p-3"><div className="mx-auto grid min-w-[420px] gap-1" style={{ gridTemplateColumns: `repeat(${character.inventoryWidth}, minmax(48px, 1fr))` }}>{slots.map((_, slot) => {
                  const x = slot % character.inventoryWidth
                  const y = Math.floor(slot / character.inventoryWidth)
                  const slotItem = snapshot.items.find(candidate => candidate.x === x && candidate.y === y)
                  const index = slotItem ? snapshot.items.indexOf(slotItem) : -1
                  return <button key={slot} disabled={!slotItem} onClick={() => setSelected(index)} className={cn("relative aspect-square min-h-12 overflow-hidden rounded-md border bg-muted/25 p-1 transition", slotItem && "hover:border-primary/60 hover:bg-muted/50", index === selected && "border-primary ring-1 ring-primary")}>
                    {slotItem && <>{snapshot.icons[slotItem.iconKey] ? <img src={snapshot.icons[slotItem.iconKey]} alt="" className="size-full object-contain [image-rendering:auto]" /> : <Backpack className="m-auto size-5 text-muted-foreground" />}{slotItem.stack > 1 && <span className="absolute bottom-0.5 right-1 text-[10px] font-semibold text-white drop-shadow">{slotItem.stack}</span>}{slotItem.equipped && <span className="absolute left-1 top-1 size-1.5 rounded-full bg-primary shadow-[0_0_5px_currentColor]" />}</>}
                  </button>
                })}</div></div>
                <ItemDetail item={item} icon={item ? snapshot.icons[item.iconKey] : undefined} />
              </div></TabsContent>
              <TabsContent value="equipment" className="pt-3"><div className="grid gap-3 sm:grid-cols-2">{snapshot.items.filter(entry => entry.equipped).map(entry => <ItemRow key={`${entry.x}:${entry.y}`} item={entry} icon={snapshot.icons[entry.iconKey]} />)}{snapshot.items.every(entry => !entry.equipped) && <EmptyState icon={Shield} title="Nothing equipped" detail="The character reported no equipped items." />}</div></TabsContent>
              <TabsContent value="skills" className="pt-3"><div className="grid gap-3 sm:grid-cols-2">{character.skills.map(skill => <div key={skill.name} className="rounded-lg border bg-muted/20 p-3"><div className="mb-2 flex items-center justify-between text-sm"><span>{skill.name}</span><span className="font-mono text-xs text-muted-foreground">{skill.level.toFixed(1)}</span></div><div className="h-1.5 overflow-hidden rounded-full bg-muted"><div className="h-full rounded-full bg-primary" style={{ width: `${Math.min(skill.level, 100)}%` }} /></div></div>)}</div></TabsContent>
            </Tabs>
          </div>
        </ScrollArea>}
      </DialogContent>
    </Dialog>
  )
}

function ItemRow({ item, icon }: { item: InventoryItem; icon?: string }) {
  return <div className="flex items-center gap-3 rounded-lg border bg-muted/20 p-3"><div className="grid size-12 shrink-0 place-items-center rounded-md bg-black/20">{icon ? <img src={icon} alt="" className="size-11 object-contain" /> : <Backpack className="size-5 text-muted-foreground" />}</div><div className="min-w-0"><p className="truncate text-sm font-medium">{item.name}</p><p className="text-xs text-muted-foreground">{item.type} · Quality {item.quality}{item.stack > 1 ? ` · ${item.stack} stacked` : ""}</p></div></div>
}

function ItemDetail({ item, icon }: { item?: InventoryItem; icon?: string }) {
  if (!item) return <div className="rounded-xl border bg-muted/15"><EmptyState icon={Backpack} title="Select an item" detail="Choose a filled slot to inspect its metadata." /></div>
  const durability = item.maxDurability > 0 ? Math.max(0, Math.min(100, item.durability / item.maxDurability * 100)) : 0
  return <div className="rounded-xl border bg-muted/15 p-4"><div className="flex items-start gap-3"><div className="grid size-16 shrink-0 place-items-center rounded-lg bg-black/20">{icon ? <img src={icon} alt="" className="size-14 object-contain" /> : <Backpack className="size-6 text-muted-foreground" />}</div><div className="min-w-0"><p className="font-semibold">{item.name}</p><p className="font-mono text-[10px] text-muted-foreground">{item.prefab}</p><div className="mt-2 flex flex-wrap gap-1"><Badge variant="outline">{item.type}</Badge>{item.equipped && <Badge variant="secondary">Equipped</Badge>}{!item.teleportable && <Badge variant="destructive">No portal</Badge>}</div></div></div>{item.description && <p className="mt-4 text-xs leading-5 text-muted-foreground">{item.description}</p>}<div className="mt-4 space-y-2 border-t pt-3 text-xs">{[["Stack", `${item.stack} / ${item.maxStack}`], ["Quality", `${item.quality} / ${item.maxQuality}`], ["Weight", item.weight.toFixed(1)], ["Variant", String(item.variant)], ["Crafter", item.crafterName || "—"]].map(([label, value]) => <div key={label} className="flex justify-between gap-3"><span className="text-muted-foreground">{label}</span><span>{value}</span></div>)}{item.maxDurability > 0 && <div className="pt-1"><div className="mb-1 flex justify-between"><span className="text-muted-foreground">Durability</span><span>{Math.round(durability)}%</span></div><div className="h-1.5 overflow-hidden rounded-full bg-muted"><div className="h-full rounded-full bg-primary" style={{ width: `${durability}%` }} /></div></div>}</div></div>
}

async function loadCharacter(player: Player, setState: (state: InspectorState) => void) {
  setState({ player, snapshot: null, loading: true, error: "" })
  try { setState({ player, snapshot: await post<CharacterSnapshot>(`/api/v1/players/${player.peerId}/inventory`), loading: false, error: "" }) }
  catch (cause) { setState({ player, snapshot: null, loading: false, error: (cause as Error).message }) }
}

function PlayersPage() {
  const { data: players, error, load } = useLoad<Player[]>("/api/v1/players")
  const [inspector, setInspector] = useState<InspectorState>(null)
  const [moderation, setModeration] = useState<{ player: Player; action: "kick" | "ban" } | null>(null)
  const [reason, setReason] = useState("")
  const [moderating, setModerating] = useState(false)
  const action = async () => {
    if (!moderation) return
    setModerating(true)
    try {
      await post(`/api/v1/players/${moderation.player.peerId}/${moderation.action}`, { reason })
      toast.success(`${moderation.player.name}: ${moderation.action} scheduled`)
      setModeration(null); setReason(""); window.setTimeout(() => void load(), 3500)
    } catch (cause) { toast.error((cause as Error).message) } finally { setModerating(false) }
  }
  return <div className="space-y-6"><PageHeader page="players" actions={<Button variant="outline" onClick={() => load()}><RefreshCw /> Refresh</Button>} /><ErrorAlert text={error} />
    <Card className="overflow-hidden py-0"><Table><TableHeader><TableRow><TableHead>Player</TableHead><TableHead>Platform ID</TableHead><TableHead>Ping</TableHead><TableHead>Client runtime</TableHead><TableHead className="w-12" /></TableRow></TableHeader><TableBody>{players?.map(player => <TableRow key={player.peerId}><TableCell><div className="font-medium">{player.name}</div><div className="text-xs text-muted-foreground">Since {new Date(player.connectedAt).toLocaleTimeString()}</div></TableCell><TableCell className="font-mono text-xs">{player.platformId || "unknown"}</TableCell><TableCell className="tabular-nums">{player.ping ?? "—"} ms</TableCell><TableCell><Badge variant={player.companion ? "secondary" : "outline"}>{player.companion ? player.inventoryAllowed ? "Sharing enabled" : "Permission off" : "Not installed"}</Badge></TableCell><TableCell><DropdownMenu><DropdownMenuTrigger asChild><Button variant="ghost" size="icon-sm"><MoreHorizontal /><span className="sr-only">Player actions</span></Button></DropdownMenuTrigger><DropdownMenuContent align="end"><DropdownMenuLabel>{player.name}</DropdownMenuLabel><DropdownMenuItem disabled={!player.companion || !player.inventoryAllowed} onClick={() => loadCharacter(player, setInspector)}><UserRoundSearch /> Inspect character</DropdownMenuItem><DropdownMenuSeparator /><DropdownMenuItem onClick={() => setModeration({ player, action: "kick" })}><LogOut /> Kick with reason</DropdownMenuItem><DropdownMenuItem variant="destructive" onClick={() => setModeration({ player, action: "ban" })}><Ban /> Ban with reason</DropdownMenuItem></DropdownMenuContent></DropdownMenu></TableCell></TableRow>)}</TableBody></Table>{players?.length === 0 && <EmptyState icon={Users} title="The realm is quiet" detail="Connected players will appear here in real time." />}</Card>
    <CharacterDialog state={inspector} onClose={() => setInspector(null)} />
    <Dialog open={moderation !== null} onOpenChange={open => { if (!open) { setModeration(null); setReason("") } }}><DialogContent className="sm:max-w-lg"><DialogHeader><DialogTitle className="capitalize">{moderation?.action} {moderation?.player.name}</DialogTitle><DialogDescription>The reason is rendered through the configured message template, shown to compatible clients before disconnect, and recorded in audit/events.</DialogDescription></DialogHeader><div className="space-y-2"><Label htmlFor="moderation-reason">Reason</Label><Textarea id="moderation-reason" value={reason} onChange={event => setReason(event.target.value)} maxLength={300} placeholder="No reason provided." /><p className="text-right text-[11px] text-muted-foreground">{reason.length} / 300</p></div><div className="flex justify-end gap-2"><Button variant="outline" onClick={() => setModeration(null)}>Cancel</Button><Button variant={moderation?.action === "ban" ? "destructive" : "default"} disabled={moderating} onClick={action}>{moderating ? <RefreshCw className="animate-spin" /> : moderation?.action === "ban" ? <Ban /> : <LogOut />}{moderating ? "Sending…" : moderation?.action === "ban" ? "Ban player" : "Kick player"}</Button></div></DialogContent></Dialog>
  </div>
}

function CharactersPage() {
  const { data: players, error, load } = useLoad<Player[]>("/api/v1/players")
  const { data: store, error: storeError, load: loadStore } = useLoad<CharacterStore>("/api/v1/characters")
  const [inspector, setInspector] = useState<InspectorState>(null)
  const [ownerId, setOwnerId] = useState("")
  const [profile, setProfile] = useState<File | null>(null)
  const [overwrite, setOverwrite] = useState(false)
  const [importing, setImporting] = useState(false)
  const importProfile = async (event: FormEvent) => {
    event.preventDefault(); if (!profile) return
    const body = new FormData(); body.append("profile", profile); body.append("platformId", ownerId); body.append("overwrite", String(overwrite))
    setImporting(true)
    try { const result = await request<ServerCharacter>("/api/v1/characters/import", { method: "POST", body }); toast.success(`${result.characterName} imported for ${result.platformId}`); setProfile(null); await loadStore() }
    catch (cause) { toast.error((cause as Error).message) }
    finally { setImporting(false) }
  }
  const stopForImport = async () => { try { await post("/api/v1/server/stop"); toast.success("Server stopped; character import is now available"); await loadStore() } catch (cause) { toast.error((cause as Error).message) } }
  return <div className="space-y-6"><PageHeader page="characters" actions={<Button variant="outline" onClick={() => { void load(); void loadStore() }}><RefreshCw /> Refresh</Button>} /><ErrorAlert text={error || storeError} />
    <Alert><ShieldCheck /><AlertTitle>Server-owned native profiles</AlertTitle><AlertDescription>The Server Manager client runtime loads the server&apos;s authoritative <code>.fch</code> before spawn and checkpoints it back during play. Dashboard inspection still requires explicit inventory-sharing consent; inspection snapshots and icons are never persisted or forwarded to webhooks.</AlertDescription></Alert>
    <div className="grid gap-4 xl:grid-cols-[1fr_1.1fr]">
      <Card><CardHeader><CardTitle className="flex items-center gap-2"><FileUp className="size-4 text-primary" /> Import existing character</CardTitle><CardDescription>Migrate a native Valheim <code>.fch</code> save into VSM server-owned characters. The full profile—including inventory, equipment, skills, and progression—is preserved.</CardDescription></CardHeader><CardContent>
        {!store?.installed ? <Alert variant="destructive"><Ban /><AlertTitle>VSM character agent is unavailable</AlertTitle><AlertDescription>Reapply the protected Valheim Server Manager server agent before importing.</AlertDescription></Alert> :
        !store.importAvailable ? <Alert><Server /><AlertTitle>Stop the server first</AlertTitle><AlertDescription className="space-y-3"><span className="block">Imports are atomic and only allowed while Valheim is stopped, preventing the mod&apos;s in-memory character from overwriting the migrated file.</span><Button type="button" size="sm" variant="outline" onClick={stopForImport}>Save & stop server</Button></AlertDescription></Alert> : null}
        <form onSubmit={importProfile} className="mt-4 space-y-4"><div className="space-y-1.5"><Label htmlFor="character-owner">Steam owner</Label><Input id="character-owner" value={ownerId} onChange={event => setOwnerId(event.target.value)} placeholder="76561198000000000 or Steam_…" required /></div><div className="space-y-1.5"><Label htmlFor="character-file">Native character save</Label><Input id="character-file" type="file" accept=".fch" onChange={event => setProfile(event.target.files?.[0] || null)} required /><p className="text-[11px] text-muted-foreground">The filename must match the in-game character name, for example <code>MyHero.fch</code>. Maximum 2 MiB.</p></div><div className="flex items-center justify-between rounded-lg border p-3"><div><p className="text-sm font-medium">Replace existing profile</p><p className="text-xs text-muted-foreground">The current server copy is backed up before replacement.</p></div><Switch checked={overwrite} onCheckedChange={setOverwrite} /></div><Button className="w-full" disabled={!store?.installed || !store.importAvailable || !profile || !ownerId || importing}><FileUp />{importing ? "Importing…" : "Import character"}</Button></form>
      </CardContent></Card>
      <Card className="overflow-hidden"><CardHeader><CardTitle>Server-owned profiles</CardTitle><CardDescription>Native saves owned by the VSM character agent in <code>characters_local</code>.</CardDescription><CardAction><Badge variant="outline">{store?.characters.length || 0}</Badge></CardAction></CardHeader><CardContent className="space-y-2">{store?.characters.map(character => <div key={character.fileName} className="flex items-center gap-3 rounded-lg border bg-muted/20 p-3"><div className="grid size-10 shrink-0 place-items-center rounded-lg bg-primary/10"><UserRoundSearch className="size-4 text-primary" /></div><div className="min-w-0 flex-1"><p className="truncate text-sm font-medium">{character.characterName}</p><p className="truncate font-mono text-[10px] text-muted-foreground">{character.platformId} · {(character.size / 1024).toFixed(1)} KiB</p></div><time className="hidden text-[10px] text-muted-foreground sm:block">{new Date(character.modifiedAt).toLocaleDateString()}</time></div>)}{store?.characters.length === 0 && <EmptyState icon={UserRoundSearch} title="No server profiles" detail="Stop Valheim and import each player's native .fch save to migrate the existing realm." />}</CardContent></Card>
    </div>
    <div><h2 className="text-base font-semibold">Online character inspection</h2><p className="mt-1 text-sm text-muted-foreground">Live dashboard snapshots remain transient even though the native gameplay profile is server-owned.</p></div>
    <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-3">{players?.map(player => <Card key={player.peerId} className="overflow-hidden"><CardHeader><div className="mb-2 grid size-10 place-items-center rounded-xl bg-primary/10 text-primary"><UserRoundSearch className="size-5" /></div><CardTitle>{player.name}</CardTitle><CardDescription className="font-mono text-xs">{player.platformId || "Unknown platform ID"}</CardDescription><CardAction><div className="flex gap-1"><Badge variant={player.serverCharacter ? "secondary" : "destructive"}>{player.serverCharacter ? "Server-owned" : "Local"}</Badge><Badge variant={player.inventoryAllowed ? "secondary" : "outline"}>{player.inventoryAllowed ? "Inspectable" : "Private"}</Badge></div></CardAction></CardHeader><CardContent className="space-y-3"><div className="flex justify-between text-xs text-muted-foreground"><span>Connected</span><span>{new Date(player.connectedAt).toLocaleTimeString()}</span></div><div className="flex justify-between text-xs text-muted-foreground"><span>Latency</span><span>{player.ping ?? "—"} ms</span></div><Button className="w-full" disabled={!player.companion || !player.inventoryAllowed} onClick={() => loadCharacter(player, setInspector)}><UserRoundSearch /> Inspect live character</Button></CardContent></Card>)}{players?.length === 0 && <Card className="sm:col-span-2 xl:col-span-3"><EmptyState icon={UserRoundSearch} title="No online characters" detail="A connected, opted-in player will appear here for live inspection." /></Card>}</div>
    <CharacterDialog state={inspector} onClose={() => setInspector(null)} />
  </div>
}

function AccessListCard({ title, detail, kind, items, onMutate }: { title: string; detail: string; kind: string; items: string[]; onMutate: (kind: string, action: "add" | "remove", id: string) => Promise<void> }) {
  const [value, setValue] = useState("")
  return <Card><CardHeader><CardTitle>{title}</CardTitle><CardDescription>{detail}</CardDescription></CardHeader><CardContent className="space-y-4"><div className="flex min-h-20 flex-wrap content-start gap-2">{items.map(id => <Badge key={id} variant="outline" className="h-7 gap-1.5 rounded-md font-mono text-[11px]">{id}<button aria-label={`Remove ${id}`} className="ml-1 text-muted-foreground hover:text-destructive" onClick={() => onMutate(kind, "remove", id)}>×</button></Badge>)}{items.length === 0 && <p className="text-xs text-muted-foreground">No entries.</p>}</div><div className="flex gap-2"><Input value={value} onChange={event => setValue(event.target.value)} placeholder="Steam_7656119…" onKeyDown={event => { if (event.key === "Enter" && value) void onMutate(kind, "add", value).then(() => setValue("")) }} /><Button size="icon" disabled={!value} onClick={() => onMutate(kind, "add", value).then(() => setValue(""))}><Plus /></Button></div></CardContent></Card>
}

function AccessPage() {
  const { data, error, load } = useLoad<AccessLists>("/api/v1/access")
  const mutate = async (kind: string, action: "add" | "remove", id: string) => {
    try {
      if (action === "add") await post(`/api/v1/access/${kind}/`, { platformId: id })
      else await remove(`/api/v1/access/${kind}/${encodeURIComponent(id)}`)
      toast.success(`${id} ${action === "add" ? "added" : "removed"}`)
      await load()
    } catch (cause) { toast.error((cause as Error).message); throw cause }
  }
  return <div className="space-y-6"><PageHeader page="access" actions={<><Badge variant={data?.whitelistEnabled ? "secondary" : "destructive"}>{data?.whitelistEnabled ? "Whitelist active" : "Whitelist disabled"}</Badge><Button variant="outline" onClick={() => load()}><RefreshCw /> Sync lists</Button></>} /><ErrorAlert text={error} />{data && !data.whitelistEnabled && <Alert variant="destructive"><ShieldCheck /><AlertTitle>Admission requests are disabled</AlertTitle><AlertDescription>Add at least one permitted identity to activate Valheim&apos;s whitelist. Until then, everyone may connect and rejected-player requests cannot be created.</AlertDescription></Alert>}<div className="grid gap-4 xl:grid-cols-3">{data && <><AccessListCard title="Whitelist" detail="At least one entry activates admission requests." kind="permitted" items={data.permitted} onMutate={mutate} /><AccessListCard title="Banned" detail="Denied at connection time." kind="banned" items={data.banned} onMutate={mutate} /><AccessListCard title="Game administrators" detail="Also controls dashboard authorization." kind="admin" items={data.admins} onMutate={mutate} /></>}</div></div>
}

function JoinRequestsPage() {
  const { data, error, load } = useLoad<JoinRequest[]>("/api/v1/join-requests?status=all")
  const { data: access } = useLoad<AccessLists>("/api/v1/access")
  const [busy, setBusy] = useState<string | null>(null)
  useEffect(() => { const timer = window.setInterval(() => void load(), 5000); return () => window.clearInterval(timer) }, [load])
  const resolve = async (item: JoinRequest, decision: "approve" | "deny") => {
    setBusy(item.id)
    try {
      await post(`/api/v1/join-requests/${item.id}/${decision}`)
      toast.success(decision === "approve" ? `${item.playerName || item.platformId} can now reconnect` : "Join request denied")
      await load()
    } catch (cause) { toast.error((cause as Error).message) } finally { setBusy(null) }
  }
  const pending = data?.filter(item => item.status === "pending") || []
  const history = data?.filter(item => item.status !== "pending").slice(0, 50) || []
  return <div className="space-y-6"><PageHeader page="requests" actions={<Button variant="outline" onClick={() => load()}><RefreshCw /> Refresh</Button>} /><ErrorAlert text={error} />
    <Alert variant={access && !access.whitelistEnabled ? "destructive" : "default"}><ShieldCheck /><AlertTitle>{access?.whitelistEnabled ? "Whitelist is active" : "Whitelist is disabled"}</AlertTitle><AlertDescription>{access?.whitelistEnabled ? "Every identity rejected by the whitelist gets its own pending request. Reconnect attempts update the same request and attempt count. Banned identities never enter this queue." : "No pending approval can be created while the permitted list is empty. Add at least one identity on the Access page to activate admission control."}</AlertDescription></Alert>
    <Card className="overflow-hidden py-0"><Table><TableHeader><TableRow><TableHead>Player</TableHead><TableHead>Platform identity</TableHead><TableHead>First requested</TableHead><TableHead>Attempts</TableHead><TableHead className="w-52" /></TableRow></TableHeader><TableBody>{pending.map(item => <TableRow key={item.id}><TableCell><div className="font-medium">{item.playerName || "Unknown character"}</div><div className="text-xs text-muted-foreground">Last tried {new Date(item.lastAttemptAt).toLocaleString()}</div></TableCell><TableCell className="font-mono text-xs">{item.platformId}</TableCell><TableCell className="text-xs text-muted-foreground">{new Date(item.requestedAt).toLocaleString()}</TableCell><TableCell><Badge variant="outline">{item.attemptCount}</Badge></TableCell><TableCell><div className="flex justify-end gap-2"><Button size="sm" variant="outline" disabled={busy === item.id} onClick={() => resolve(item, "deny")}><X /> Deny</Button><Button size="sm" disabled={busy === item.id} onClick={() => resolve(item, "approve")}><Check /> Approve</Button></div></TableCell></TableRow>)}</TableBody></Table>{pending.length === 0 && <EmptyState icon={UserPlus} title="No pending requests" detail="A player rejected by the active whitelist will appear here automatically." />}</Card>
    {history.length > 0 && <Card className="overflow-hidden py-0"><Table><TableHeader><TableRow><TableHead>Recent decision</TableHead><TableHead>Platform identity</TableHead><TableHead>Status</TableHead><TableHead>Resolved by</TableHead></TableRow></TableHeader><TableBody>{history.map(item => <TableRow key={item.id}><TableCell><div className="font-medium">{item.playerName || "Unknown character"}</div><div className="text-xs text-muted-foreground">{item.resolvedAt ? new Date(item.resolvedAt).toLocaleString() : "—"}</div></TableCell><TableCell className="font-mono text-xs">{item.platformId}</TableCell><TableCell><Badge variant={item.status === "approved" ? "secondary" : "destructive"}>{item.status}</Badge></TableCell><TableCell className="font-mono text-xs">{item.resolvedBy || "—"}</TableCell></TableRow>)}</TableBody></Table></Card>}
  </div>
}

function groupConfigEntries(entries: ModConfigEntry[]) {
  return entries.reduce<Record<string, ModConfigEntry[]>>((groups, entry) => {
    ;(groups[entry.section] ||= []).push(entry)
    return groups
  }, {})
}

function ModConfigDialog({ mod, onClose, onChanged }: { mod: Mod | null; onClose: () => void; onChanged: () => void }) {
  const [files, setFiles] = useState<ModConfigFile[]>([])
  const [error, setError] = useState("")
  const [loading, setLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [dirty, setDirty] = useState<Record<string, ModConfigEntry>>({})
  const load = useCallback(async () => {
    if (!mod) return
    setLoading(true)
    try { setFiles(await request<ModConfigFile[]>(`/api/v1/mods/${mod.id}/configs`)); setDirty({}); setError("") }
    catch (cause) { setError((cause as Error).message) }
    finally { setLoading(false) }
  }, [mod])
  useEffect(() => { void load() }, [load])
  const entryId = (file: string, entry: ModConfigEntry) => `${file}\0${entry.section}\0${entry.key}`
  const change = (file: string, entry: ModConfigEntry, value: string) => {
    const updated = { ...entry, value }
    setFiles(current => current.map(config => config.file !== file ? config : { ...config, entries: config.entries.map(item => item.section === entry.section && item.key === entry.key ? updated : item) }))
    setDirty(current => ({ ...current, [entryId(file, entry)]: updated }))
  }
  const save = async (restart: boolean) => {
    if (!mod || Object.keys(dirty).length === 0) return
    setSaving(true)
    try {
      for (const file of files) {
        const values = file.entries.filter(entry => dirty[entryId(file.file, entry)]).map(entry => ({ section: entry.section, key: entry.key, value: entry.value }))
        if (values.length) await post(`/api/v1/mods/${mod.id}/configs`, { file: file.file, revision: file.revision, values })
      }
      toast.success(restart ? "Configuration saved; restarting Valheim…" : "Configuration saved; restart required")
      onChanged()
      if (restart) { await post("/api/v1/mods/configs/apply"); toast.success("Valheim restarted with the new configuration"); onClose() }
      else await load()
    } catch (cause) { toast.error((cause as Error).message); setError((cause as Error).message) }
    finally { setSaving(false) }
  }
  return <Dialog open={!!mod} onOpenChange={open => { if (!open && !saving) onClose() }}><DialogContent className="sm:max-w-4xl"><DialogHeader><DialogTitle>{mod ? `${mod.namespace}/${mod.name} configuration` : "Mod configuration"}</DialogTitle><DialogDescription>Only BepInEx configuration files mapped to DLLs owned by this package are available. Sensitive values are never returned by the API.</DialogDescription></DialogHeader><ErrorAlert text={error} />
    {loading ? <div className="flex min-h-48 items-center justify-center gap-2 text-sm text-muted-foreground"><RefreshCw className="size-4 animate-spin" /> Loading configuration…</div> : files.length === 0 ? <EmptyState icon={Settings} title="No configuration discovered" detail="Start the server once after installing this mod so BepInEx can create its configuration file and publish the plugin mapping." /> : <ScrollArea className="max-h-[65vh] pr-4"><div className="space-y-5">{files.map(file => <Card key={file.file} size="sm"><CardHeader><CardTitle className="text-base">{file.pluginName || file.file}</CardTitle><CardDescription className="font-mono text-[11px]">{file.file}{file.pluginGuid ? ` · ${file.pluginGuid}` : ""}</CardDescription></CardHeader><CardContent className="space-y-5">{Object.entries(groupConfigEntries(file.entries)).map(([section, entries]) => <section key={section} className="space-y-3"><div className="border-b pb-2"><h3 className="text-sm font-semibold">{section}</h3></div>{entries?.map(entry => { const id = `${file.file}-${entry.section}-${entry.key}`; const kind = entry.settingType.toLowerCase(); const options = entry.acceptableValues.includes(",") ? entry.acceptableValues.split(",").map(value => value.trim()).filter(Boolean) : []; return <div key={id} className="grid gap-2 rounded-lg border bg-muted/15 p-3 md:grid-cols-[minmax(0,1fr)_minmax(220px,.8fr)] md:items-center"><div><Label htmlFor={id}>{entry.key}</Label>{entry.description && <p className="mt-1 text-xs leading-5 text-muted-foreground">{entry.description}</p>}<p className="mt-1 font-mono text-[10px] text-muted-foreground">{entry.settingType}{entry.defaultValue ? ` · default ${entry.defaultValue}` : ""}{entry.acceptableValues ? ` · ${entry.acceptableValues}` : ""}</p></div>{kind === "boolean" ? <div className="flex items-center justify-end gap-3"><span className="text-xs text-muted-foreground">{entry.value || entry.defaultValue}</span><Switch id={id} checked={(entry.value || entry.defaultValue).toLowerCase() === "true"} onCheckedChange={checked => change(file.file, entry, String(checked))} /></div> : options.length > 0 ? <Select value={entry.value} onValueChange={value => change(file.file, entry, value)}><SelectTrigger id={id} className="w-full"><SelectValue placeholder="Choose a value" /></SelectTrigger><SelectContent>{options.map(option => <SelectItem value={option} key={option}>{option}</SelectItem>)}</SelectContent></Select> : <Input id={id} type={entry.sensitive ? "password" : kind.includes("int") || kind.includes("float") || kind.includes("double") ? "number" : "text"} value={entry.value} placeholder={entry.sensitive && entry.hasValue ? "Value is set; enter to replace" : entry.defaultValue} onChange={event => change(file.file, entry, event.target.value)} />}</div>})}</section>)}</CardContent></Card>)}</div></ScrollArea>}
    <div className="flex flex-col-reverse gap-2 border-t pt-4 sm:flex-row sm:justify-end"><Button variant="outline" disabled={saving} onClick={onClose}>Cancel</Button><Button variant="outline" disabled={saving || Object.keys(dirty).length === 0} onClick={() => save(false)}>{saving ? <RefreshCw className="animate-spin" /> : <Settings />} Save for restart</Button><Button disabled={saving || Object.keys(dirty).length === 0} onClick={() => save(true)}>{saving ? <RefreshCw className="animate-spin" /> : <RefreshCw />} Save & restart</Button></div>
  </DialogContent></Dialog>
}

function ModsPage({ refreshStatus }: { refreshStatus: () => void }) {
  const { data: mods, error, load } = useLoad<Mod[]>("/api/v1/mods")
  const { data: updates, load: loadUpdates } = useLoad<Record<string, string>>("/api/v1/mods/updates")
  const { data: clientSync, load: loadClientSync } = useLoad<Record<string, boolean>>("/api/v1/mods/client-sync")
  const [query, setQuery] = useState("")
  const [results, setResults] = useState<any[]>([])
  const [busy, setBusy] = useState(false)
  const [configMod, setConfigMod] = useState<Mod | null>(null)
  const search = async () => { try { setResults(await request(`/api/v1/mods/search?q=${encodeURIComponent(query)}`)) } catch (cause) { toast.error((cause as Error).message) } }
  const install = async (item: any) => { setBusy(true); try { await post("/api/v1/mods/thunderstore", { namespace: item.namespace, name: item.name, version: item.version }); toast.success(`${item.fullName} staged`); await load(); refreshStatus() } catch (cause) { toast.error((cause as Error).message) } finally { setBusy(false) } }
  const toggle = async (mod: Mod) => { try { await post(`/api/v1/mods/${mod.id}/${mod.enabled ? "disable" : "enable"}`); await load(); refreshStatus() } catch (cause) { toast.error((cause as Error).message) } }
  const upload = async (event: React.ChangeEvent<HTMLInputElement>) => { const file = event.target.files?.[0]; if (!file) return; const body = new FormData(); body.append("package", file); setBusy(true); try { await request("/api/v1/mods/upload", { method: "POST", body }); toast.success(`${file.name} staged`); await load(); refreshStatus() } catch (cause) { toast.error((cause as Error).message) } finally { setBusy(false); event.target.value = "" } }
  const apply = async () => { setBusy(true); try { await post("/api/v1/mods/apply"); toast.success("Restart and health validation started"); refreshStatus() } catch (cause) { toast.error((cause as Error).message) } finally { setBusy(false) } }
  const update = async (mod: Mod) => { const version = updates?.[mod.id]; if (!version) return; setBusy(true); try { await post("/api/v1/mods/thunderstore", { namespace: mod.namespace, name: mod.name, version }); await Promise.all([load(), loadUpdates()]); refreshStatus() } catch (cause) { toast.error((cause as Error).message) } finally { setBusy(false) } }
  const checkUpdates = async () => { setBusy(true); try { await post("/api/v1/mods/updates/check"); await loadUpdates(); toast.success("Thunderstore update check completed") } catch (cause) { toast.error((cause as Error).message) } finally { setBusy(false) } }
  const updateAll = async () => { setBusy(true); try { const result = await post<{ staged: number }>("/api/v1/mods/updates/stage-all"); await Promise.all([load(), loadUpdates()]); refreshStatus(); toast.success(`${result.staged} update${result.staged === 1 ? "" : "s"} staged; review and apply when ready`) } catch (cause) { toast.error((cause as Error).message) } finally { setBusy(false) } }
  const toggleClientSync = async (mod: Mod) => { const required = !(clientSync?.[mod.id] ?? true); try { await post(`/api/v1/mods/${mod.id}/client-sync`, { required }); await loadClientSync(); toast.success(required ? `${mod.name} is now required on clients` : `${mod.name} is now server-only`) } catch (cause) { toast.error((cause as Error).message) } }
  const updateCount = Object.keys(updates || {}).length
  return <div className="space-y-6"><PageHeader page="mods" actions={<><Button variant="outline" disabled={busy} onClick={checkUpdates}><RefreshCw /> Check updates</Button>{updateCount > 0 && <Button variant="outline" disabled={busy} onClick={updateAll}><Download /> Stage all ({updateCount})</Button>}<Button asChild variant="outline"><label><FileUp /> Upload ZIP<input hidden type="file" accept=".zip" onChange={upload} /></label></Button><Button disabled={busy} onClick={apply}><RefreshCw className={cn(busy && "animate-spin")} /> Apply & restart</Button></>} /><ErrorAlert text={error} />
    <Alert><Download /><AlertTitle>One Server Manager install</AlertTitle><AlertDescription>Players install Server Manager once. On connection, this server sends the VSM client runtime and every enabled Thunderstore package marked <strong>Required</strong>. Changed DLLs are activated after one Valheim restart.</AlertDescription></Alert>
    <Card><CardHeader><CardTitle>Thunderstore catalog</CardTitle><CardDescription>Search packages and stage an exact version with its dependency graph.</CardDescription></CardHeader><CardContent><div className="flex gap-2"><div className="relative flex-1"><Search className="absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" /><Input className="pl-9" value={query} onChange={event => setQuery(event.target.value)} onKeyDown={event => event.key === "Enter" && void search()} placeholder="Search Thunderstore packages" /></div><Button variant="outline" onClick={search} disabled={!query}>Search</Button></div>{results.length > 0 && <div className="mt-4 divide-y rounded-lg border">{results.map(item => <button key={item.fullName} onClick={() => install(item)} className="flex w-full items-center justify-between gap-4 p-3 text-left hover:bg-muted/40"><span className="min-w-0"><span className="block truncate text-sm font-medium">{item.fullName}</span><span className="block truncate text-xs text-muted-foreground">{item.description}</span></span><span className="flex shrink-0 items-center gap-2 font-mono text-xs text-muted-foreground">{item.version}<ChevronRight className="size-3.5" /></span></button>)}</div>}</CardContent></Card>
    <Card className="overflow-hidden py-0"><Table><TableHeader><TableRow><TableHead>Package</TableHead><TableHead>Version</TableHead><TableHead>Source</TableHead><TableHead>Clients</TableHead><TableHead>Status</TableHead><TableHead>Config</TableHead><TableHead className="w-12" /></TableRow></TableHeader><TableBody>{mods?.map(mod => { const syncable = mod.source === "thunderstore" && !mod.protected; const required = clientSync?.[mod.id] ?? syncable; return <TableRow key={mod.id}><TableCell><div className="font-medium">{mod.namespace}/{mod.name}</div>{mod.protected && <div className="text-xs text-muted-foreground">Protected system package</div>}</TableCell><TableCell className="font-mono text-xs">{mod.version}{updates?.[mod.id] && <Badge variant="secondary" className="ml-2">{updates[mod.id]} available</Badge>}</TableCell><TableCell>{mod.source}</TableCell><TableCell><div className="flex items-center gap-2"><Switch checked={required} disabled={!syncable} onCheckedChange={() => toggleClientSync(mod)} aria-label={`Require ${mod.name} on clients`} /><span className="text-xs text-muted-foreground">{!syncable ? "Server only" : required ? "Required" : "Server only"}</span></div></TableCell><TableCell><Badge variant={mod.enabled ? "secondary" : "outline"}>{mod.enabled ? "Enabled" : "Disabled"}</Badge></TableCell><TableCell><Button variant="outline" size="sm" disabled={mod.protected} onClick={() => setConfigMod(mod)}><Settings /> Configure</Button></TableCell><TableCell><DropdownMenu><DropdownMenuTrigger asChild><Button variant="ghost" size="icon-sm" disabled={mod.protected}><MoreHorizontal /></Button></DropdownMenuTrigger><DropdownMenuContent align="end"><DropdownMenuLabel>{mod.name}</DropdownMenuLabel>{updates?.[mod.id] && <DropdownMenuItem onClick={() => update(mod)}><Download /> Stage update</DropdownMenuItem>}<DropdownMenuItem onClick={() => toggle(mod)}>{mod.enabled ? <WifiOff /> : <Wifi />}{mod.enabled ? "Disable" : "Enable"}</DropdownMenuItem><DropdownMenuSeparator /><DropdownMenuItem variant="destructive" onClick={async () => { if (confirm(`Remove ${mod.name}?`)) { await remove(`/api/v1/mods/${mod.id}`); await load(); refreshStatus() } }}><Trash2 /> Remove</DropdownMenuItem></DropdownMenuContent></DropdownMenu></TableCell></TableRow> })}</TableBody></Table>{mods?.length === 0 && <EmptyState icon={Boxes} title="No managed mods" detail="Search Thunderstore or upload a validated package ZIP to get started." />}</Card><ModConfigDialog mod={configMod} onClose={() => setConfigMod(null)} onChanged={refreshStatus} />
  </div>
}

function WebhooksPage() {
  const { data: hooks, error, load } = useLoad<Hook[]>("/api/v1/webhooks")
  const { data: deliveries, load: loadDeliveries } = useLoad<Delivery[]>("/api/v1/webhooks/deliveries")
  const [name, setName] = useState("")
  const [url, setUrl] = useState("")
  const [kind, setKind] = useState("generic")
  const [events, setEvents] = useState("player.*,server.*")
  const [template, setTemplate] = useState("**{{type}}** on {{server}}")
  const [secret, setSecret] = useState("")
  const create = async (event: FormEvent) => { event.preventDefault(); try { const output = await post<{ secret: string }>("/api/v1/webhooks", { name, url, kind, secret: "", eventTypes: events.split(",").map(x => x.trim()).filter(Boolean), template, enabled: true, allowPrivateNetwork: false }); setSecret(output.secret); setName(""); setUrl(""); toast.success("Webhook created"); await load() } catch (cause) { toast.error((cause as Error).message) } }
  const save = async (hook: Hook, changes: Partial<Hook>) => { try { await request(`/api/v1/webhooks/${hook.id}`, { method: "PUT", body: JSON.stringify({ ...hook, ...changes, secret: "" }) }); await load() } catch (cause) { toast.error((cause as Error).message) } }
  const edit = async (hook: Hook) => { const nextName = prompt("Webhook name", hook.name); if (nextName === null) return; const nextUrl = prompt("Destination URL", hook.url); if (nextUrl === null) return; const nextEvents = prompt("Comma-separated event filters", hook.eventTypes.join(",")); if (nextEvents === null) return; const nextTemplate = prompt("Discord message template", hook.template); if (nextTemplate === null) return; await save(hook, { name: nextName, url: nextUrl, eventTypes: nextEvents.split(",").map(x => x.trim()).filter(Boolean), template: nextTemplate }) }
  return <div className="space-y-6"><PageHeader page="webhooks" /><ErrorAlert text={error} /><div className="grid gap-4 xl:grid-cols-[1.05fr_.95fr]">
    <Card><CardHeader><CardTitle className="flex items-center gap-2"><PlugZap className="size-4 text-primary" /> New endpoint</CardTitle><CardDescription>Destinations are disabled from private networks by default.</CardDescription></CardHeader><CardContent><form onSubmit={create} className="grid gap-4 sm:grid-cols-2"><div className="space-y-1.5"><Label htmlFor="hook-name">Name</Label><Input id="hook-name" value={name} onChange={e => setName(e.target.value)} required /></div><div className="space-y-1.5"><Label>Adapter</Label><Select value={kind} onValueChange={setKind}><SelectTrigger className="w-full"><SelectValue /></SelectTrigger><SelectContent><SelectItem value="generic">Generic JSON</SelectItem><SelectItem value="discord">Discord</SelectItem></SelectContent></Select></div><div className="space-y-1.5 sm:col-span-2"><Label htmlFor="hook-url">Destination URL</Label><Input id="hook-url" type="url" value={url} onChange={e => setUrl(e.target.value)} required placeholder="https://…" /></div><div className="space-y-1.5 sm:col-span-2"><Label htmlFor="hook-events">Event filters</Label><Input id="hook-events" value={events} onChange={e => setEvents(e.target.value)} /></div>{kind === "discord" && <div className="space-y-1.5 sm:col-span-2"><Label htmlFor="hook-template">Message template</Label><Input id="hook-template" value={template} onChange={e => setTemplate(e.target.value)} /></div>}<Button className="sm:col-span-2"><Plus /> Create webhook</Button>{secret && <Alert className="sm:col-span-2 border-primary/20 bg-primary/[.06]"><ShieldCheck /><AlertTitle>Copy this secret now</AlertTitle><AlertDescription className="break-all font-mono">{secret}</AlertDescription></Alert>}</form></CardContent></Card>
    <Card><CardHeader><CardTitle>Delivery policy</CardTitle><CardDescription>Reliable by default, inspectable when something fails.</CardDescription></CardHeader><CardContent className="space-y-3">{[["Signed payloads", "HMAC-SHA256, delivery ID, event type, and timestamp headers."], ["Bounded retries", "Exponential backoff with delivery history and manual redelivery."], ["Private by default", "Loopback, link-local, and metadata destinations stay blocked."]].map(([title, detail]) => <div className="rounded-lg border bg-background/40 p-3" key={title}><p className="text-sm font-medium">{title}</p><p className="mt-1 text-xs leading-5 text-muted-foreground">{detail}</p></div>)}</CardContent></Card>
    </div>
    <div className="space-y-3">{hooks?.map(hook => <Card key={hook.id} size="sm"><CardContent className="flex flex-col gap-4 sm:flex-row sm:items-center"><div className="min-w-0 flex-1"><div className="flex flex-wrap items-center gap-2"><p className="font-medium">{hook.name}</p><Badge variant="outline">{hook.kind}</Badge>{hook.consecutiveFailures > 0 && <Badge variant="destructive">{hook.consecutiveFailures} failures</Badge>}</div><p className="mt-1 truncate font-mono text-xs text-muted-foreground">{hook.url}</p></div><div className="flex items-center gap-2"><Switch checked={hook.enabled} onCheckedChange={enabled => save(hook, { enabled })} aria-label={`Toggle ${hook.name}`} /><Button variant="outline" size="sm" onClick={() => edit(hook)}>Edit</Button><Button variant="outline" size="sm" onClick={async () => { await post(`/api/v1/webhooks/${hook.id}/test`); toast.success("Test event queued"); window.setTimeout(loadDeliveries, 500) }}>Test</Button><Button variant="ghost" size="icon-sm" className="text-destructive" onClick={async () => { if (confirm(`Delete ${hook.name}?`)) { await remove(`/api/v1/webhooks/${hook.id}`); await load() } }}><Trash2 /></Button></div></CardContent></Card>)}{hooks?.length === 0 && <Card><EmptyState icon={Webhook} title="No subscriptions" detail="Create a generic or Discord webhook to deliver selected server events." /></Card>}</div>
    <Card className="overflow-hidden py-0"><Table><TableHeader><TableRow><TableHead>Created</TableHead><TableHead>Event</TableHead><TableHead>Status</TableHead><TableHead>Attempts</TableHead><TableHead className="w-24" /></TableRow></TableHeader><TableBody>{deliveries?.map(delivery => <TableRow key={delivery.id}><TableCell><div className="text-sm">{new Date(delivery.createdAt).toLocaleString()}</div><div className="max-w-sm truncate text-xs text-destructive">{delivery.lastError}</div></TableCell><TableCell className="font-mono text-xs">{delivery.eventType}</TableCell><TableCell><Badge variant={delivery.status === "delivered" ? "secondary" : delivery.status === "failed" ? "destructive" : "outline"}>{delivery.status}</Badge></TableCell><TableCell>{delivery.attempts}{delivery.responseStatus ? ` · HTTP ${delivery.responseStatus}` : ""}</TableCell><TableCell><Button variant="ghost" size="sm" onClick={async () => { await post(`/api/v1/webhooks/deliveries/${delivery.id}/redeliver`); toast.success("Delivery queued"); await loadDeliveries() }}><RefreshCw /> Retry</Button></TableCell></TableRow>)}</TableBody></Table>{deliveries?.length === 0 && <EmptyState icon={RadioTower} title="No deliveries yet" detail="Delivery attempts and responses will appear here." />}</Card>
  </div>
}

function ConsolePage({ liveLogs }: { liveLogs: Log[] }) {
  const { data: history } = useLoad<Log[]>("/api/v1/console/history")
  const [command, setCommand] = useState("")
  const [paused, setPaused] = useState(false)
  const [filter, setFilter] = useState("")
  const [stream, setStream] = useState("all")
  const end = useRef<HTMLDivElement>(null)
  const allLogs = paused ? (history || []) : [...(history || []), ...liveLogs].slice(-1000)
  const logs = allLogs.filter(line => (stream === "all" || line.stream === stream) && (!filter || line.message.toLowerCase().includes(filter.toLowerCase())))
  useEffect(() => { if (!paused) end.current?.scrollIntoView({ behavior: "smooth" }) }, [logs.length, paused])
  const run = async (event: FormEvent) => { event.preventDefault(); if (!command.trim()) return; try { const result = await post("/api/v1/console", { command }); setCommand(""); toast.success(typeof result === "object" ? JSON.stringify(result) : String(result)) } catch (cause) { toast.error((cause as Error).message) } }
  const download = () => { const blob = new Blob([allLogs.map(line => `${line.timestamp} [${line.stream}] ${line.message}`).join("\n")], { type: "text/plain" }); const anchor = document.createElement("a"); anchor.href = URL.createObjectURL(blob); anchor.download = "valheim-console.log"; anchor.click(); URL.revokeObjectURL(anchor.href) }
  return <div className="space-y-6"><PageHeader page="console" actions={<><Button variant="outline" onClick={download}><Download /> Download</Button><Button variant={paused ? "default" : "outline"} onClick={() => setPaused(!paused)}>{paused ? "Resume" : "Pause"}</Button></>} />
    <Card className="gap-0 overflow-hidden border-emerald-950 bg-[#07100c] py-0 text-slate-200 ring-emerald-900/50"><div className="flex flex-col gap-3 border-b border-emerald-950 p-3 sm:flex-row sm:items-center"><div className="flex gap-1.5"><span className="size-2.5 rounded-full bg-red-400/60" /><span className="size-2.5 rounded-full bg-amber-400/60" /><span className="size-2.5 rounded-full bg-emerald-400/60" /></div><div className="relative flex-1 sm:max-w-xs"><Search className="absolute left-2.5 top-1/2 size-3.5 -translate-y-1/2 text-slate-500" /><Input className="h-7 border-emerald-950 bg-black/20 pl-8 text-xs" placeholder="Filter output" value={filter} onChange={e => setFilter(e.target.value)} /></div><Select value={stream} onValueChange={setStream}><SelectTrigger size="sm" className="border-emerald-950 bg-black/20"><SelectValue /></SelectTrigger><SelectContent><SelectItem value="all">All streams</SelectItem><SelectItem value="stdout">stdout</SelectItem><SelectItem value="stderr">stderr</SelectItem><SelectItem value="plugin">plugin</SelectItem></SelectContent></Select><Badge variant="outline" className="ml-auto border-emerald-900 text-emerald-400">{paused ? "Paused" : "Live"}</Badge></div>
      <ScrollArea className="h-[56vh]"><div className="min-w-[680px] p-4 font-mono text-[11px] leading-5">{logs.map((line, index) => <div key={`${line.timestamp}-${index}`} className="grid grid-cols-[82px_55px_1fr] gap-3"><time className="text-slate-600">{new Date(line.timestamp).toLocaleTimeString()}</time><span className={cn("text-emerald-700", line.stream === "stderr" && "text-red-400")}>{line.stream}</span><span className="whitespace-pre-wrap break-all text-slate-300">{line.message}</span></div>)}<div ref={end} /></div></ScrollArea>
      <form onSubmit={run} className="flex items-center gap-2 border-t border-emerald-950 p-2"><Command className="ml-2 size-4 text-emerald-500" /><Input value={command} onChange={e => setCommand(e.target.value)} placeholder="help" autoComplete="off" className="border-0 bg-transparent font-mono text-xs shadow-none focus-visible:ring-0" /><Button size="sm" className="bg-emerald-800 text-emerald-50 hover:bg-emerald-700">Run</Button></form>
    </Card>
  </div>
}

function AuditPage() {
  const { data, error, load } = useLoad<Audit[]>("/api/v1/audit")
  return <div className="space-y-6"><PageHeader page="audit" actions={<Button variant="outline" onClick={() => load()}><RefreshCw /> Refresh</Button>} /><ErrorAlert text={error} /><Card className="overflow-hidden py-0"><Table><TableHeader><TableRow><TableHead>Time</TableHead><TableHead>Actor</TableHead><TableHead>Action</TableHead><TableHead>Target</TableHead><TableHead>Result</TableHead></TableRow></TableHeader><TableBody>{data?.map(item => <TableRow key={item.id}><TableCell className="whitespace-nowrap text-xs text-muted-foreground">{new Date(item.occurredAt).toLocaleString()}</TableCell><TableCell className="font-mono text-xs">{item.actor}</TableCell><TableCell className="font-mono text-xs">{item.action}</TableCell><TableCell>{item.target || "—"}</TableCell><TableCell><Badge variant={item.result === "success" ? "secondary" : "destructive"}>{item.result}</Badge></TableCell></TableRow>)}</TableBody></Table>{data?.length === 0 && <EmptyState icon={ScrollText} title="No audit records" detail="Administrative actions will be recorded here." />}</Card></div>
}

function ServerAccessSettingsCard() {
  const { data, error, load } = useLoad<ServerAccessSettings>("/api/v1/settings/server-access")
  const [passwordEnabled, setPasswordEnabled] = useState(true)
  const [password, setPassword] = useState("")
  const [busy, setBusy] = useState(false)
  useEffect(() => { if (data) setPasswordEnabled(data.passwordEnabled) }, [data])
  const save = async (event: FormEvent) => {
    event.preventDefault()
    setBusy(true)
    try {
      await post<ServerAccessSettings>("/api/v1/settings/server-access", { passwordEnabled, password: password || null })
      setPassword("")
      toast.success("Access mode saved; Valheim restarted")
      await load()
    } catch (cause) { toast.error((cause as Error).message) } finally { setBusy(false) }
  }
  return <Card><CardHeader><CardTitle>Connection protection</CardTitle><CardDescription>Choose whether joining requires a Valheim password. Saving performs a world save and controlled game restart.</CardDescription></CardHeader><CardContent><ErrorAlert text={error} /><form onSubmit={save} className="space-y-4"><div className="flex items-center justify-between rounded-lg border p-4"><div><p className="text-sm font-medium">Require a server password</p><p className="text-xs text-muted-foreground">{passwordEnabled ? "The server may remain visible in the public browser." : "Passwordless servers are forced to private/unlisted mode and joined by IP."}</p></div><Switch checked={passwordEnabled} onCheckedChange={setPasswordEnabled} /></div>{passwordEnabled && <div className="space-y-1.5"><Label htmlFor="server-password">New password</Label><Input id="server-password" type="password" value={password} onChange={event => setPassword(event.target.value)} placeholder={data?.hasPassword ? "Leave blank to keep the current password" : "At least 5 characters"} /><p className="text-[11px] text-muted-foreground">Stored encrypted. It must be at least five characters and cannot appear in the server name.</p></div>}<div className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-muted/30 p-3 text-xs"><span><strong>{data?.serverName || "Server"}</strong> · port {data?.port || "—"}</span><Badge variant="outline">{passwordEnabled ? data?.publicListing ? "Password · listed" : "Password · unlisted" : "No password · unlisted"}</Badge></div><Button disabled={busy}>{busy ? <RefreshCw className="animate-spin" /> : <KeyRound />}{busy ? "Saving and restarting…" : "Save & restart server"}</Button></form></CardContent></Card>
}

function ServerCharacterSettingsCard() {
  const { data, error, load } = useLoad<ServerCharacterSettings>("/api/v1/settings/server-characters")
  const [enabled, setEnabled] = useState(false)
  const [acceptFirstJoinProfile, setAcceptFirstJoinProfile] = useState(true)
  const [rejectPreviouslyUsedCharacters, setRejectPreviouslyUsedCharacters] = useState(false)
  const [backupsToKeep, setBackupsToKeep] = useState(10)
  const [clientGraceSeconds, setClientGraceSeconds] = useState(20)
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    if (!data) return
    setEnabled(data.enabled)
    setAcceptFirstJoinProfile(data.acceptFirstJoinProfile)
    setRejectPreviouslyUsedCharacters(data.rejectPreviouslyUsedCharacters)
    setBackupsToKeep(data.backupsToKeep)
    setClientGraceSeconds(data.clientGraceSeconds)
  }, [data])
  const save = async (event: FormEvent) => {
    event.preventDefault()
    setBusy(true)
    try {
      await request<ServerCharacterSettings>("/api/v1/settings/server-characters", { method: "PUT", body: JSON.stringify({ enabled, acceptFirstJoinProfile, rejectPreviouslyUsedCharacters, backupsToKeep, clientGraceSeconds }) })
      toast.success(enabled ? "Server-owned characters enabled" : "Vanilla-compatible mode enabled")
      await load()
    } catch (cause) { toast.error((cause as Error).message) } finally { setBusy(false) }
  }
  return <Card><CardHeader><CardTitle>Client compatibility</CardTitle><CardDescription>Choose a normal vanilla-compatible server or opt into server-owned characters and managed clients.</CardDescription></CardHeader><CardContent><ErrorAlert text={error} /><form onSubmit={save} className="space-y-4"><div className="flex items-center justify-between rounded-lg border p-4"><div><div className="flex items-center gap-2"><p className="text-sm font-medium">Allow players without mods</p><Badge variant={!enabled ? "secondary" : "outline"}>{!enabled ? "Recommended" : "Off"}</Badge></div><p className="text-xs text-muted-foreground">Disables server-owned characters. Players can join with an unmodified Valheim client.</p></div><Switch checked={!enabled} onCheckedChange={allow => setEnabled(!allow)} /></div><div className="flex items-center justify-between rounded-lg border p-4"><div><p className="text-sm font-medium">Accept a profile on first join</p><p className="text-xs text-muted-foreground">When disabled, an administrator must import the player's .fch save before they can enter.</p></div><Switch checked={acceptFirstJoinProfile} disabled={!enabled} onCheckedChange={setAcceptFirstJoinProfile} /></div><div className="flex items-center justify-between rounded-lg border p-4"><div><p className="text-sm font-medium">Reject previously used characters</p><p className="text-xs text-muted-foreground">Require a new character with no existing world history on first join.</p></div><Switch checked={rejectPreviouslyUsedCharacters} disabled={!enabled || !acceptFirstJoinProfile} onCheckedChange={setRejectPreviouslyUsedCharacters} /></div><div className="grid gap-4 sm:grid-cols-2"><div className="space-y-1.5"><Label htmlFor="character-backups">Character backups</Label><Input id="character-backups" type="number" min={1} max={50} value={backupsToKeep} disabled={!enabled} onChange={event => setBackupsToKeep(Number(event.target.value))} /><p className="text-[11px] text-muted-foreground">Rolling native saves per character (1–50).</p></div><div className="space-y-1.5"><Label htmlFor="client-grace">Client handshake grace</Label><Input id="client-grace" type="number" min={5} max={120} value={clientGraceSeconds} disabled={!enabled} onChange={event => setClientGraceSeconds(Number(event.target.value))} /><p className="text-[11px] text-muted-foreground">Seconds before a missing client mod is rejected (5–120).</p></div></div>{enabled ? <Alert><ShieldCheck /><AlertTitle>Managed-client mode</AlertTitle><AlertDescription>Server-owned characters require Server Manager on each client. Players without it are rejected after the configured grace period.</AlertDescription></Alert> : <Alert><Users /><AlertTitle>Vanilla-compatible mode</AlertTitle><AlertDescription>Server administration remains active, but character transfer, checkpoints, and client-mod enforcement are disabled.</AlertDescription></Alert>}<Button disabled={busy}>{busy ? <RefreshCw className="animate-spin" /> : <Shield />}{busy ? "Applying…" : "Save compatibility mode"}</Button></form></CardContent></Card>
}

const messageFields: { key: keyof ServerMessages; label: string; placeholders: string; detail: string }[] = [
  { key: "welcome", label: "Welcome", placeholders: "{player} {server}", detail: "A quiet notice shown once the character is ready." },
  { key: "kick", label: "Kick", placeholders: "{player} {server} {reason}", detail: "Sent before a kick; remains readable at the menu on compatible clients." },
  { key: "ban", label: "Ban", placeholders: "{player} {server} {reason}", detail: "Sent before an online ban; remains readable at the menu on compatible clients." },
  { key: "restart", label: "Restart", placeholders: "{server} {seconds} {reason}", detail: "Broadcast for delayed safe-console restarts." },
  { key: "whitelistRejected", label: "Whitelist rejection", placeholders: "{player} {server}", detail: "Explain how to get approval. The Server Manager update receiver can show this before the full client runtime is installed." },
  { key: "companionRequired", label: "Client runtime required", placeholders: "{player} {server}", detail: "Shown when server-owned characters require a client restart/update." },
]

function ServerMessagesSettingsCard() {
  const { data, error, load } = useLoad<ServerMessages>("/api/v1/settings/messages")
  const [values, setValues] = useState<ServerMessages | null>(null)
  const previewRef = useRef<HTMLElement>(null)
  const [preview, setPreview] = useState<keyof ServerMessages>("kick")
  const [busy, setBusy] = useState(false)
  useEffect(() => { if (data) setValues(data) }, [data])
  const save = async (event: FormEvent) => {
    event.preventDefault(); if (!values) return
    setBusy(true)
    try {
      const saved = await request<ServerMessages>("/api/v1/settings/messages", { method: "PUT", body: JSON.stringify(values) })
      setValues(saved); toast.success("Server messages published to the live agent"); await load()
    } catch (cause) { toast.error((cause as Error).message) } finally { setBusy(false) }
  }
  const samples: Record<string, string> = { player: "Astrid", server: "Your server", reason: "Please contact an administrator before rejoining.", seconds: "60" }
  const titles: Record<keyof ServerMessages, string> = { welcome: "Welcome", kick: "Kicked", ban: "Banned", restart: "Server notice", whitelistRejected: "Whitelist approval required", companionRequired: "Client update required" }
  const previewMessage = (values?.[preview] ?? "").replace(/\{(player|server|reason|seconds)\}/g, (_, key: string) => samples[key])
  const tooManyLines = values && messageFields.some(field => values[field.key].split("\n").length > 4)
  return <Card>
    <CardHeader><CardTitle>Player-facing messages</CardTitle><CardDescription>Give players a clear reason and a next step. Use up to 500 characters and four lines per message.</CardDescription></CardHeader>
    <CardContent><ErrorAlert text={error} />{values && <div className="grid gap-8 xl:grid-cols-[minmax(0,1fr)_minmax(280px,380px)]">
      <aside ref={previewRef} className="min-w-0 space-y-3 xl:sticky xl:top-6 xl:col-start-2 xl:row-start-1 xl:self-start" aria-label="Client notice preview">
        <Label htmlFor="notice-preview-type">Client preview</Label>
        <Select value={preview} onValueChange={value => setPreview(value as keyof ServerMessages)}><SelectTrigger id="notice-preview-type" className="w-full"><SelectValue /></SelectTrigger><SelectContent>{messageFields.map(field => <SelectItem key={field.key} value={field.key}>{field.label}</SelectItem>)}</SelectContent></Select>
        <ClientNoticePreview title={titles[preview]} message={previewMessage} persistent={preview !== "welcome" && preview !== "restart"} />
      </aside>
      <form onSubmit={save} className="min-w-0 space-y-5 xl:col-start-1 xl:row-start-1">
        {messageFields.map(field => {
          const lines = values[field.key].split("\n").length
          return <div key={field.key} className="space-y-1.5">
            <div className="flex flex-wrap items-end justify-between gap-2"><Label htmlFor={`message-${field.key}`}>{field.label}</Label><div className="flex items-center gap-3"><button type="button" className="text-xs text-primary underline-offset-4 hover:underline" aria-label={`Preview ${field.label.toLowerCase()} notice`} onClick={() => { setPreview(field.key); previewRef.current?.scrollIntoView({ block: "nearest", behavior: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "instant" : "smooth" }) }}>Preview</button><span className="text-[11px] text-muted-foreground">{values[field.key].length}/500</span></div></div>
            <Textarea id={`message-${field.key}`} value={values[field.key]} maxLength={500} rows={3} required aria-invalid={lines > 4} aria-describedby={`message-help-${field.key}`} onFocus={() => setPreview(field.key)} onChange={event => setValues({ ...values, [field.key]: event.target.value })} />
            <div id={`message-help-${field.key}`} className="space-y-1"><p className="text-[11px] text-muted-foreground">{field.detail}</p><code className="text-[10px] text-muted-foreground">{field.placeholders}</code>{lines > 4 && <p className="text-xs text-destructive" role="alert">Use no more than four lines ({lines} entered).</p>}</div>
          </div>
        })}
        <Button disabled={busy || !!tooManyLines}>{busy ? <RefreshCw className="animate-spin" /> : <ScrollText />}{busy ? "Publishing…" : "Save messages"}</Button>
      </form>

    </div>}</CardContent>
  </Card>
}

function ApiTokensSettingsCard() {
  const { data, error, load } = useLoad<ApiToken[]>("/api/v1/api-tokens")
  const [name, setName] = useState("Discord registration bot")
  const [created, setCreated] = useState<CreatedApiToken | null>(null)
  const create = async (event: FormEvent) => { event.preventDefault(); try { const token = await post<CreatedApiToken>("/api/v1/api-tokens", { name, scopes: ["whitelist.read", "whitelist.write", "join-requests.read", "join-requests.write"] }); setCreated(token); setName(""); await load(); toast.success("API token created") } catch (cause) { toast.error((cause as Error).message) } }
  const revoke = async (token: ApiToken) => { if (!confirm(`Revoke ${token.name}?`)) return; try { await remove(`/api/v1/api-tokens/${token.id}`); await load(); toast.success("API token revoked") } catch (cause) { toast.error((cause as Error).message) } }
  return <div className="grid gap-4 xl:grid-cols-[.9fr_1.1fr]"><Card><CardHeader><CardTitle>Create service token</CardTitle><CardDescription>Scoped Bearer token for a Discord bot or registration service. The secret is shown once.</CardDescription></CardHeader><CardContent><ErrorAlert text={error} /><form className="space-y-4" onSubmit={create}><div className="space-y-1.5"><Label htmlFor="token-name">Integration name</Label><Input id="token-name" value={name} onChange={event => setName(event.target.value)} maxLength={80} required /></div><div className="rounded-lg border p-3 text-xs"><p className="font-medium">Scopes</p><p className="mt-1 font-mono text-muted-foreground">whitelist.read · whitelist.write</p><p className="mt-1 font-mono text-muted-foreground">join-requests.read · join-requests.write</p></div><Button><Plus /> Create token</Button></form>{created && <Alert className="mt-4 border-primary/30 bg-primary/[.06]"><KeyRound /><AlertTitle>Copy this token now</AlertTitle><AlertDescription><code className="mt-2 block break-all rounded bg-background/70 p-2 text-[11px]">{created.token}</code><Button type="button" size="sm" variant="outline" className="mt-2" onClick={() => navigator.clipboard.writeText(created.token)}><Copy /> Copy</Button></AlertDescription></Alert>}</CardContent></Card><Card><CardHeader><CardTitle>Registration API</CardTitle><CardDescription>Use the token without Steam cookies or CSRF headers.</CardDescription></CardHeader><CardContent className="space-y-4"><div className="space-y-2 rounded-lg border bg-muted/20 p-3 font-mono text-[11px]"><p>GET /api/external/v1/join-requests?status=pending</p><p>POST /api/external/v1/join-requests/:id/approve</p><p>POST /api/external/v1/join-requests/:id/deny</p><p className="pt-1">GET /api/external/v1/whitelist</p><p>POST /api/external/v1/whitelist</p><p className="text-muted-foreground">{"{ \"platformId\": \"76561198000000000\" }"}</p><p>DELETE /api/external/v1/whitelist/Steam_76561198000000000</p><p className="pt-1 text-muted-foreground">Authorization: Bearer &lt;token&gt;</p></div><div className="space-y-2">{data?.map(token => <div key={token.id} className="flex items-center gap-3 rounded-lg border p-3"><div className="grid size-9 place-items-center rounded-lg bg-primary/10"><KeyRound className="size-4 text-primary" /></div><div className="min-w-0 flex-1"><div className="flex items-center gap-2"><p className="truncate text-sm font-medium">{token.name}</p>{token.revokedAt && <Badge variant="destructive">Revoked</Badge>}</div><p className="font-mono text-[10px] text-muted-foreground">{token.prefix}… · {token.lastUsedAt ? `used ${new Date(token.lastUsedAt).toLocaleString()}` : "never used"}</p></div>{!token.revokedAt && <Button variant="ghost" size="sm" className="text-destructive" onClick={() => revoke(token)}>Revoke</Button>}</div>)}{data?.length === 0 && <p className="py-6 text-center text-sm text-muted-foreground">No service tokens.</p>}</div></CardContent></Card></div>
}

function ManagerUpdateSettingsCard() {
  const { data, error, setData } = useLoad<ManagerUpdate>("/api/v1/manager-update")
  const [busy, setBusy] = useState(false)
  const check = async () => { setBusy(true); try { const status = await post<ManagerUpdate>("/api/v1/manager-update/check"); setData(status); toast.success(status.updateAvailable ? `Server Manager ${status.latestVersion} is available` : "Server Manager is up to date") } catch (cause) { toast.error((cause as Error).message) } finally { setBusy(false) } }
  const setAutomatic = async (automatic: boolean) => { try { const status = await request<ManagerUpdate>("/api/v1/manager-update/settings", { method: "PUT", body: JSON.stringify({ automatic }) }); setData(status); toast.success(automatic ? "Automatic stable updates enabled" : "Automatic updates disabled") } catch (cause) { toast.error((cause as Error).message) } }
  const apply = async () => {
    if (!confirm(`Update Server Manager to ${data?.latestVersion}? Valheim will save and briefly stop.`)) return
    setBusy(true)
    try { const status = await post<ManagerUpdate>("/api/v1/manager-update/apply"); setData(status); toast.success("Update queued; this dashboard will reconnect automatically") }
    catch (cause) { toast.error((cause as Error).message); setBusy(false) }
  }
  const active = !!data && !["idle", "succeeded", "failed"].includes(data.state)
  return <Card><CardHeader><CardTitle className="flex items-center gap-2"><Download className="size-4 text-primary" /> Server Manager updates</CardTitle><CardDescription>Updates the control plane, web dashboard, server agent, and client artifact together from stable GitHub releases.</CardDescription><CardAction><Badge variant={data?.updateAvailable ? "secondary" : "outline"}>{data?.updateAvailable ? `${data.latestVersion} available` : data ? `v${data.currentVersion}` : "Loading"}</Badge></CardAction></CardHeader><CardContent className="space-y-4"><ErrorAlert text={error} />
    <div className="grid gap-3 sm:grid-cols-3"><div className="rounded-lg border bg-muted/20 p-3"><p className="text-xs text-muted-foreground">Installed</p><p className="mt-1 font-mono text-sm">v{data?.currentVersion || "—"}</p></div><div className="rounded-lg border bg-muted/20 p-3"><p className="text-xs text-muted-foreground">Latest stable</p><p className="mt-1 font-mono text-sm">{data?.latestVersion ? `v${data.latestVersion}` : "Not checked"}</p></div><div className="rounded-lg border bg-muted/20 p-3"><p className="text-xs text-muted-foreground">Updater</p><p className="mt-1 text-sm">{data?.hostUpdaterAvailable ? "Host service connected" : "Host service unavailable"}</p></div></div>
    <div className="flex items-center justify-between rounded-lg border p-4"><div><p className="text-sm font-medium">Automatic stable updates</p><p className="text-xs text-muted-foreground">Checks daily and applies only while no players are online. Failed health checks restore the previous image.</p></div><Switch checked={data?.automaticUpdates || false} disabled={!data?.hostUpdaterAvailable} onCheckedChange={setAutomatic} /></div>
    {data && data.state !== "idle" && <Alert variant={data.state === "failed" ? "destructive" : "default"}><RefreshCw className={cn(active && "animate-spin")} /><AlertTitle className="capitalize">{data.state.replace("-", " ")}{data.targetVersion ? ` · v${data.targetVersion}` : ""}</AlertTitle><AlertDescription>{data.detail}</AlertDescription></Alert>}
    {!data?.hostUpdaterAvailable && <Alert><Server /><AlertTitle>One-time host setup required</AlertTitle><AlertDescription>Run <code>sudo ./scripts/install-host-updater.sh</code> on the Docker host. The dashboard never receives Docker socket or shell access.</AlertDescription></Alert>}
    <div className="flex flex-wrap gap-2"><Button variant="outline" disabled={busy} onClick={check}><RefreshCw className={cn(busy && "animate-spin")} /> Check now</Button><Button disabled={busy || !data?.updateAvailable || !data.hostUpdaterAvailable || active} onClick={apply}><Download /> Save & update</Button>{data?.releaseUrl && <Button asChild variant="ghost"><a href={data.releaseUrl} target="_blank" rel="noreferrer">Release notes</a></Button>}</div>
  </CardContent></Card>
}

function SettingsPage() {
  return <div className="space-y-6"><PageHeader page="settings" /><Tabs defaultValue="server"><TabsList className="grid w-full grid-cols-3 gap-1 group-data-horizontal/tabs:h-auto sm:inline-flex sm:w-fit [&_[data-slot=tabs-trigger]]:h-7"><TabsTrigger value="server">Server</TabsTrigger><TabsTrigger value="updates">Updates</TabsTrigger><TabsTrigger value="messages">Messages</TabsTrigger><TabsTrigger value="api">API tokens</TabsTrigger><TabsTrigger value="privacy">Privacy</TabsTrigger><TabsTrigger value="artifacts">Artifacts</TabsTrigger></TabsList><TabsContent value="server" className="space-y-4 pt-2"><ServerAccessSettingsCard /><ServerCharacterSettingsCard /></TabsContent><TabsContent value="updates" className="pt-2"><ManagerUpdateSettingsCard /></TabsContent><TabsContent value="messages" className="pt-2"><ServerMessagesSettingsCard /></TabsContent><TabsContent value="api" className="pt-2"><ApiTokensSettingsCard /></TabsContent><TabsContent value="privacy" className="pt-2"><Card><CardHeader><CardTitle>Privacy defaults</CardTitle><CardDescription>Collection remains narrow until explicitly enabled.</CardDescription></CardHeader><CardContent className="divide-y rounded-lg border bg-background/40 px-4">{[["Chat webhooks", "Disabled until explicitly filtered"], ["Inventory inspection", "Consent-based, on demand, never persisted"], ["Server character", "Optional; disabled in vanilla-compatible mode"], ["Private whispers", "Never captured"], ["Detailed telemetry", "Opt-in on each client"]].map(([label, value]) => <div className="flex flex-col justify-between gap-1 py-3 sm:flex-row sm:items-center" key={label}><span className="text-sm font-medium">{label}</span><span className="text-sm text-muted-foreground">{value}</span></div>)}</CardContent></Card></TabsContent><TabsContent value="artifacts" className="pt-2"><Card><CardHeader><CardTitle>Server Manager package</CardTitle><CardDescription>Install this package on the server. Clients need it only when managed-client mode is enabled.</CardDescription></CardHeader><CardContent><Button asChild className="h-auto justify-start p-4"><a href="/api/v1/downloads/plugin"><PackageCheck className="size-5" /><span className="text-left"><span className="block">Valheim Server Manager 2.0.0</span><span className="block text-xs font-normal opacity-80">One optional client package—no companion mod</span></span></a></Button></CardContent></Card></TabsContent></Tabs></div>
}

function Dashboard({ auth }: { auth: AuthState }) {
  const [page, setPage] = useState<PageId>("overview")
  const [status, setStatus] = useState<Status>({ status: "loading", uptimeSeconds: 0, agentConnected: false, agentVersion: "", gameVersion: "", restartRequired: false, players: 0 })
  const [liveLogs, setLiveLogs] = useState<Log[]>([])
  const loadedManagerVersion = useRef<string | null>(null)
  const refreshStatus = useCallback(() => { void request<Status>("/api/v1/status").then(setStatus).catch(() => {}) }, [])
  useEffect(() => { refreshStatus(); const connection = new HubConnectionBuilder().withUrl("/hubs/live").withAutomaticReconnect().configureLogging(LogLevel.Warning).build(); connection.on("status", setStatus); connection.on("log", (log: Log) => setLiveLogs(items => [...items.slice(-999), log])); connection.start().catch(console.error); return () => { void connection.stop() } }, [refreshStatus])
  useEffect(() => { const check = () => request<ManagerUpdate>("/api/v1/manager-update").then(update => { if (loadedManagerVersion.current && loadedManagerVersion.current !== update.currentVersion) window.location.reload(); loadedManagerVersion.current = update.currentVersion }).catch(() => {}); void check(); const timer = window.setInterval(check, 30_000); return () => window.clearInterval(timer) }, [])
  const logout = async () => { await post("/api/v1/auth/logout"); window.location.assign("/") }
  const active = navigation.find(item => item.id === page)!
  const content = page === "overview" ? <Overview status={status} refresh={refreshStatus} /> : page === "players" ? <PlayersPage /> : page === "characters" ? <CharactersPage /> : page === "access" ? <AccessPage /> : page === "requests" ? <JoinRequestsPage /> : page === "mods" ? <ModsPage refreshStatus={refreshStatus} /> : page === "webhooks" ? <WebhooksPage /> : page === "console" ? <ConsolePage liveLogs={liveLogs} /> : page === "audit" ? <AuditPage /> : <SettingsPage />
  return <SidebarProvider style={{ "--sidebar-width": "calc(var(--spacing) * 72)", "--header-height": "calc(var(--spacing) * 12)" } as React.CSSProperties}>
    <AppSidebar variant="inset" items={navigation} activeId={page} onNavigate={id => setPage(id as PageId)} userName={auth.userName || "Steam admin"} steamId={auth.steamId} onlinePlayers={status.players} agentConnected={status.agentConnected} onLogout={logout} />
    <SidebarInset>
      <SiteHeader title={active.label} status={status.status} onRefresh={refreshStatus} />
      <div className="flex flex-1 flex-col"><div className="@container/main flex flex-1 flex-col gap-2"><div className="flex flex-col gap-4 py-4 md:gap-6 md:py-6"><main className="mx-auto w-full max-w-[1500px] px-4 lg:px-6">{content}</main></div></div></div>
    </SidebarInset>
  </SidebarProvider>
}

function App() {
  const [auth, setAuth] = useState<AuthState | null>(null)
  useEffect(() => { void ensureCsrf(); void request<AuthState>("/api/v1/auth/state").then(setAuth) }, [])
  if (!auth) return <main className="grid min-h-svh place-items-center bg-background"><div className="flex items-center gap-3 text-sm text-muted-foreground"><RefreshCw className="size-4 animate-spin" /> Loading the hall…</div></main>
  if (!auth.authenticated) return <Auth />
  return <Dashboard auth={auth} />
}

function formatDuration(seconds: number) {
  if (!seconds) return "—"
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor(seconds % 86400 / 3600)
  const minutes = Math.floor(seconds % 3600 / 60)
  return days ? `${days}d ${hours}h` : hours ? `${hours}h ${minutes}m` : `${minutes}m`
}

document.documentElement.classList.add("dark")
createRoot(document.getElementById("root")!).render(<React.StrictMode><TooltipProvider><App /><Toaster theme="dark" richColors position="bottom-right" /></TooltipProvider></React.StrictMode>)
