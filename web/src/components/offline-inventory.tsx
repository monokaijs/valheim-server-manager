import { useEffect, useMemo, useState } from "react"
import { request } from "../api"
import { Button } from "./ui/button"
import { Input } from "./ui/input"

type Character = { fileName: string; characterName: string; modifiedAt: string }
type Item = { name: string; stack: number; quality: number; durability: number; equipped: boolean; variant: number; gridX: number; gridY: number }
type Metadata = { name: string; inventoryValid: boolean; maxStack: number; maxQuality: number }
type Inventory = { characterName: string; revision: string; items: Item[]; catalog?: Metadata[]; given: number }

export function OfflineInventory({ characters }: { characters: Character[] }) {
  const [fileName, setFileName] = useState(characters[0]?.fileName || "")
  const [data, setData] = useState<Inventory | null>(null)
  const [error, setError] = useState("")
  const [notice, setNotice] = useState("")
  const [busy, setBusy] = useState(false)
  const [loadRevision, setLoadRevision] = useState(0)
  const [selected, setSelected] = useState<Item | null>(null)
  const [prefab, setPrefab] = useState("")
  const [quantity, setQuantity] = useState(1)
  const [quality, setQuality] = useState(1)
  const [customMaxStack, setCustomMaxStack] = useState(1)
  const [stack, setStack] = useState(1)
  const [itemQuality, setItemQuality] = useState(1)
  const [durability, setDurability] = useState(0)
  const [equipped, setEquipped] = useState(false)
  const url = `/api/v1/characters/${encodeURIComponent(fileName)}/inventory`
  useEffect(() => {
    let active = true
    setData(null); setSelected(null); setError(""); setNotice("")
    if (fileName) void request<Inventory>(url).then(value => { if (active) setData(value) }).catch((cause: Error) => { if (active) setError(cause.message) })
    return () => { active = false }
  }, [fileName, url, loadRevision])
  const catalog = useMemo(() => (data?.catalog || []).filter(item => item.inventoryValid), [data?.catalog])
  const choose = (item: Item) => { setSelected(item); setStack(item.stack); setItemQuality(item.quality); setDurability(item.durability); setEquipped(item.equipped) }
  const edit = async (action: "give" | "replace" | "remove") => {
    if (!data || busy) return
    setBusy(true); setError(""); setNotice("")
    try {
      const result = await request<Inventory>(url, { method: "POST", body: JSON.stringify({
        action, revision: data.revision, prefab, quantity, quality, maxStack: customMaxStack,
        x: selected?.gridX || 0, y: selected?.gridY || 0,
        item: selected && action === "replace" ? { name: selected.name, stack, quality: itemQuality, durability, equipped, variant: selected.variant } : null,
      }) })
      const refreshed = await request<Inventory>(url)
      setData(refreshed)
      setSelected(null)
      if (action === "give" && result.given < quantity) setError(`Added ${result.given} of ${quantity}; the inventory is full.`)
      else setNotice(action === "give" ? `Added ${result.given} item${result.given === 1 ? "" : "s"} to the saved character.` : action === "replace" ? "Saved item changes." : "Removed item from the saved character.")
    } catch (cause) { setError((cause as Error).message) }
    finally { setBusy(false) }
  }
  return <div className="h-full overflow-y-auto p-4 sm:p-6">
    <div className="mb-4 flex flex-wrap items-center justify-between gap-3"><div><h3 className="font-semibold">Saved character inventory</h3><p className="text-xs text-muted-foreground">Changes are written to the server-owned character save and loaded on the next join.</p></div>
      <select aria-label="Character" className="rounded-md border bg-background px-3 py-2 text-sm" value={fileName} onChange={event => setFileName(event.target.value)}>{characters.map(character => <option key={character.fileName} value={character.fileName}>{character.characterName}</option>)}</select>
    </div>
    {error && <p role="alert" className="mb-3 text-sm text-destructive">{error}</p>}
    {notice && <p role="status" className="mb-3 text-sm text-primary">{notice}</p>}
    {!data ? error ? <Button variant="outline" size="sm" onClick={() => setLoadRevision(value => value + 1)}>Retry reading character</Button> : <p className="text-sm text-muted-foreground">Reading saved character…</p> : <>
      <div className="mb-5 rounded-xl border bg-card p-4"><h4 className="mb-3 text-sm font-semibold">Give item</h4><div className="flex flex-wrap items-end gap-2">
        <label className="min-w-48 flex-1 text-xs">Prefab<Input list="saved-character-items" value={prefab} onChange={event => setPrefab(event.target.value)} placeholder="Search or enter exact prefab" /></label>
        <datalist id="saved-character-items">{catalog.map(item => <option key={item.name} value={item.name} />)}</datalist>
        <label className="w-24 text-xs">Quality<Input type="number" min={1} max={100} value={quality} onChange={event => setQuality(Number(event.target.value))} /></label>
        <label className="w-24 text-xs">Quantity<Input type="number" min={1} max={1000} value={quantity} onChange={event => setQuantity(Number(event.target.value))} /></label>
        {!catalog.some(item => item.name === prefab) && <label className="w-24 text-xs">Max stack<Input type="number" min={1} max={1000} value={customMaxStack} onChange={event => setCustomMaxStack(Number(event.target.value))} /></label>}
        <Button disabled={busy || !prefab.trim() || !Number.isInteger(quantity) || quantity < 1 || quantity > 1000 || !Number.isInteger(quality) || quality < 1 || quality > 100 || customMaxStack < 1 || customMaxStack > 1000} onClick={() => void edit("give")}>Give</Button>
      </div></div>
      <div className="grid gap-4 xl:grid-cols-[minmax(0,1fr)_260px]"><section className="rounded-xl border bg-card p-4"><h4 className="mb-3 text-sm font-semibold">Inventory · {data.items.length} stacks</h4>
        <div className="grid grid-cols-8 gap-1">{Array.from({ length: 32 }, (_, index) => { const x = index % 8, y = Math.floor(index / 8), item = data.items.find(entry => entry.gridX === x && entry.gridY === y); return <button key={index} type="button" onClick={() => item ? choose(item) : setSelected(null)} className={`min-h-14 min-w-0 rounded border p-1 text-left text-[10px] hover:border-primary ${selected?.gridX === x && selected?.gridY === y ? "border-primary bg-primary/10" : "bg-muted/20"}`} title={item ? `${item.name} ×${item.stack}` : "Empty slot"}>{item && <><span className="block truncate">{item.name}</span><span>×{item.stack} · Q{item.quality}</span></>}</button> })}</div>
      </section><section className="rounded-xl border bg-card p-4"><h4 className="mb-3 text-sm font-semibold">Edit selected item</h4>{selected ? <div className="space-y-3 text-xs"><p className="break-all font-mono">{selected.name} · slot {selected.gridX + 1},{selected.gridY + 1}</p>
        <label className="block">Stack<Input type="number" min={1} max={1000} value={stack} onChange={event => setStack(Number(event.target.value))} /></label>
        <label className="block">Quality<Input type="number" min={1} max={100} value={itemQuality} onChange={event => setItemQuality(Number(event.target.value))} /></label>
        <label className="block">Durability<Input type="number" min={0} step="any" value={durability} onChange={event => setDurability(Number(event.target.value))} /></label>
        <label className="flex items-center gap-2"><input type="checkbox" checked={equipped} onChange={event => setEquipped(event.target.checked)} />Equipped</label>
        <div className="flex gap-2"><Button size="sm" disabled={busy || stack < 1 || itemQuality < 1 || durability < 0} onClick={() => void edit("replace")}>Save</Button><Button size="sm" variant="destructive" disabled={busy} onClick={() => void edit("remove")}>Remove</Button></div>
      </div> : <p className="text-xs text-muted-foreground">Select an occupied slot.</p>}</section></div>
    </>}
  </div>
}
