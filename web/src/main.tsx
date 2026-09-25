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
import { ModFiles, ModFileManagerPage } from "@/components/mod-file-manager"
import { LiveInspection } from "@/components/live-inspection"
import "./styles.css"

type AuthState = { authenticated: boolean; userName?: string; steamId?: string }
type Status = { status: string; startedAt?: string; uptimeSeconds: number; agentConnected: boolean; agentVersion: string; gameVersion: string; restartRequired: boolean; players: number }
type Player = { peerId: number; peerKey: string; name: string; platformId: string; connectedAt: string; ping?: number; companion: boolean; inventoryAllowed: boolean; serverCharacter: boolean }
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
type ServerSettingsData = {
  passwordEnabled: boolean; hasPassword: boolean; publicListing: boolean; serverName: string; worldName: string; port: number
  crossplay: boolean; instanceId: string; saveIntervalSeconds: number; backupCount: number; backupShortSeconds: number
  backupLongSeconds: number; maxPlayers: number; manageWorldModifiers: boolean; preset: string; combatModifier: string
  deathPenaltyModifier: string; resourceModifier: string; raidModifier: string; portalModifier: string
  noBuildCost: boolean; playerEvents: boolean; passiveMobs: boolean; noMap: boolean
}
type ServerCharacterSettings = { enabled: boolean; acceptFirstJoinProfile: boolean; rejectPreviouslyUsedCharacters: boolean; backupsToKeep: number; clientGraceSeconds: number; requireInventoryInspection: boolean }
type ApiToken = { id: string; name: string; prefix: string; scopes: string[]; createdAt: string; lastUsedAt?: string; revokedAt?: string }
type CreatedApiToken = ApiToken & { token: string }
type JoinRequest = { id: string; platformId: string; playerName: string; status: "pending" | "approved" | "denied"; requestedAt: string; lastAttemptAt: string; attemptCount: number; resolvedAt?: string; resolvedBy?: string }
type AccessLists = { whitelistEnabled: boolean; permitted: string[]; banned: string[]; admins: string[] }
type KnownPlayer = { platformId: string; name: string; lastSeenAt: string }
type SteamProfile = { steamId: string; name: string; avatarUrl: string; profileUrl: string }
type ServerMessages = { welcome: string; kick: string; ban: string; restart: string; whitelistRejected: string; companionRequired: string }
type ManagerUpdate = { currentVersion: string; latestVersion?: string; updateAvailable: boolean; automaticUpdates: boolean; hostUpdaterAvailable: boolean; state: string; targetVersion?: string; detail: string; lastCheckedAt?: string; releaseUrl?: string }
type PageId = "files" | "overview" | "mods" | "players" | "webhooks" | "console" | "audit" | "settings"

const navigation: { id: PageId; label: string; icon: React.ElementType; hint: string }[] = [
  { id: "overview", label: "Overview", icon: CircleGauge, hint: "Health & runtime" },
  { id: "files", label: "Files", icon: ScrollText, hint: "Configuration workspace" },
  { id: "mods", label: "Mods", icon: Boxes, hint: "Packages & updates" },
  { id: "players", label: "Players", icon: Users, hint: "Roster, requests, characters & access" },
  { id: "webhooks", label: "Webhooks", icon: Webhook, hint: "Event delivery" },
  { id: "console", label: "Console", icon: SquareTerminal, hint: "Live server output" },
  { id: "audit", label: "Audit log", icon: ScrollText, hint: "Administrative history" },
  { id: "settings", label: "Settings", icon: Settings, hint: "Privacy & downloads" },
]

const pageCopy: Record<PageId, { eyebrow: string; title: string; description: string }> = {
  files: { eyebrow: "Configuration workspace", title: "Files", description: "Safe, package-scoped configuration files." },
  overview: { eyebrow: "", title: "Overview", description: "" },
  mods: { eyebrow: "Package control", title: "Mods", description: "Pinned packages with dependency-aware installs and safe rollback." },
  players: { eyebrow: "", title: "Players", description: "Roster, requests, characters and access." },
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
    <header className="flex flex-wrap items-center justify-between gap-3 border-b border-border/60 pb-3">
      <div className="max-w-2xl">
        <h1 className="font-heading text-2xl font-semibold tracking-tight text-foreground">{copy.title}</h1>
        {page !== "overview" && page !== "players" && <p className="mt-0.5 text-sm text-muted-foreground">{copy.description}</p>}
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
    { label: "Server", value: status.status, icon: Server, warn: status.status !== "running" },
    { label: "Players online", value: String(status.players), icon: Users, warn: false },
    { label: "Uptime", value: formatDuration(status.uptimeSeconds), icon: Clock3, warn: false },
    { label: "Changes", value: status.restartRequired ? "Restart needed" : "None", icon: PackageCheck, warn: status.restartRequired },
  ]
  return <div className="space-y-4">
    <PageHeader page="overview" actions={<><Button variant="outline" size="sm" disabled={status.status !== "running"} onClick={() => action("restart")}><RefreshCw /> Restart</Button><Button size="sm" onClick={() => action(status.status === "running" ? "stop" : "start")}>{status.status === "running" ? "Stop server" : "Start server"}</Button></>} />
    <div className="grid grid-cols-2 gap-2 lg:grid-cols-4">{stats.map(({ label, value, icon: Icon, warn }) => <div key={label} className="rounded-xl border bg-card/70 p-3 sm:p-4"><div className="flex items-center gap-2 text-xs text-muted-foreground"><Icon className={cn("size-4", warn ? "text-amber-300" : "text-primary")} />{label}</div><p className="mt-2 text-xl font-semibold capitalize tabular-nums sm:text-2xl">{value}</p></div>)}</div>
    <div className="flex flex-wrap gap-x-5 gap-y-1 rounded-xl border bg-card/50 px-3 py-2 text-xs text-muted-foreground sm:px-4">
      <span><span className="text-foreground">Agent:</span> {status.agentConnected ? "Connected" : "Disconnected"}{status.agentVersion && ` · ${status.agentVersion}`}</span>
      <span><span className="text-foreground">Game:</span> {status.gameVersion || "—"}</span>
      {status.startedAt && <span><span className="text-foreground">Started:</span> {new Date(status.startedAt).toLocaleString()}</span>}
    </div>
  </div>
}

type InspectorState = { player: Player; snapshot: CharacterSnapshot | null; loading: boolean; error: string } | null

function CharacterDialog({ state, onClose }: { state: InspectorState; onClose: () => void }) {
  return <LiveInspection player={state?.player || null} onClose={onClose} />
}

function loadCharacter(player: Player, setState: (state: InspectorState) => void) {
  setState({ player, snapshot: null, loading: false, error: "" })
}

function steamId(platformId: string) {
  const value = platformId.startsWith("Steam_") ? platformId.slice(6) : platformId
  return /^\d{17}$/.test(value) ? value : null
}

function useSteamProfiles(ids: string[]) {
  const key = [...new Set(ids.map(id => steamId(id)).filter((id): id is string => !!id))].sort().join(",")
  const [profiles, setProfiles] = useState<Record<string, SteamProfile>>({})
  useEffect(() => {
    if (!key) return
    let active = true
    const chunks = key.split(",").reduce<string[][]>((groups, id, index) => {
      if (index % 100 === 0) groups.push([])
      groups[groups.length - 1].push(id)
      return groups
    }, [])
    void Promise.all(chunks.map(chunk => request<Record<string, SteamProfile>>(`/api/v1/steam-profiles?ids=${encodeURIComponent(chunk.join(","))}`).catch(() => ({}))))
      .then(results => { if (active) setProfiles(previous => Object.assign({}, previous, ...results)) })
    return () => { active = false }
  }, [key])
  return profiles
}

function PlayerIdentity({ id, fallback, profiles, detail }: { id: string; fallback?: string; profiles: Record<string, SteamProfile>; detail?: string }) {
  const profile = profiles[steamId(id) || ""]
  const name = profile?.name || fallback || (steamId(id) ? `Steam player · ${id.slice(-4)}` : id)
  return <div className="flex min-w-0 items-center gap-2.5">
    {profile?.avatarUrl ? <img src={profile.avatarUrl} alt="" className="size-9 shrink-0 rounded-lg bg-muted object-cover" /> : <div className="grid size-9 shrink-0 place-items-center rounded-lg bg-muted text-xs font-semibold">{name.slice(0, 2).toUpperCase()}</div>}
    <div className="min-w-0">
      {profile ? <a className="block truncate text-sm font-medium hover:text-primary hover:underline" href={profile.profileUrl} target="_blank" rel="noreferrer" title={`Open ${name} on Steam`}>{name}</a> : steamId(id) ? <a className="block truncate text-sm font-medium hover:text-primary hover:underline" href={`https://steamcommunity.com/profiles/${steamId(id)}`} target="_blank" rel="noreferrer" title="Open Steam profile">{name}</a> : <p className="truncate text-sm font-medium">{name}</p>}
      {detail && <p className="truncate text-xs text-muted-foreground">{detail}</p>}
    </div>
  </div>
}

function PlayersPage() {
  const { data: players, error: playersError, load: loadPlayers } = useLoad<Player[]>("/api/v1/players")
  const { data: known, error: knownError, load: loadKnown } = useLoad<KnownPlayer[]>("/api/v1/player-directory")
  const { data: store, error: storeError, load: loadStore } = useLoad<CharacterStore>("/api/v1/characters")
  const { data: requests, error: requestsError, load: loadRequests } = useLoad<JoinRequest[]>("/api/v1/join-requests?status=all")
  const { data: access, error: accessError, load: loadAccess } = useLoad<AccessLists>("/api/v1/access")
  const [query, setQuery] = useState("")
  const [filter, setFilter] = useState<"all" | "online" | "offline">("all")
  const [inspector, setInspector] = useState<InspectorState>(null)
  const [moderation, setModeration] = useState<{ player: Player; action: "kick" | "ban" } | null>(null)
  const [reason, setReason] = useState("")
  const [moderating, setModerating] = useState(false)
  useEffect(() => {
    const timer = window.setInterval(() => { void loadPlayers(); void loadKnown(); void loadRequests() }, 5000)
    return () => window.clearInterval(timer)
  }, [loadPlayers, loadKnown, loadRequests])
  const refresh = () => { void loadPlayers(); void loadKnown(); void loadStore(); void loadRequests(); void loadAccess() }
  const profiles = useSteamProfiles([
    ...(players || []).map(player => player.platformId), ...(known || []).map(player => player.platformId),
    ...(store?.characters || []).map(character => character.platformId), ...(requests || []).map(item => item.platformId),
    ...(access?.permitted || []), ...(access?.banned || []), ...(access?.admins || []),
  ])
  type RosterEntry = { id: string; player?: Player; known?: KnownPlayer; characters: ServerCharacter[]; request?: JoinRequest; permitted: boolean; banned: boolean; admin: boolean }
  const entries = new Map<string, RosterEntry>()
  const canonical = (id: string) => steamId(id) ? `Steam_${steamId(id)}` : id
  const get = (id: string) => {
    const key = canonical(id)
    if (!entries.has(key)) entries.set(key, { id: key, characters: [], permitted: false, banned: false, admin: false })
    return entries.get(key)!
  }
  known?.forEach(item => { get(item.platformId).known = item })
  store?.characters.forEach(item => { get(item.platformId).characters.push(item) })
  requests?.forEach(item => { if (!get(item.platformId).request) get(item.platformId).request = item })
  access?.permitted.forEach(id => { get(id).permitted = true })
  access?.banned.forEach(id => { get(id).banned = true })
  access?.admins.forEach(id => { get(id).admin = true })
  players?.forEach(player => { get(player.platformId || `peer:${player.peerKey}`).player = player })
  const roster = [...entries.values()].sort((a, b) => Number(!!b.player) - Number(!!a.player) || (b.known?.lastSeenAt || "").localeCompare(a.known?.lastSeenAt || "") || a.id.localeCompare(b.id))
  const visible = roster.filter(item => {
    if (filter === "online" && !item.player || filter === "offline" && item.player) return false
    const profile = profiles[steamId(item.id) || ""]
    return [item.id, item.player?.name, item.known?.name, item.request?.playerName, profile?.name, ...item.characters.map(character => character.characterName)]
      .some(value => value?.toLowerCase().includes(query.toLowerCase()))
  })
  const pendingCount = requests?.filter(item => item.status === "pending").length || 0
  const moderate = async () => {
    if (!moderation) return
    setModerating(true)
    try {
      await post(`/api/v1/players/${moderation.player.peerKey}/${moderation.action}`, { reason })
      toast.success(`${moderation.player.name}: ${moderation.action} scheduled`)
      setModeration(null); setReason(""); window.setTimeout(loadPlayers, 3500)
    } catch (cause) { toast.error((cause as Error).message) } finally { setModerating(false) }
  }
  return <div className="space-y-4">
    <PageHeader page="players" actions={<Button variant="outline" size="sm" onClick={refresh}><RefreshCw /> Refresh</Button>} />
    <Tabs defaultValue="roster" className="gap-3">
      <TabsList className="grid h-auto w-full grid-cols-4 gap-1 sm:inline-flex sm:w-auto sm:self-start">
        <TabsTrigger value="roster">Roster <span className="hidden text-xs text-muted-foreground sm:inline">{roster.length}</span></TabsTrigger>
        <TabsTrigger value="requests">Requests {pendingCount > 0 && <span className="rounded-full bg-primary px-1.5 text-[10px] text-primary-foreground">{pendingCount}</span>}</TabsTrigger>
        <TabsTrigger value="characters"><span className="sm:hidden">Chars</span><span className="hidden sm:inline">Characters</span></TabsTrigger>
        <TabsTrigger value="access">Access</TabsTrigger>
      </TabsList>
      <TabsContent value="roster" className="space-y-3">
        <ErrorAlert text={playersError || knownError} />
        <div className="flex flex-col gap-2 sm:flex-row sm:items-center sm:justify-between">
          <p className="text-xs text-muted-foreground">{players?.length || 0} online · {roster.length} total</p>
          <div className="flex gap-2"><Input aria-label="Search players" className="min-w-0 sm:w-56" value={query} onChange={event => setQuery(event.target.value)} placeholder="Search players…" /><Select value={filter} onValueChange={value => setFilter(value as typeof filter)}><SelectTrigger aria-label="Filter players" className="w-28 shrink-0"><SelectValue /></SelectTrigger><SelectContent><SelectItem value="all">All</SelectItem><SelectItem value="online">Online</SelectItem><SelectItem value="offline">Offline</SelectItem></SelectContent></Select></div>
        </div>
        <div className="overflow-hidden rounded-xl border bg-card/50">
          {visible.map(item => {
            const player = item.player
            const names = item.characters.map(character => character.characterName)
            const fallback = item.known?.name || item.request?.playerName || player?.name || names[0]
            const detail = [player?.name && profiles[steamId(item.id) || ""] ? player.name : null, names.length ? `${names.length} character${names.length === 1 ? "" : "s"}: ${names.join(", ")}` : null, !player && item.known?.lastSeenAt ? `Last seen ${new Date(item.known.lastSeenAt).toLocaleDateString()}` : null].filter(Boolean).join(" · ")
            return <div key={item.id} className="flex flex-wrap items-center gap-2 border-b px-3 py-3 last:border-0 sm:px-4">
              <div className="min-w-0 flex-1 basis-44"><PlayerIdentity id={item.id} fallback={fallback} profiles={profiles} detail={detail} /></div>
              <div className="flex flex-wrap items-center gap-1.5"><Badge variant={player ? "secondary" : "outline"}>{player ? "Online" : "Offline"}</Badge>{item.request?.status === "pending" && <Badge variant="outline">Request pending</Badge>}{item.banned && <Badge variant="destructive">Banned</Badge>}{item.admin && <Badge variant="outline">Admin</Badge>}{item.permitted && <Badge variant="outline">Allowed</Badge>}</div>
              {player && <div className="ml-auto flex items-center gap-1"><Button size="sm" variant="outline" disabled={!player.companion || !player.inventoryAllowed} onClick={() => loadCharacter(player, setInspector)}><UserRoundSearch /> <span className="hidden sm:inline">Inspect</span></Button><DropdownMenu><DropdownMenuTrigger asChild><Button variant="ghost" size="icon-sm" aria-label={`Actions for ${fallback || item.id}`}><MoreHorizontal /></Button></DropdownMenuTrigger><DropdownMenuContent align="end"><DropdownMenuItem onClick={() => setModeration({ player, action: "kick" })}><LogOut /> Kick</DropdownMenuItem><DropdownMenuItem variant="destructive" onClick={() => setModeration({ player, action: "ban" })}><Ban /> Ban</DropdownMenuItem></DropdownMenuContent></DropdownMenu></div>}
            </div>
          })}
          {visible.length === 0 && <EmptyState icon={Users} title={roster.length ? "No matching players" : "No players yet"} detail={roster.length ? "Try another name or status." : "Players appear after they join, request access, or receive a server-owned character."} />}
        </div>
      </TabsContent>
      <TabsContent value="requests"><JoinRequestsPage data={requests} error={requestsError} load={loadRequests} access={access} profiles={profiles} onChanged={refresh} /></TabsContent>
      <TabsContent value="characters"><CharactersPage store={store} error={storeError} loadStore={loadStore} profiles={profiles} /></TabsContent>
      <TabsContent value="access"><AccessPage data={access} error={accessError} load={loadAccess} profiles={profiles} onChanged={refresh} /></TabsContent>
    </Tabs>
    <CharacterDialog state={inspector} onClose={() => setInspector(null)} />
    <Dialog open={moderation !== null} onOpenChange={open => { if (!open) { setModeration(null); setReason("") } }}><DialogContent className="sm:max-w-lg"><DialogHeader><DialogTitle className="capitalize">{moderation?.action} {moderation?.player.name}</DialogTitle><DialogDescription>This action is recorded in the audit log.</DialogDescription></DialogHeader><div className="space-y-2"><Label htmlFor="moderation-reason">Reason</Label><Textarea id="moderation-reason" value={reason} onChange={event => setReason(event.target.value)} maxLength={300} placeholder="No reason provided." /></div><div className="flex justify-end gap-2"><Button variant="outline" onClick={() => setModeration(null)}>Cancel</Button><Button variant={moderation?.action === "ban" ? "destructive" : "default"} disabled={moderating} onClick={moderate}>{moderating ? "Sending…" : moderation?.action === "ban" ? "Ban player" : "Kick player"}</Button></div></DialogContent></Dialog>
  </div>
}

function CharactersPage({ store, error, loadStore, profiles }: { store: CharacterStore | null; error: string; loadStore: () => Promise<void>; profiles: Record<string, SteamProfile> }) {
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
  return <div className="space-y-3"><ErrorAlert text={error} />
    <Card className="gap-3"><CardHeader><CardTitle>Server-owned profiles</CardTitle><CardAction><Badge variant="outline">{store?.characters.length || 0}</Badge></CardAction></CardHeader><CardContent className="grid gap-2 sm:grid-cols-2 xl:grid-cols-3">{store?.characters.map(character => <div key={character.fileName} className="flex min-w-0 items-center gap-3 rounded-lg border bg-muted/20 p-3"><div className="min-w-0 flex-1"><PlayerIdentity id={character.platformId} fallback={character.characterName} profiles={profiles} detail={`${character.characterName} · ${(character.size / 1024).toFixed(1)} KiB`} /></div><time className="hidden text-[10px] text-muted-foreground sm:block">{new Date(character.modifiedAt).toLocaleDateString()}</time></div>)}{store?.characters.length === 0 && <div className="sm:col-span-2 xl:col-span-3"><EmptyState icon={UserRoundSearch} title="No server profiles" detail="Import a native .fch save to add a server-owned character." /></div>}</CardContent></Card>
    <details className="group rounded-xl border bg-card/50"><summary className="flex cursor-pointer list-none items-center gap-2 p-4 text-sm font-medium"><FileUp className="size-4 text-primary" /> Import existing character <ChevronRight className="ml-auto size-4 transition-transform group-open:rotate-90" /></summary><div className="space-y-3 border-t p-4"><p className="text-xs text-muted-foreground">Upload a native Valheim .fch save for its Steam owner. The full character is preserved.</p>
      {store && !store.installed ? <Alert variant="destructive"><Ban /><AlertTitle>Character agent unavailable</AlertTitle><AlertDescription>Reapply the protected Server Manager server agent before importing.</AlertDescription></Alert> :
      store && !store.importAvailable ? <Alert><Server /><AlertTitle>Stop the server first</AlertTitle><AlertDescription className="space-y-3"><span className="block">Imports are only allowed while Valheim is stopped.</span><Button type="button" size="sm" variant="outline" onClick={stopForImport}>Save & stop server</Button></AlertDescription></Alert> : null}
      <form onSubmit={importProfile} className="grid gap-3 sm:grid-cols-2"><div className="space-y-1.5"><Label htmlFor="character-owner">Steam owner</Label><Input id="character-owner" value={ownerId} onChange={event => setOwnerId(event.target.value)} placeholder="76561198000000000 or Steam_…" required /></div><div className="space-y-1.5"><Label htmlFor="character-file">Native character save</Label><Input id="character-file" type="file" accept=".fch" onChange={event => setProfile(event.target.files?.[0] || null)} required /></div><p className="text-xs text-muted-foreground sm:col-span-2">The filename must match the in-game character name, for example MyHero.fch. Maximum 2 MiB.</p><div className="flex items-center justify-between rounded-lg border p-3 sm:col-span-2"><div><p className="text-sm font-medium">Replace existing profile</p><p className="text-xs text-muted-foreground">The current server copy is backed up before replacement.</p></div><Switch checked={overwrite} onCheckedChange={setOverwrite} /></div><Button className="sm:col-span-2 sm:justify-self-start" disabled={!store?.installed || !store.importAvailable || !profile || !ownerId || importing}><FileUp />{importing ? "Importing…" : "Import character"}</Button></form>
    </div></details>
  </div>
}

function AccessListCard({ title, detail, kind, items, profiles, onMutate }: { title: string; detail: string; kind: string; items: string[]; profiles: Record<string, SteamProfile>; onMutate: (kind: string, action: "add" | "remove", id: string) => Promise<void> }) {
  const [value, setValue] = useState("")
  const add = () => { if (value) void onMutate(kind, "add", value).then(() => setValue("")).catch(() => {}) }
  return <Card className="gap-3"><CardHeader><CardTitle>{title} <span className="text-sm font-normal text-muted-foreground">{items.length}</span></CardTitle><CardDescription>{detail}</CardDescription></CardHeader><CardContent className="space-y-3">
    <div className="max-h-80 divide-y overflow-y-auto rounded-lg border">{items.map(id => <div key={id} className="flex items-center gap-2 px-2.5 py-2"><div className="min-w-0 flex-1"><PlayerIdentity id={id} profiles={profiles} /></div><Button size="icon-sm" variant="ghost" aria-label={`Remove ${id}`} title={`Remove ${id}`} onClick={() => { void onMutate(kind, "remove", id).catch(() => {}) }}><X /></Button></div>)}{items.length === 0 && <p className="p-3 text-xs text-muted-foreground">No entries.</p>}</div>
    <div className="flex gap-2"><Input aria-label={`Add to ${title}`} value={value} onChange={event => setValue(event.target.value)} placeholder="Steam ID or platform ID" onKeyDown={event => { if (event.key === "Enter") add() }} /><Button type="button" size="icon" aria-label={`Add to ${title}`} disabled={!value.trim()} onClick={add}><Plus /></Button></div>
  </CardContent></Card>
}

function AccessPage({ data, error, load, profiles, onChanged }: { data: AccessLists | null; error: string; load: () => Promise<void>; profiles: Record<string, SteamProfile>; onChanged: () => void }) {
  const mutate = async (kind: string, action: "add" | "remove", id: string) => {
    try {
      if (action === "add") await post(`/api/v1/access/${kind}/`, { platformId: id })
      else await remove(`/api/v1/access/${kind}/${encodeURIComponent(id)}`)
      toast.success(`${action === "add" ? "Added" : "Removed"} ${id}`)
      await load(); onChanged()
    } catch (cause) { toast.error((cause as Error).message); throw cause }
  }
  return <div className="space-y-3"><Badge variant={data?.whitelistEnabled ? "secondary" : "destructive"}>{data?.whitelistEnabled ? "Whitelist active" : "Whitelist disabled"}</Badge><ErrorAlert text={error} />{data && !data.whitelistEnabled && <p className="rounded-lg border border-amber-400/30 px-3 py-2 text-xs text-amber-200">Add a permitted player to activate admission requests. Until then, anyone can connect.</p>}<div className="grid gap-3 xl:grid-cols-3">{data && <><AccessListCard title="Allowed" detail="Can join the realm" kind="permitted" items={data.permitted} profiles={profiles} onMutate={mutate} /><AccessListCard title="Banned" detail="Denied at connection" kind="banned" items={data.banned} profiles={profiles} onMutate={mutate} /><AccessListCard title="Administrators" detail="Game and dashboard access" kind="admin" items={data.admins} profiles={profiles} onMutate={mutate} /></>}</div></div>
}

function JoinRequestsPage({ data, error, load, access, profiles, onChanged }: { data: JoinRequest[] | null; error: string; load: () => Promise<void>; access: AccessLists | null; profiles: Record<string, SteamProfile>; onChanged: () => void }) {
  const [busy, setBusy] = useState<string | null>(null)
  const resolve = async (item: JoinRequest, decision: "approve" | "deny") => {
    setBusy(item.id)
    try {
      await post(`/api/v1/join-requests/${item.id}/${decision}`)
      toast.success(decision === "approve" ? `${item.playerName || item.platformId} can now reconnect` : "Join request denied")
      await load(); onChanged()
    } catch (cause) { toast.error((cause as Error).message) } finally { setBusy(null) }
  }
  const pending = data?.filter(item => item.status === "pending") || []
  const history = data?.filter(item => item.status !== "pending").slice(0, 50) || []
  return <div className="space-y-3"><p className="text-sm text-muted-foreground">{pending.length} pending</p><ErrorAlert text={error} />
    {access && !access.whitelistEnabled && <p className="rounded-lg border border-amber-400/30 px-3 py-2 text-xs text-amber-200">Requests need an active whitelist. Add a player in Access first.</p>}
    <div className="overflow-hidden rounded-xl border bg-card/50">{pending.map(item => <div key={item.id} className="flex flex-wrap items-center gap-3 border-b p-3 last:border-0 sm:px-4"><div className="min-w-0 flex-1 basis-44"><PlayerIdentity id={item.platformId} fallback={item.playerName || undefined} profiles={profiles} detail={`${item.attemptCount} attempt${item.attemptCount === 1 ? "" : "s"} · Last tried ${new Date(item.lastAttemptAt).toLocaleString()}`} /></div><div className="ml-auto flex gap-2"><Button size="sm" variant="outline" disabled={busy === item.id} onClick={() => resolve(item, "deny")}><X /> Deny</Button><Button size="sm" disabled={busy === item.id} onClick={() => resolve(item, "approve")}><Check /> Approve</Button></div></div>)}{pending.length === 0 && <EmptyState icon={UserPlus} title="No pending requests" detail="New requests will appear here when the whitelist rejects a player." />}</div>
    {history.length > 0 && <details className="rounded-xl border bg-card/50"><summary className="cursor-pointer px-4 py-3 text-sm font-medium">Recent decisions <span className="text-muted-foreground">{history.length}</span></summary><div className="divide-y border-t">{history.map(item => <div key={item.id} className="flex items-center gap-3 px-4 py-2.5"><div className="min-w-0 flex-1"><PlayerIdentity id={item.platformId} fallback={item.playerName || undefined} profiles={profiles} detail={item.resolvedAt ? new Date(item.resolvedAt).toLocaleString() : undefined} /></div><Badge variant={item.status === "approved" ? "secondary" : "destructive"}>{item.status}</Badge></div>)}</div></details>}
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
  return <Dialog open={!!mod} onOpenChange={open => { if (!open && !saving) onClose() }}><DialogContent className="max-h-[94dvh] overflow-y-auto sm:max-w-6xl"><DialogHeader><DialogTitle>{mod ? `${mod.namespace}/${mod.name} configuration` : "Mod configuration"}</DialogTitle><DialogDescription>The Settings editor masks sensitive values. Files provides a package-scoped raw editor with an explicit secrets warning.</DialogDescription></DialogHeader><Tabs defaultValue="settings"><TabsList><TabsTrigger value="settings">Settings</TabsTrigger><TabsTrigger value="files">Files</TabsTrigger></TabsList><TabsContent value="settings" className="pt-3"><ErrorAlert text={error} />
    {loading ? <div className="flex min-h-48 items-center justify-center gap-2 text-sm text-muted-foreground"><RefreshCw className="size-4 animate-spin" /> Loading configuration…</div> : files.length === 0 ? <EmptyState icon={Settings} title="No configuration discovered" detail="Start the server once after installing this mod so BepInEx can create its configuration file and publish the plugin mapping." /> : <ScrollArea className="max-h-[65vh] pr-4"><div className="space-y-5">{files.map(file => <Card key={file.file} size="sm"><CardHeader><CardTitle className="text-base">{file.pluginName || file.file}</CardTitle><CardDescription className="font-mono text-[11px]">{file.file}{file.pluginGuid ? ` · ${file.pluginGuid}` : ""}</CardDescription></CardHeader><CardContent className="space-y-5">{Object.entries(groupConfigEntries(file.entries)).map(([section, entries]) => <section key={section} className="space-y-3"><div className="border-b pb-2"><h3 className="text-sm font-semibold">{section}</h3></div>{entries?.map(entry => { const id = `${file.file}-${entry.section}-${entry.key}`; const kind = entry.settingType.toLowerCase(); const options = entry.acceptableValues.includes(",") ? entry.acceptableValues.split(",").map(value => value.trim()).filter(Boolean) : []; return <div key={id} className="grid gap-2 rounded-lg border bg-muted/15 p-3 md:grid-cols-[minmax(0,1fr)_minmax(220px,.8fr)] md:items-center"><div><Label htmlFor={id}>{entry.key}</Label>{entry.description && <p className="mt-1 text-xs leading-5 text-muted-foreground">{entry.description}</p>}<p className="mt-1 font-mono text-[10px] text-muted-foreground">{entry.settingType}{entry.defaultValue ? ` · default ${entry.defaultValue}` : ""}{entry.acceptableValues ? ` · ${entry.acceptableValues}` : ""}</p></div>{kind === "boolean" ? <div className="flex items-center justify-end gap-3"><span className="text-xs text-muted-foreground">{entry.value || entry.defaultValue}</span><Switch id={id} checked={(entry.value || entry.defaultValue).toLowerCase() === "true"} onCheckedChange={checked => change(file.file, entry, String(checked))} /></div> : options.length > 0 ? <Select value={entry.value} onValueChange={value => change(file.file, entry, value)}><SelectTrigger id={id} className="w-full"><SelectValue placeholder="Choose a value" /></SelectTrigger><SelectContent>{options.map(option => <SelectItem value={option} key={option}>{option}</SelectItem>)}</SelectContent></Select> : <Input id={id} type={entry.sensitive ? "password" : kind.includes("int") || kind.includes("float") || kind.includes("double") ? "number" : "text"} value={entry.value} placeholder={entry.sensitive && entry.hasValue ? "Value is set; enter to replace" : entry.defaultValue} onChange={event => change(file.file, entry, event.target.value)} />}</div>})}</section>)}</CardContent></Card>)}</div></ScrollArea>}
    <div className="flex flex-col-reverse gap-2 border-t pt-4 sm:flex-row sm:justify-end"><Button variant="outline" disabled={saving} onClick={onClose}>Cancel</Button><Button variant="outline" disabled={saving || Object.keys(dirty).length === 0} onClick={() => save(false)}>{saving ? <RefreshCw className="animate-spin" /> : <Settings />} Save for restart</Button><Button disabled={saving || Object.keys(dirty).length === 0} onClick={() => save(true)}>{saving ? <RefreshCw className="animate-spin" /> : <RefreshCw />} Save & restart</Button></div>
    </TabsContent><TabsContent value="files" className="pt-3">{mod && <ModFiles key={mod.id} modId={mod.id} onChanged={onChanged} />}</TabsContent></Tabs>
  </DialogContent></Dialog>
}

function ModsPage({ refreshStatus }: { refreshStatus: () => void }) {
  const { data: mods, error, load } = useLoad<Mod[]>("/api/v1/mods")
  const { data: updates, load: loadUpdates } = useLoad<Record<string, string>>("/api/v1/mods/updates")
  const { data: clientSync, load: loadClientSync } = useLoad<Record<string, { policy: string; effective: string; requiredBy: string[] }>>("/api/v1/mods/client-policies")
  const [clientFilter, setClientFilter] = useState("all")
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
  const setClientPolicy = async (mod: Mod, policy: string) => {
    if (policy === "required" && !window.confirm(`Require ${mod.name} on clients? Players missing the required version will be disconnected and shown the expected package version. They must use their external mod manager and restart Valheim.`)) return
    try { await post(`/api/v1/mods/${mod.id}/client-policy`, { policy }); await loadClientSync(); toast.success(`${mod.name}: client policy saved`) }
    catch (cause) { toast.error((cause as Error).message) }
  }
  const updateCount = Object.keys(updates || {}).length
  return <div className="space-y-6"><PageHeader page="mods" actions={<><Button variant="outline" disabled={busy} onClick={checkUpdates}><RefreshCw /> Check updates</Button>{updateCount > 0 && <Button variant="outline" disabled={busy} onClick={updateAll}><Download /> Stage all ({updateCount})</Button>}<Button asChild variant="outline"><label><FileUp /> Upload ZIP<input hidden type="file" accept=".zip" onChange={upload} /></label></Button><Button disabled={busy} onClick={apply}><RefreshCw className={cn(busy && "animate-spin")} /> Apply & restart</Button></>} /><ErrorAlert text={error} />
    <Alert><ShieldCheck /><AlertTitle>Read-only client compatibility</AlertTitle><AlertDescription>The client checks the active BepInEx profile against the server allowlist. Missing required packages, unlisted packages, and wrong versions cause a disconnect notice. Listed optional packages may be omitted. The server holds world data until a valid acknowledgment. Players change mods through their external mod manager.</AlertDescription></Alert>
    <Card><CardHeader><CardTitle>Thunderstore catalog</CardTitle><CardDescription>Search packages and stage an exact version with its dependency graph.</CardDescription></CardHeader><CardContent><div className="flex gap-2"><div className="relative flex-1"><Search className="absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" /><Input className="pl-9" value={query} onChange={event => setQuery(event.target.value)} onKeyDown={event => event.key === "Enter" && void search()} placeholder="Search Thunderstore packages" /></div><Button variant="outline" onClick={search} disabled={!query}>Search</Button></div>{results.length > 0 && <div className="mt-4 divide-y rounded-lg border">{results.map(item => <button key={item.fullName} onClick={() => install(item)} className="flex w-full items-center justify-between gap-4 p-3 text-left hover:bg-muted/40"><span className="min-w-0"><span className="block truncate text-sm font-medium">{item.fullName}</span><span className="block truncate text-xs text-muted-foreground">{item.description}</span></span><span className="flex shrink-0 items-center gap-2 font-mono text-xs text-muted-foreground">{item.version}<ChevronRight className="size-3.5" /></span></button>)}</div>}</CardContent></Card>
    <div className="grid gap-3 md:grid-cols-3">{[{ key: "required", title: "Mandatory", detail: "Pinned packages and dependencies. Exact versions are required for admission." }, { key: "optional", title: "Optional · not required", detail: "May be omitted; the listed version is allowed if installed." }, { key: "serverOnly", title: "Server only", detail: "Not offered to clients unless needed by a selected package." }].map(group => <button key={group.key} onClick={() => setClientFilter(value => value === group.key ? "all" : group.key)} className={cn("rounded-xl border bg-card p-4 text-left transition-colors hover:bg-muted/30", clientFilter === group.key && "border-primary bg-primary/5")}><div className="flex justify-between text-sm font-semibold"><span>{group.title}</span><span className="font-mono text-primary">{mods?.filter(mod => (clientSync?.[mod.id]?.effective || (mod.protected ? "serverOnly" : "required")) === group.key).length || 0}</span></div><p className="mt-2 text-xs leading-5 text-muted-foreground">{group.detail}</p></button>)}</div><div className="flex items-center justify-between text-xs text-muted-foreground"><span>{clientFilter === "all" ? "All packages" : "Filtered client policy"} · The Server Manager client runtime ships in this package; players update it through their mod manager.</span>{clientFilter !== "all" && <Button variant="ghost" size="sm" onClick={() => setClientFilter("all")}>Show all</Button>}</div>
    <Card className="overflow-hidden py-0"><Table><TableHeader><TableRow><TableHead>Package</TableHead><TableHead>Version</TableHead><TableHead>Source</TableHead><TableHead>Clients</TableHead><TableHead>Status</TableHead><TableHead>Config</TableHead><TableHead className="w-12" /></TableRow></TableHeader><TableBody>{mods?.filter(mod => clientFilter === "all" || (clientSync?.[mod.id]?.effective || (mod.protected ? "serverOnly" : "required")) === clientFilter).map(mod => { const syncable = mod.source === "thunderstore" && !mod.protected; const policy = clientSync?.[mod.id]; return <TableRow key={mod.id}><TableCell><div className="font-medium">{mod.namespace}/{mod.name}</div>{mod.protected && <div className="text-xs text-muted-foreground">Protected system package</div>}</TableCell><TableCell className="font-mono text-xs">{mod.version}{updates?.[mod.id] && <Badge variant="secondary" className="ml-2">{updates[mod.id]} available</Badge>}</TableCell><TableCell>{mod.source}</TableCell><TableCell><div className="min-w-36 space-y-1"><Select value={policy?.policy || (syncable ? "required" : "serverOnly")} disabled={!syncable} onValueChange={value => void setClientPolicy(mod, value)}><SelectTrigger aria-label={`Client policy for ${mod.name}`} className="h-8 w-36"><SelectValue /></SelectTrigger><SelectContent><SelectItem value="required">Required</SelectItem><SelectItem value="optional">Optional · opt-in</SelectItem><SelectItem value="serverOnly">Server only</SelectItem></SelectContent></Select>{!!policy?.requiredBy.length && <p className="max-w-56 text-[10px] text-amber-300" title={policy.requiredBy.join(", ")}>Required dependency of {policy.requiredBy.join(", ")}</p>}</div></TableCell><TableCell><Badge variant={mod.enabled ? "secondary" : "outline"}>{mod.enabled ? "Enabled" : "Disabled"}</Badge></TableCell><TableCell><Button variant="outline" size="sm" disabled={mod.protected} onClick={() => setConfigMod(mod)}><Settings /> Configure</Button></TableCell><TableCell><DropdownMenu><DropdownMenuTrigger asChild><Button variant="ghost" size="icon-sm" disabled={mod.protected}><MoreHorizontal /></Button></DropdownMenuTrigger><DropdownMenuContent align="end"><DropdownMenuLabel>{mod.name}</DropdownMenuLabel>{updates?.[mod.id] && <DropdownMenuItem onClick={() => update(mod)}><Download /> Stage update</DropdownMenuItem>}<DropdownMenuItem onClick={() => toggle(mod)}>{mod.enabled ? <WifiOff /> : <Wifi />}{mod.enabled ? "Disable" : "Enable"}</DropdownMenuItem><DropdownMenuSeparator /><DropdownMenuItem variant="destructive" onClick={async () => { if (confirm(`Remove ${mod.name}?`)) { await remove(`/api/v1/mods/${mod.id}`); await load(); refreshStatus() } }}><Trash2 /> Remove</DropdownMenuItem></DropdownMenuContent></DropdownMenu></TableCell></TableRow> })}</TableBody></Table>{mods?.length === 0 && <EmptyState icon={Boxes} title="No managed mods" detail="Search Thunderstore or upload a validated package ZIP to get started." />}</Card><ModConfigDialog mod={configMod} onClose={() => setConfigMod(null)} onChanged={refreshStatus} />
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
  const { data, error, load } = useLoad<ServerSettingsData>("/api/v1/settings/server-access")
  const [form, setForm] = useState<ServerSettingsData | null>(null)
  const [password, setPassword] = useState("")
  const [busy, setBusy] = useState(false)
  useEffect(() => { if (data) setForm(data) }, [data])
  const set = <K extends keyof ServerSettingsData>(key: K, value: ServerSettingsData[K]) => setForm(current => current ? { ...current, [key]: value } : current)
  const save = async (event: FormEvent) => {
    event.preventDefault()
    if (!form) return
    setBusy(true)
    try {
      const { hasPassword: _hasPassword, port: _port, ...settings } = form
      await post<ServerSettingsData>("/api/v1/settings/server-access", { ...settings, password: password || null })
      setPassword("")
      toast.success("Server settings saved; Valheim restarted")
      await load()
    } catch (cause) { toast.error((cause as Error).message) } finally { setBusy(false) }
  }
  if (!form) return <Card><CardHeader><CardTitle>Server configuration</CardTitle></CardHeader><CardContent><ErrorAlert text={error} /><p className="text-sm text-muted-foreground">Loading settings…</p></CardContent></Card>
  const select = (key: keyof ServerSettingsData, value: string) => set(key, (value === "default" ? "" : value) as never)
  return <Card><CardHeader><CardTitle>Server configuration</CardTitle><CardDescription>Official dedicated-server settings plus an optional modded player-cap override. Saving performs a world save and controlled restart.</CardDescription></CardHeader><CardContent><ErrorAlert text={error} /><form onSubmit={save} className="space-y-6">
    <section className="space-y-4"><div><p className="text-sm font-medium">Realm and networking</p><p className="text-xs text-muted-foreground">Changing the world selects an existing save with that name or creates it on first launch.</p></div><div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
      <div className="space-y-1.5"><Label htmlFor="server-name">Server name</Label><Input id="server-name" maxLength={64} value={form.serverName} onChange={event => set("serverName", event.target.value)} /></div>
      <div className="space-y-1.5"><Label htmlFor="world-name">World name</Label><Input id="world-name" maxLength={64} value={form.worldName} onChange={event => set("worldName", event.target.value)} /></div>
      <div className="space-y-1.5"><Label htmlFor="server-port">Game port</Label><Input id="server-port" value={form.port} disabled /><p className="text-[11px] text-muted-foreground">Deployment-owned; change SERVER_PORT and Docker mappings together.</p></div>
      <div className="space-y-1.5"><Label htmlFor="max-players">Player limit</Label><Input id="max-players" type="number" min={1} max={100} value={form.maxPlayers} onChange={event => set("maxPlayers", Number(event.target.value))} /><p className="text-[11px] text-muted-foreground">Vanilla supports 10. Higher values use VSM's server-side override and are unsupported by Iron Gate.</p></div>
      <div className="space-y-1.5"><Label htmlFor="instance-id">Crossplay instance ID</Label><Input id="instance-id" maxLength={64} value={form.instanceId} onChange={event => set("instanceId", event.target.value)} placeholder="Optional; useful for multiple servers" /></div>
    </div><div className="grid gap-3 sm:grid-cols-2">
      <SettingSwitch title="Crossplay backend" detail="Use PlayFab so supported non-Steam platforms can join. Local/loopback connections are unavailable." checked={form.crossplay} onChange={value => set("crossplay", value)} />
      <SettingSwitch title="Public server listing" detail={form.passwordEnabled ? "Advertise the server in the browser." : "Passwordless servers are always forced unlisted."} checked={form.passwordEnabled && form.publicListing} disabled={!form.passwordEnabled} onChange={value => set("publicListing", value)} />
    </div></section>
    <section className="space-y-4 border-t pt-6"><p className="text-sm font-medium">Connection protection</p><SettingSwitch title="Require a server password" detail={form.passwordEnabled ? "Required for public listing." : "Players join the private server by address or join code."} checked={form.passwordEnabled} onChange={value => set("passwordEnabled", value)} />{form.passwordEnabled && <div className="space-y-1.5 sm:max-w-md"><Label htmlFor="server-password">New password</Label><Input id="server-password" type="password" value={password} onChange={event => setPassword(event.target.value)} placeholder={form.hasPassword ? "Leave blank to keep the current password" : "At least 5 characters"} /><p className="text-[11px] text-muted-foreground">Stored encrypted; at least five characters and not contained in the server name.</p></div>}</section>
    <section className="space-y-4 border-t pt-6"><div><p className="text-sm font-medium">World saves and backups</p><p className="text-xs text-muted-foreground">Intervals are seconds. VSM keeps Valheim's native automatic backup system enabled.</p></div><div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
      <NumberField id="save-interval" label="Save interval" value={form.saveIntervalSeconds} min={60} max={86400} onChange={value => set("saveIntervalSeconds", value)} />
      <NumberField id="backup-count" label="Backups kept" value={form.backupCount} min={1} max={50} onChange={value => set("backupCount", value)} />
      <NumberField id="backup-short" label="First backup age" value={form.backupShortSeconds} min={60} max={604800} onChange={value => set("backupShortSeconds", value)} />
      <NumberField id="backup-long" label="Later backup spacing" value={form.backupLongSeconds} min={60} max={2592000} onChange={value => set("backupLongSeconds", value)} />
    </div></section>
    <section className="space-y-4 border-t pt-6"><SettingSwitch title="Manage gameplay and exploration modifiers" detail="Apply the selected preset and overrides at every startup. Leave off to preserve modifiers already stored in the world." checked={form.manageWorldModifiers} onChange={value => set("manageWorldModifiers", value)} /><div className={cn("space-y-4", !form.manageWorldModifiers && "pointer-events-none opacity-50")}>
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3"><SelectField label="Preset" value={form.preset} options={["normal", "casual", "easy", "hard", "hardcore", "immersive", "hammer"]} onChange={value => select("preset", value)} /><SelectField label="Combat" value={form.combatModifier} options={["default", "veryeasy", "easy", "hard", "veryhard"]} onChange={value => select("combatModifier", value)} /><SelectField label="Death penalty" value={form.deathPenaltyModifier} options={["default", "casual", "veryeasy", "easy", "hard", "hardcore"]} onChange={value => select("deathPenaltyModifier", value)} /><SelectField label="Resources" value={form.resourceModifier} options={["default", "muchless", "less", "more", "muchmore", "most"]} onChange={value => select("resourceModifier", value)} /><SelectField label="Raids" value={form.raidModifier} options={["default", "none", "muchless", "less", "more", "muchmore"]} onChange={value => select("raidModifier", value)} /><SelectField label="Portals" value={form.portalModifier} options={["default", "casual", "hard", "veryhard"]} onChange={value => select("portalModifier", value)} /></div>
      <div className="grid gap-3 sm:grid-cols-2"><SettingSwitch title="No build cost" detail="Building consumes no resources." checked={form.noBuildCost} onChange={value => set("noBuildCost", value)} /><SettingSwitch title="Player-based raids" detail="Raid eligibility follows nearby player progression." checked={form.playerEvents} onChange={value => set("playerEvents", value)} /><SettingSwitch title="Passive enemies" detail="Creatures remain passive until provoked." checked={form.passiveMobs} onChange={value => set("passiveMobs", value)} /><SettingSwitch title="No map" detail="Disable the map for an exploration-focused world." checked={form.noMap} onChange={value => set("noMap", value)} /></div>
    </div></section>
    {form.maxPlayers > 10 && <Alert><Users /><AlertTitle>Modded player capacity</AlertTitle><AlertDescription>Counts above 10 patch Valheim's admission and backend-advertisement limits. Test performance and mod compatibility before inviting a large group.</AlertDescription></Alert>}
    <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-muted/30 p-3 text-xs"><span><strong>{form.serverName}</strong> · {form.worldName} · UDP {form.port}–{form.port + 2}</span><Badge variant="outline">{form.crossplay ? "Crossplay" : "Steam"} · {form.passwordEnabled ? form.publicListing ? "listed" : "unlisted" : "passwordless/unlisted"}</Badge></div>
    <Button disabled={busy}>{busy ? <RefreshCw className="animate-spin" /> : <Server />}{busy ? "Saving and restarting…" : "Save all & restart server"}</Button>
  </form></CardContent></Card>
}

function SettingSwitch({ title, detail, checked, disabled, onChange }: { title: string; detail: string; checked: boolean; disabled?: boolean; onChange: (value: boolean) => void }) {
  return <div className="flex items-center justify-between gap-4 rounded-lg border p-4"><div><p className="text-sm font-medium">{title}</p><p className="text-xs text-muted-foreground">{detail}</p></div><Switch checked={checked} disabled={disabled} onCheckedChange={onChange} /></div>
}

function NumberField({ id, label, value, min, max, onChange }: { id: string; label: string; value: number; min: number; max: number; onChange: (value: number) => void }) {
  return <div className="space-y-1.5"><Label htmlFor={id}>{label}</Label><Input id={id} type="number" min={min} max={max} value={value} onChange={event => onChange(Number(event.target.value))} /></div>
}

function SelectField({ label, value, options, onChange }: { label: string; value: string; options: string[]; onChange: (value: string) => void }) {
  const display = (value || "default").replace(/([a-z])([A-Z])/g, "$1 $2").replace("veryeasy", "very easy").replace("muchless", "much less").replace("muchmore", "much more")
  return <div className="space-y-1.5"><Label>{label}</Label><Select value={value || "default"} onValueChange={onChange}><SelectTrigger className="w-full"><SelectValue /></SelectTrigger><SelectContent>{options.map(option => <SelectItem key={option} value={option}><span className="capitalize">{option === (value || "default") ? display : option.replace("veryeasy", "very easy").replace("muchless", "much less").replace("muchmore", "much more")}</span></SelectItem>)}</SelectContent></Select></div>
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
      await request<ServerCharacterSettings>("/api/v1/settings/server-characters", { method: "PUT", body: JSON.stringify({ enabled, acceptFirstJoinProfile, rejectPreviouslyUsedCharacters, backupsToKeep, clientGraceSeconds, requireInventoryInspection: true }) })
      toast.success("Client admission policy saved")
      await load()
    } catch (cause) { toast.error((cause as Error).message) } finally { setBusy(false) }
  }
  return <Card><CardHeader><CardTitle>Client compatibility</CardTitle><CardDescription>Inventory inspection is required for every player. Configure server-owned characters separately.</CardDescription></CardHeader><CardContent><ErrorAlert text={error} /><form onSubmit={save} className="space-y-4"><Alert><ShieldCheck /><AlertTitle>Live inspection required</AlertTitle><AlertDescription>Every player needs the Server Manager client runtime with Privacy → AllowInventoryInspection enabled. Clients that refuse sharing are disconnected after the handshake grace period.</AlertDescription></Alert><SettingSwitch title="Server-owned characters" detail="Load the server's native character profile before spawn and save checkpoints during play." checked={enabled} onChange={setEnabled} /><div className="flex items-center justify-between rounded-lg border p-4"><div><p className="text-sm font-medium">Accept a profile on first join</p><p className="text-xs text-muted-foreground">When disabled, an administrator must import the player's .fch save before they can enter.</p></div><Switch checked={acceptFirstJoinProfile} disabled={!enabled} onCheckedChange={setAcceptFirstJoinProfile} /></div><div className="flex items-center justify-between rounded-lg border p-4"><div><p className="text-sm font-medium">Reject previously used characters</p><p className="text-xs text-muted-foreground">Require a new character with no existing world history on first join.</p></div><Switch checked={rejectPreviouslyUsedCharacters} disabled={!enabled || !acceptFirstJoinProfile} onCheckedChange={setRejectPreviouslyUsedCharacters} /></div><div className="grid gap-4 sm:grid-cols-2"><div className="space-y-1.5"><Label htmlFor="character-backups">Character backups</Label><Input id="character-backups" type="number" min={1} max={50} value={backupsToKeep} disabled={!enabled} onChange={event => setBackupsToKeep(Number(event.target.value))} /><p className="text-[11px] text-muted-foreground">Rolling native saves per character (1–50).</p></div><div className="space-y-1.5"><Label htmlFor="client-grace">Client handshake grace</Label><Input id="client-grace" type="number" min={5} max={120} value={clientGraceSeconds} onChange={event => setClientGraceSeconds(Number(event.target.value))} /><p className="text-[11px] text-muted-foreground">Seconds before a missing runtime or refused sharing is rejected (5–120).</p></div></div><Button disabled={busy}>{busy ? <RefreshCw className="animate-spin" /> : <Shield />}{busy ? "Applying…" : "Save compatibility mode"}</Button></form></CardContent></Card>
}

const messageFields: { key: keyof ServerMessages; label: string; placeholders: string; detail: string }[] = [
  { key: "welcome", label: "Welcome", placeholders: "{player} {server}", detail: "A quiet notice shown once the character is ready." },
  { key: "kick", label: "Kick", placeholders: "{player} {server} {reason}", detail: "Sent before a kick; remains readable at the menu on compatible clients." },
  { key: "ban", label: "Ban", placeholders: "{player} {server} {reason}", detail: "Sent before an online ban; remains readable at the menu on compatible clients." },
  { key: "restart", label: "Restart", placeholders: "{server} {seconds} {reason}", detail: "Broadcast for delayed safe-console restarts." },
  { key: "whitelistRejected", label: "Whitelist rejection", placeholders: "{player} {server}", detail: "Explain how to get approval. The bundled Server Manager client can show this while the connection is starting." },
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
  return <div className="space-y-6"><PageHeader page="settings" /><Tabs defaultValue="server"><TabsList className="grid w-full grid-cols-3 gap-1 group-data-horizontal/tabs:h-auto sm:inline-flex sm:w-fit [&_[data-slot=tabs-trigger]]:h-7"><TabsTrigger value="server">Server</TabsTrigger><TabsTrigger value="updates">Updates</TabsTrigger><TabsTrigger value="messages">Messages</TabsTrigger><TabsTrigger value="api">API tokens</TabsTrigger><TabsTrigger value="privacy">Privacy</TabsTrigger><TabsTrigger value="artifacts">Artifacts</TabsTrigger></TabsList><TabsContent value="server" className="space-y-4 pt-2"><ServerAccessSettingsCard /><ServerCharacterSettingsCard /></TabsContent><TabsContent value="updates" className="pt-2"><ManagerUpdateSettingsCard /></TabsContent><TabsContent value="messages" className="pt-2"><ServerMessagesSettingsCard /></TabsContent><TabsContent value="api" className="pt-2"><ApiTokensSettingsCard /></TabsContent><TabsContent value="privacy" className="pt-2"><Card><CardHeader><CardTitle>Privacy defaults</CardTitle><CardDescription>Inventory inspection is required; other collection remains limited.</CardDescription></CardHeader><CardContent className="divide-y rounded-lg border bg-background/40 px-4">{[["Chat webhooks", "Disabled until explicitly filtered"], ["Inventory inspection", "Required client sharing; admin-only live view, never persisted"], ["Server character", "Optional; disabled by default"], ["Private whispers", "Never captured"], ["Detailed telemetry", "Opt-in on each client"]].map(([label, value]) => <div className="flex flex-col justify-between gap-1 py-3 sm:flex-row sm:items-center" key={label}><span className="text-sm font-medium">{label}</span><span className="text-sm text-muted-foreground">{value}</span></div>)}</CardContent></Card></TabsContent><TabsContent value="artifacts" className="pt-2"><Card><CardHeader><CardTitle>Server Manager client package</CardTitle><CardDescription>The server agent is installed by Docker. Every player needs this client package to join.</CardDescription></CardHeader><CardContent><Button asChild className="h-auto justify-start p-4"><a href="/api/v1/downloads/plugin"><PackageCheck className="size-5" /><span className="text-left"><span className="block">Valheim Server Manager 2.3.1</span><span className="block text-xs font-normal opacity-80">Read-only mod check and client features</span></span></a></Button></CardContent></Card></TabsContent></Tabs></div>
}

function Dashboard({ auth }: { auth: AuthState }) {
  const [page, setPage] = useState<PageId>("overview")
  const [status, setStatus] = useState<Status>({ status: "loading", uptimeSeconds: 0, agentConnected: false, agentVersion: "", gameVersion: "", restartRequired: false, players: 0 })
  const [liveLogs, setLiveLogs] = useState<Log[]>([])
  const loadedManagerVersion = useRef<string | null>(null)
  const adminProfiles = useSteamProfiles(auth.steamId ? [auth.steamId] : [])
  const adminProfile = adminProfiles[auth.steamId || ""]
  const refreshStatus = useCallback(() => { void request<Status>("/api/v1/status").then(setStatus).catch(() => {}) }, [])
  useEffect(() => { refreshStatus(); const connection = new HubConnectionBuilder().withUrl("/hubs/live").withAutomaticReconnect().configureLogging(LogLevel.Warning).build(); connection.on("status", setStatus); connection.on("log", (log: Log) => setLiveLogs(items => [...items.slice(-999), log])); connection.start().catch(console.error); return () => { void connection.stop() } }, [refreshStatus])
  useEffect(() => { const check = () => request<ManagerUpdate>("/api/v1/manager-update").then(update => { if (loadedManagerVersion.current && loadedManagerVersion.current !== update.currentVersion) window.location.reload(); loadedManagerVersion.current = update.currentVersion }).catch(() => {}); void check(); const timer = window.setInterval(check, 30_000); return () => window.clearInterval(timer) }, [])
  const logout = async () => { await post("/api/v1/auth/logout"); window.location.assign("/") }
  const content = page === "files" ? <ModFileManagerPage onChanged={refreshStatus} /> : page === "overview" ? <Overview status={status} refresh={refreshStatus} /> : page === "players" ? <PlayersPage /> : page === "mods" ? <ModsPage refreshStatus={refreshStatus} /> : page === "webhooks" ? <WebhooksPage /> : page === "console" ? <ConsolePage liveLogs={liveLogs} /> : page === "audit" ? <AuditPage /> : <SettingsPage />
  return <SidebarProvider style={{ "--sidebar-width": "calc(var(--spacing) * 72)", "--header-height": "calc(var(--spacing) * 12)" } as React.CSSProperties}>
    <AppSidebar variant="inset" items={navigation} activeId={page} onNavigate={id => setPage(id as PageId)} userName={adminProfile?.name || "Steam admin"} steamId={auth.steamId} avatarUrl={adminProfile?.avatarUrl} onlinePlayers={status.players} agentConnected={status.agentConnected} onLogout={logout} />
    <SidebarInset>
      <SiteHeader status={status.status} onRefresh={refreshStatus} />
      <div className="flex flex-1 flex-col"><div className="@container/main flex flex-1 flex-col"><div className="py-3 md:py-4"><main className="mx-auto w-full max-w-[1500px] px-3 sm:px-4 lg:px-6">{content}</main></div></div></div>
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
