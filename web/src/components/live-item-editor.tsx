import { useEffect, useState } from "react"
import { request } from "../api"
import { Button } from "./ui/button"
import { Input } from "./ui/input"

type Item = { prefab: string; x: number; y: number; stack: number; maxStack: number; quality: number; maxQuality: number; durability: number; maxDurability: number }

export function LiveItemEditor({ peerKey, item, enabled }: { peerKey: string; item: Item; enabled: boolean }) {
  const [stack, setStack] = useState(item.stack)
  const [quality, setQuality] = useState(item.quality)
  const [durability, setDurability] = useState(item.durability)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState("")
  useEffect(() => { setStack(item.stack); setQuality(item.quality); setDurability(item.durability); setMessage("") }, [item.prefab, item.x, item.y])
  const edit = async (action: "replace" | "remove") => {
    if (!enabled || busy) return
    setBusy(true); setMessage("")
    try {
      await request(`/api/v1/players/${encodeURIComponent(peerKey)}/items/edit`, { method: "POST", body: JSON.stringify({
        action, prefab: item.prefab, x: item.x, y: item.y, expectedStack: item.stack, expectedQuality: item.quality,
        stack, quality, durability,
      }) })
      setMessage(action === "remove" ? "Item removed." : "Item updated.")
    } catch (cause) { setMessage((cause as Error).message) }
    finally { setBusy(false) }
  }
  return <div className="mt-4 space-y-2 border-t pt-3 text-xs"><p className="font-semibold">Edit item</p>
    <div className="grid grid-cols-2 gap-2"><label>Stack<Input type="number" min={1} max={item.maxStack} value={stack} onChange={event => setStack(Number(event.target.value))} /></label><label>Quality<Input type="number" min={1} max={item.maxQuality} value={quality} onChange={event => setQuality(Number(event.target.value))} /></label></div>
    {item.maxDurability > 0 && <label className="block">Durability<Input type="number" min={0} step="any" value={durability} onChange={event => setDurability(Number(event.target.value))} /></label>}
    <div className="flex gap-2"><Button size="sm" disabled={!enabled || busy || stack < 1 || stack > item.maxStack || quality < 1 || quality > item.maxQuality || durability < 0} onClick={() => void edit("replace")}>Save</Button><Button size="sm" variant="destructive" disabled={!enabled || busy} onClick={() => void edit("remove")}>Remove</Button></div>
    {message && <p role="status">{message}</p>}
  </div>
}
