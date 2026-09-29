import { useCallback, useEffect, useState } from "react"
import { Area, AreaChart, CartesianGrid, XAxis, YAxis } from "recharts"
import { Activity, Clock3, Cpu, MemoryStick, Radio, RefreshCw, Server, Users } from "lucide-react"
import { toast } from "sonner"

import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader } from "@/components/ui/card"
import { ChartContainer, ChartTooltip, ChartTooltipContent } from "@/components/ui/chart"
import { cn } from "@/lib/utils"
import { post, request } from "@/api"

export type MonitorStatus = {
  status: string; startedAt?: string; uptimeSeconds: number; agentConnected: boolean
  agentVersion: string; gameVersion: string; restartRequired: boolean; players: number
}
type Sample = { at: string; players: number; cpuPercent: number | null; memoryBytes: number | null }
type Event = { id: string; type: string; occurredAt: string; player: string | null }
type Monitor = { samples: Sample[]; events: Event[] }
type Range = "15m" | "1h"
type TrendMetric = "players" | "cpuPercent" | "memoryMiB"
type TrendSample = Sample & { memoryMiB: number | null }

const chartConfig = {
  players: { label: "Players", color: "var(--chart-1)" },
  cpuPercent: { label: "CPU", color: "var(--chart-3)" },
  memoryMiB: { label: "RAM", color: "var(--chart-2)" },
}

const time = (value: string) => new Date(value).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
const memory = (bytes: number | null | undefined) => bytes == null ? "—" : `${(bytes / 1048576).toFixed(0)} MB`
const duration = (seconds: number) => {
  if (!seconds) return "—"
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor(seconds % 86400 / 3600)
  const minutes = Math.floor(seconds % 3600 / 60)
  return days ? `${days}d ${hours}h` : hours ? `${hours}h ${minutes}m` : minutes ? `${minutes}m` : "<1m"
}
const eventLabel = (type: string) => type.split(".").map(word => word[0]?.toUpperCase() + word.slice(1).replace(/-/g, " ")).join(" · ")

function Metric({ label, value, detail, icon: Icon, tone }: { label: string; value: string; detail: string; icon: typeof Activity; tone?: string }) {
  return <div className="rounded-xl border border-border/70 bg-card px-4 py-4 sm:px-5">
    <div className="flex items-center justify-between gap-2 text-xs text-muted-foreground"><span>{label}</span><Icon className={cn("size-4", tone || "text-muted-foreground")} /></div>
    <div className="mt-3 text-[1.65rem] font-semibold leading-none tracking-tight tabular-nums">{value}</div>
    <div className="mt-2 truncate text-xs text-muted-foreground">{detail}</div>
  </div>
}

const metricValue = (metric: TrendMetric, value: number) => {
  if (metric === "players") return `${value} ${value === 1 ? "player" : "players"}`
  if (metric === "cpuPercent") return `${value.toFixed(1)}%`
  return `${Math.round(value).toLocaleString()} MB`
}

// Use a few round ticks so small CPU values fill the plot and labels stay readable.
function chartTicks(metric: TrendMetric, peak: number) {
  if (peak === 0) return metric === "memoryMiB" ? [0, 128, 256] : metric === "cpuPercent" ? [0, 0.5, 1] : [0, 1]
  const roughStep = peak / 3
  const magnitude = 10 ** Math.floor(Math.log10(roughStep))
  const step = [1, 2, 5, 10].map(value => value * magnitude).find(value => value >= roughStep) ?? 10 * magnitude
  const interval = metric === "players" ? Math.max(1, Math.ceil(step)) : step
  const upper = metric === "cpuPercent" ? Math.min(100, Math.ceil(peak * 1.08 / interval) * interval) : Math.ceil(peak * 1.08 / interval) * interval
  return Array.from({ length: Math.round(upper / interval) + 1 }, (_, index) => Number((index * interval).toFixed(3)))
}

function TrendChart({ title, detail, data, metric, empty }: {
  title: string; detail: string; data: TrendSample[]; metric: TrendMetric; empty: string
}) {
  const color = chartConfig[metric].color
  const id = `fill-${metric}`
  const values = data.map(point => point[metric]).filter((value): value is number => value != null)
  const current = data.at(-1)?.[metric]
  const peak = Math.max(0, ...values)
  const ticks = chartTicks(metric, peak)
  const hasData = data.length >= 2 && values.length > 0
  return <Card className="min-w-0 border border-border/70 bg-card shadow-none ring-0">
    <CardHeader className="flex flex-row items-start justify-between gap-3 pb-1">
      <div className="min-w-0"><h2 className="text-sm font-semibold">{title}</h2><p className="mt-1 text-xs text-muted-foreground">{detail}</p></div>
      {hasData && <div className="shrink-0 text-right"><div className="text-lg font-semibold leading-none tabular-nums" style={{ color }}>{current == null ? "—" : metricValue(metric, current)}</div><div className="mt-1 text-[11px] text-muted-foreground">Current</div></div>}
    </CardHeader>
    <CardContent className="pt-2">
      {!hasData ? <div className="flex h-[210px] items-center justify-center text-sm text-muted-foreground">{empty}</div> :
        <ChartContainer config={chartConfig} className="h-[210px] w-full aspect-auto">
          <AreaChart data={data} margin={{ top: 10, right: 10, bottom: 0, left: 0 }} accessibilityLayer>
            <defs><linearGradient id={id} x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stopColor={color} stopOpacity={0.18} /><stop offset="100%" stopColor={color} stopOpacity={0} /></linearGradient></defs>
            <CartesianGrid vertical={false} stroke="var(--border)" strokeOpacity={0.45} />
            <XAxis dataKey="at" axisLine={false} tickLine={false} tickMargin={10} minTickGap={50} tickFormatter={time} />
            <YAxis axisLine={false} tickLine={false} tickMargin={8} width={metric === "memoryMiB" ? 48 : 42} domain={[0, ticks.at(-1)!]} ticks={ticks} tickFormatter={value => metric === "cpuPercent" ? `${value}%` : Number(value).toLocaleString()} allowDecimals={metric !== "players"} />
            <ChartTooltip cursor={{ stroke: "var(--border)" }} content={<ChartTooltipContent labelFormatter={(_, payload) => payload?.[0]?.payload?.at ? time(payload[0].payload.at) : ""} formatter={(value) => metricValue(metric, Number(value))} />} />
            <Area dataKey={metric} type={metric === "players" ? "stepAfter" : "linear"} connectNulls={false} stroke={color} strokeWidth={2} fill={`url(#${id})`} isAnimationActive={false} />
          </AreaChart>
        </ChartContainer>}
      {hasData && <div className="mt-3 flex items-center justify-between border-t border-border/50 pt-2 text-[11px] text-muted-foreground"><span>{metric === "players" && peak === 0 ? "No players in this range" : "Peak in this range"}</span><span className="font-medium tabular-nums text-foreground">{metricValue(metric, peak)}</span></div>}
    </CardContent>
  </Card>
}

export function MonitorDashboard({ status, refresh, isAdmin }: { status: MonitorStatus; refresh: () => void; isAdmin: boolean }) {
  const [monitor, setMonitor] = useState<Monitor | null>(null)
  const [error, setError] = useState("")
  const [range, setRange] = useState<Range>("1h")
  const load = useCallback(async () => {
    try { setMonitor(await request<Monitor>("/api/v1/monitor")); setError("") }
    catch (cause) { setError((cause as Error).message) }
  }, [])
  useEffect(() => { void load(); const timer = window.setInterval(() => { void load(); refresh() }, 5000); return () => window.clearInterval(timer) }, [load, refresh])

  const action = (name: "start" | "stop" | "restart") => {
    const promise = post(`/api/v1/server/${name}`).then(() => { refresh(); void load() })
    toast.promise(promise, { loading: `Requesting ${name}…`, success: `Server ${name} requested`, error: (cause) => (cause as Error).message })
  }
  const latest = monitor?.samples.at(-1)
  const cutoff = Date.now() - (range === "15m" ? 15 : 60) * 60_000
  const data = (monitor?.samples || []).filter(point => Date.parse(point.at) >= cutoff).map(point => ({ ...point, memoryMiB: point.memoryBytes == null ? null : Math.round(point.memoryBytes / 1048576) }))
  const online = status.status === "running"

  return <div className="space-y-5 pb-8">
    <header className="flex flex-wrap items-end justify-between gap-4 border-b border-border/70 pb-5">
      <div><p className="mb-1 text-[11px] font-semibold uppercase tracking-[.18em] text-primary">Server operations</p><h1 className="text-2xl font-semibold tracking-tight sm:text-3xl">Monitor</h1><p className="mt-1 text-sm text-muted-foreground">Live activity and resource usage for your Valheim server.</p></div>
      <div className="flex flex-wrap items-center gap-2">
        <span className={cn("mr-1 inline-flex items-center gap-2 text-xs font-medium capitalize", online ? "text-emerald-400" : "text-amber-400")}><span className={cn("size-2 rounded-full", online ? "bg-emerald-400" : "bg-amber-400")} />{status.status}</span>
        {isAdmin && <Button variant="outline" size="sm" disabled={!online} onClick={() => action("restart")}><RefreshCw /> Restart</Button>}
        {isAdmin && <Button size="sm" onClick={() => action(online ? "stop" : "start")}>{online ? "Stop server" : "Start server"}</Button>}
      </div>
    </header>

    {error && <div role="alert" className="rounded-lg border border-destructive/30 px-4 py-3 text-sm text-destructive">Monitor unavailable: {error}</div>}

    <section aria-label="Realtime stats" className="space-y-3">
      <div className="flex items-center gap-2"><Radio className="size-3.5 text-primary" /><h2 className="text-xs font-semibold uppercase tracking-[.12em] text-muted-foreground">Realtime stats</h2><span className="ml-auto text-xs text-muted-foreground">Updates every 5 seconds</span></div>
      <div className="grid grid-cols-2 gap-3 xl:grid-cols-4">
        <Metric label="Players online" value={String(status.players)} detail="Connected now" icon={Users} tone="text-primary" />
        <Metric label="CPU usage" value={latest?.cpuPercent == null ? "—" : `${latest.cpuPercent.toFixed(1)}%`} detail="Valheim process" icon={Cpu} tone="text-sky-400" />
        <Metric label="RAM usage" value={memory(latest?.memoryBytes)} detail="Valheim working set" icon={MemoryStick} tone="text-amber-400" />
        <Metric label="Uptime" value={duration(status.uptimeSeconds)} detail={status.restartRequired ? "Restart required" : "Since last start"} icon={Clock3} tone={status.restartRequired ? "text-amber-400" : "text-muted-foreground"} />
      </div>
    </section>

    <div className="flex flex-wrap items-center justify-between gap-3 pt-1">
      <div><h2 className="text-sm font-semibold">Trends</h2><p className="text-xs text-muted-foreground">Samples are kept for the last hour while the manager is running.</p></div>
      <div className="inline-flex rounded-lg border border-border/70 bg-card p-0.5" role="group" aria-label="Chart time range">
        {(["15m", "1h"] as const).map(value => <button key={value} type="button" aria-pressed={range === value} onClick={() => setRange(value)} className={cn("rounded-md px-3 py-1.5 text-xs font-medium transition-colors focus-visible:outline-2 focus-visible:outline-primary", range === value ? "bg-muted text-foreground" : "text-muted-foreground hover:text-foreground")}>{value === "15m" ? "15 min" : "1 hour"}</button>)}
      </div>
    </div>

    <section aria-label="Monitor charts" className="grid gap-3 lg:grid-cols-2">
      <TrendChart title="Player timeline" detail="Concurrent players" data={data} metric="players" empty="Collecting player samples…" />
      <TrendChart title="CPU usage" detail="Share of total host CPU capacity" data={data} metric="cpuPercent" empty="CPU samples appear while the server is running." />
      <TrendChart title="RAM usage" detail="Valheim process memory" data={data} metric="memoryMiB" empty="RAM samples appear while the server is running." />
      <Card className="min-w-0 border border-border/70 bg-card shadow-none ring-0">
        <CardHeader className="flex flex-row items-start justify-between gap-3 pb-1"><div><h2 className="text-sm font-semibold">Recent events</h2><p className="mt-1 text-xs text-muted-foreground">Latest server and player activity</p></div><Activity className="size-4 text-muted-foreground" /></CardHeader>
        <CardContent className="min-h-[210px] pt-2">
          {monitor?.events.length ? <div className="max-h-[246px] divide-y divide-border/60 overflow-y-auto pr-1">{monitor.events.slice(0, 12).map(event => <div key={event.id} className="flex items-center gap-3 py-2.5 first:pt-0"><span className="size-1.5 shrink-0 rounded-full bg-primary/75" /><div className="min-w-0 flex-1"><p className="truncate text-xs font-medium">{eventLabel(event.type)}</p>{event.player && <p className="truncate text-[11px] text-muted-foreground">{event.player}</p>}</div><time className="shrink-0 text-[11px] tabular-nums text-muted-foreground" dateTime={event.occurredAt}>{time(event.occurredAt)}</time></div>)}</div> : <div className="flex h-[210px] items-center justify-center text-sm text-muted-foreground">Events will appear here as they happen.</div>}
        </CardContent>
      </Card>
    </section>

    <div className="flex flex-wrap gap-x-5 gap-y-1 border-t border-border/60 pt-4 text-xs text-muted-foreground"><span className="flex items-center gap-1.5"><Server className="size-3.5" /> Agent {status.agentConnected ? "connected" : "disconnected"}{status.agentVersion && ` · ${status.agentVersion}`}</span><span>Game {status.gameVersion || "—"}</span>{status.startedAt && <span>Started {new Date(status.startedAt).toLocaleString()}</span>}</div>
  </div>
}
