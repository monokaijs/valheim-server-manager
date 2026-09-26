import { useEffect, useMemo, useState } from "react"
import { Gift, LoaderCircle } from "lucide-react"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { request } from "../api"

type CatalogItem = { prefab: string; name: string; maxStack: number; maxQuality: number }
type Catalog = { ok: boolean; items: CatalogItem[] }
type GiveResult = { given: number; requested: number; error?: string }

export function GiveItem({ peerKey, enabled }: { peerKey: string; enabled: boolean }) {
  const [items, setItems] = useState<CatalogItem[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState("")
  const [query, setQuery] = useState("")
  const [selected, setSelected] = useState<CatalogItem | null>(null)
  const [open, setOpen] = useState(false)
  const [active, setActive] = useState(0)
  const [quality, setQuality] = useState(1)
  const [quantity, setQuantity] = useState(1)
  const [sending, setSending] = useState(false)
  const [message, setMessage] = useState("")
  const [retry, setRetry] = useState(0)

  useEffect(() => {
    let cancelled = false
    setItems([]); setSelected(null); setQuery(""); setError(""); setMessage(""); setLoading(true)
    void request<Catalog>(`/api/v1/players/${encodeURIComponent(peerKey)}/items`)
      .then(result => { if (!cancelled) setItems(result.items) })
      .catch((cause: Error) => { if (!cancelled) setError(cause.message) })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
  }, [peerKey, retry])

  const matches = useMemo(() => {
    const term = query.trim().toLowerCase()
    return items.filter(item => `${item.name} ${item.prefab}`.toLowerCase().includes(term)).slice(0, 12)
  }, [items, query])
  const choose = (item: CatalogItem) => {
    setSelected(item); setQuery(`${item.name} (${item.prefab})`); setQuality(value => Math.min(value, item.maxQuality)); setOpen(false); setMessage("")
  }
  const give = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!selected || !enabled || sending) return
    setSending(true); setMessage("")
    try {
      const result = await request<GiveResult>(`/api/v1/players/${encodeURIComponent(peerKey)}/give`, {
        method: "POST", body: JSON.stringify({ prefab: selected.prefab, quantity, quality }),
      })
      setMessage(result.given === result.requested ? `Gave ${result.given} × ${selected.name}.` : `Gave ${result.given} of ${result.requested} × ${selected.name}. ${result.error || "Inventory is full."}`)
    } catch (cause) { setMessage((cause as Error).message) }
    finally { setSending(false) }
  }

  return <form onSubmit={give} className="mb-4 rounded-xl border bg-card p-4">
    <div className="mb-3 flex items-center gap-2"><Gift className="size-4 text-primary" /><h3 className="text-sm font-semibold">Give item</h3></div>
    <div className="grid gap-3 sm:grid-cols-[minmax(0,1fr)_100px_100px_auto] sm:items-end">
      <div className="relative min-w-0"><label htmlFor="give-item" className="mb-1 block text-xs text-muted-foreground">Item</label><Input id="give-item" role="combobox" aria-autocomplete="list" aria-expanded={open && matches.length > 0} aria-controls="give-item-options" autoComplete="off" placeholder={loading ? "Loading items…" : "Search name or prefab…"} value={query} disabled={loading || !!error || !enabled || sending} onFocus={() => setOpen(true)} onBlur={() => window.setTimeout(() => setOpen(false), 150)} onChange={event => { setQuery(event.target.value); setSelected(null); setActive(0); setOpen(true); setMessage("") }} onKeyDown={event => {
        if (event.key === "Escape") setOpen(false)
        if (event.key === "ArrowDown" || event.key === "ArrowUp") { event.preventDefault(); setOpen(true); setActive(value => Math.max(0, Math.min(matches.length - 1, value + (event.key === "ArrowDown" ? 1 : -1)))) }
        if (event.key === "Enter" && open && matches.length > 0) { event.preventDefault(); choose(matches[active] || matches[0]) }
      }} />{open && matches.length > 0 && <div id="give-item-options" role="listbox" className="absolute z-20 mt-1 max-h-64 w-full overflow-y-auto rounded-md border bg-popover p-1 shadow-lg">{matches.map((item, index) => <button type="button" role="option" aria-selected={index === active} key={item.prefab} onMouseDown={event => event.preventDefault()} onClick={() => choose(item)} className={`block w-full rounded px-2 py-1.5 text-left text-xs hover:bg-accent ${index === active ? "bg-accent" : ""}`}><span className="block font-medium">{item.name}</span><span className="font-mono text-muted-foreground">{item.prefab}</span></button>)}</div>}</div>
      <div><label htmlFor="give-quality" className="mb-1 block text-xs text-muted-foreground">Quality</label><Input id="give-quality" type="number" min={1} max={selected?.maxQuality || 1} value={quality} disabled={!selected || !enabled || sending} onChange={event => setQuality(Number(event.target.value))} required /></div>
      <div><label htmlFor="give-quantity" className="mb-1 block text-xs text-muted-foreground">Quantity</label><Input id="give-quantity" type="number" min={1} max={1000} value={quantity} disabled={!selected || !enabled || sending} onChange={event => setQuantity(Number(event.target.value))} required /></div>
      <Button type="submit" disabled={!selected || !enabled || sending || !Number.isInteger(quality) || quality < 1 || quality > (selected?.maxQuality || 1) || !Number.isInteger(quantity) || quantity < 1 || quantity > 1000}>{sending ? <LoaderCircle className="animate-spin" /> : <Gift />}Give</Button>
    </div>
    {error && <div role="alert" className="mt-2 flex items-center gap-2 text-xs text-destructive"><span>{error}</span><Button type="button" size="sm" variant="outline" onClick={() => setRetry(value => value + 1)}>Retry</Button></div>}
    {message && <p role="status" className="mt-2 text-xs">{message}</p>}
    {!enabled && !error && <p className="mt-2 text-xs text-muted-foreground">Wait for a live inventory sample to give an item.</p>}
  </form>
}
