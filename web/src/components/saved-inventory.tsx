import { useEffect, useState } from "react"
import { request } from "../api"
import { Button } from "./ui/button"
import { Input } from "./ui/input"

type Item = { prefab: string; name: string; x: number; y: number; stack: number; quality: number; durability: number; maxStack: number; maxQuality: number }
type Edit = { id: string; action: string; status: string; createdAt: string; error: string; payloadJson: string }
type Archive = { capturedAt?: string; snapshot?: { character: { name: string }; items: Item[] }; edits: Edit[] }
const editDetail = (edit: Edit) => { try { const value = JSON.parse(edit.payloadJson); return `${value.prefab}${edit.action === "give" ? ` ×${value.quantity}` : ` · slot ${value.x + 1},${value.y + 1}`}` } catch { return "" } }

export function SavedInventory({ platformId }: { platformId: string }) {
  const [archive, setArchive] = useState<Archive | null>(null)
  const [error, setError] = useState("")
  const [busy, setBusy] = useState(false)
  const [prefab, setPrefab] = useState("")
  const [quantity, setQuantity] = useState(1)
  const [quality, setQuality] = useState(1)
  const [selected, setSelected] = useState<Item | null>(null)
  const [stack, setStack] = useState(1)
  const [itemQuality, setItemQuality] = useState(1)
  const [durability, setDurability] = useState(0)
  const url = `/api/v1/players/${encodeURIComponent(platformId)}/saved-inventory`
  const refresh = () => request<Archive>(url).then(setArchive).catch((cause: Error) => setError(cause.message))
  useEffect(() => {
    setArchive(null); setSelected(null); setError(""); void refresh()
    const timer = window.setInterval(() => { void refresh() }, 5000)
    return () => window.clearInterval(timer)
  }, [platformId])
  const choose = (item: Item) => { setSelected(item); setStack(item.stack); setItemQuality(item.quality); setDurability(item.durability) }
  const queue = async (action: "give" | "replace" | "remove") => {
    setBusy(true); setError("")
    try {
      await request(`${url}/edits`, { method: "POST", body: JSON.stringify({
        action, prefab: action === "give" ? prefab : selected?.prefab, quantity, quality: action === "give" ? quality : itemQuality,
        x: selected?.x || 0, y: selected?.y || 0, targetCharacter: action === "give" ? null : archive?.snapshot?.character.name,
        expectedStack: selected?.stack || 0,
        expectedQuality: selected?.quality || 0, stack, durability,
      }) })
      setSelected(null)
      await refresh()
    } catch (cause) { setError((cause as Error).message) }
    finally { setBusy(false) }
  }
  return <div className="h-full overflow-y-auto p-4 sm:p-6"><div className="mb-4 flex items-start justify-between gap-3"><div><h3 className="font-semibold">Last known inventory</h3><p className="text-xs text-muted-foreground">This player keeps their character locally. Offline edits are queued and applied when they next join.</p></div><Button size="sm" variant="outline" onClick={() => void refresh()}>Refresh</Button></div>
    {error && <p role="alert" className="mb-3 text-sm text-destructive">{error}</p>}
    {archive?.capturedAt && <p className="mb-3 text-xs text-muted-foreground">Snapshot from {new Date(archive.capturedAt).toLocaleString()}. The player may have changed items since then.</p>}
    <div className="mb-4 rounded-xl border bg-card p-4"><h4 className="mb-3 text-sm font-semibold">Queue item grant</h4><div className="flex flex-wrap items-end gap-2"><label className="min-w-40 flex-1 text-xs">Exact prefab<Input value={prefab} onChange={event => setPrefab(event.target.value)} placeholder="Wood" /></label><label className="w-24 text-xs">Quantity<Input type="number" min={1} max={1000} value={quantity} onChange={event => setQuantity(Number(event.target.value))} /></label><label className="w-24 text-xs">Quality<Input type="number" min={1} max={100} value={quality} onChange={event => setQuality(Number(event.target.value))} /></label><Button disabled={busy || !prefab.trim() || quantity < 1 || quality < 1} onClick={() => void queue("give")}>Queue give</Button></div></div>
    <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_260px]"><section className="rounded-xl border bg-card p-4"><h4 className="mb-3 text-sm font-semibold">Saved snapshot</h4>{archive?.snapshot ? <div className="grid grid-cols-8 gap-1">{Array.from({ length: 32 }, (_, index) => { const x = index % 8, y = Math.floor(index / 8), item = archive.snapshot?.items.find(entry => entry.x === x && entry.y === y); return <button key={index} type="button" title={item ? `${item.name} ×${item.stack}` : "Empty slot"} className="min-h-14 min-w-0 rounded border bg-muted/20 p-1 text-left text-[10px] hover:border-primary" onClick={() => item ? choose(item) : setSelected(null)}>{item && <><span className="block truncate">{item.name}</span><span>×{item.stack} · Q{item.quality}</span></>}</button> })}</div> : <p className="text-xs text-muted-foreground">No snapshot yet. One is saved after the player connects with inventory sharing enabled.</p>}</section>
      <section className="rounded-xl border bg-card p-4"><h4 className="mb-3 text-sm font-semibold">Queue item edit</h4>{selected ? <div className="space-y-2 text-xs"><p className="break-all font-mono">{selected.prefab} · slot {selected.x + 1},{selected.y + 1}</p><label className="block">Stack<Input type="number" min={1} max={selected.maxStack} value={stack} onChange={event => setStack(Number(event.target.value))} /></label><label className="block">Quality<Input type="number" min={1} max={selected.maxQuality} value={itemQuality} onChange={event => setItemQuality(Number(event.target.value))} /></label><label className="block">Durability<Input type="number" min={0} step="any" value={durability} onChange={event => setDurability(Number(event.target.value))} /></label><div className="flex gap-2"><Button size="sm" disabled={busy || stack < 1 || itemQuality < 1 || durability < 0} onClick={() => void queue("replace")}>Queue edit</Button><Button size="sm" variant="destructive" disabled={busy} onClick={() => void queue("remove")}>Queue removal</Button></div></div> : <p className="text-xs text-muted-foreground">Select an occupied slot in the snapshot.</p>}</section>
    </div><section className="mt-4 rounded-xl border bg-card p-4"><h4 className="mb-2 text-sm font-semibold">Queued edit status</h4>{archive?.edits.length ? archive.edits.map(edit => <div key={edit.id} className="flex flex-wrap gap-2 border-t py-2 text-xs"><span>{edit.action} {editDetail(edit)}</span><span className="font-semibold">{edit.status}</span><span className="text-muted-foreground">{new Date(edit.createdAt).toLocaleString()}</span>{edit.error && <span className="text-destructive">{edit.error}</span>}</div>) : <p className="text-xs text-muted-foreground">No queued edits.</p>}</section>
  </div>
}
