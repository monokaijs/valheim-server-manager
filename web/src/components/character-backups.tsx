import { useEffect, useState } from "react"
import { Clock3, RotateCcw } from "lucide-react"
import { request } from "../api"
import { Button } from "./ui/button"

type Character = { fileName: string; characterName: string }
type Backup = { fileName: string; createdAt: string; size: number; sha256: string }
type BackupSet = { characterName: string; revision: string; online: boolean; pendingDeath: boolean; backups: Backup[] }

export function CharacterBackups({ characters, online, canRestore, onRestored }: { characters: Character[]; online: boolean; canRestore: boolean; onRestored: () => void }) {
  const [fileName, setFileName] = useState(characters[0]?.fileName || "")
  const [data, setData] = useState<BackupSet | null>(null)
  const [error, setError] = useState("")
  const [notice, setNotice] = useState("")
  const [busy, setBusy] = useState(false)
  const [refresh, setRefresh] = useState(0)
  const url = `/api/v1/characters/${encodeURIComponent(fileName)}/backups`

  useEffect(() => {
    let active = true
    setData(null); setError("")
    if (fileName) void request<BackupSet>(url).then(value => { if (active) setData(value) }).catch((cause: Error) => { if (active) setError(cause.message) })
    return () => { active = false }
  }, [fileName, url, refresh])

  const restore = async (backup: Backup) => {
    if (!data || busy || online || !canRestore || data.online || data.pendingDeath) return
    const when = new Date(backup.createdAt).toLocaleString()
    if (!window.confirm(`Restore ${data.characterName} from ${when}? This replaces the entire character save, including inventory, skills, and progression. Check existing tombstones and stored items to avoid duplicates. The current save will be kept as an administrator safety backup.`)) return
    setBusy(true); setError(""); setNotice("")
    try {
      const result = await request<BackupSet>(`${url}/${encodeURIComponent(backup.fileName)}/restore`, {
        method: "POST", body: JSON.stringify({ revision: data.revision, backupSha256: backup.sha256 }),
      })
      setData(result)
      setNotice(`Restored ${result.characterName} from ${when}. The player will load this save on their next join.`)
      onRestored()
    } catch (cause) { setError((cause as Error).message) }
    finally { setBusy(false) }
  }

  return <section className="mt-5 border-t pt-5">
    <div className="flex items-center justify-between gap-2"><h3 className="text-[11px] font-semibold uppercase tracking-[.12em] text-muted-foreground">Character backups</h3><Button type="button" size="xs" variant="ghost" onClick={() => setRefresh(value => value + 1)}>Refresh</Button></div>
    <p className="mt-1 text-[11px] text-muted-foreground">Every 30 minutes online · latest five</p>
    {characters.length > 1 && <select aria-label="Backup character" className="mt-2 w-full rounded-md border bg-background px-2 py-1.5 text-xs" value={fileName} onChange={event => { setFileName(event.target.value); setNotice("") }}>{characters.map(character => <option key={character.fileName} value={character.fileName}>{character.characterName}</option>)}</select>}
    {error && <p role="alert" className="mt-2 text-xs text-destructive">{error}</p>}
    {notice && <p role="status" className="mt-2 text-xs text-primary">{notice}</p>}
    {!data && !error && <p className="mt-2 text-xs text-muted-foreground">Loading backups…</p>}
    {data?.pendingDeath && <p className="mt-2 text-xs text-amber-600">An unfinished death save needs recovery before a backup can be restored.</p>}
    {(online || data?.online) && <p className="mt-2 text-xs text-muted-foreground">The player must disconnect before a save can be restored.</p>}
    {!canRestore && <p className="mt-2 text-xs text-muted-foreground">An administrator can restore these saves.</p>}
    {data && data.backups.length === 0 && <p className="mt-2 text-xs text-muted-foreground">No periodic backups yet. The first is saved at the next online character checkpoint.</p>}
    <div className="mt-2 space-y-1.5">{data?.backups.map(backup => <div key={backup.fileName} className="rounded-md border bg-background/50 p-2"><div className="flex items-center gap-1.5 text-xs"><Clock3 className="size-3.5 text-muted-foreground" /><time>{new Date(backup.createdAt).toLocaleString()}</time></div><div className="mt-1 flex items-center justify-between gap-2"><span className="text-[10px] text-muted-foreground">{(backup.size / 1024).toFixed(1)} KiB</span><Button type="button" size="xs" variant="outline" disabled={busy || online || !canRestore || data.online || data.pendingDeath} onClick={() => void restore(backup)}><RotateCcw />Restore</Button></div></div>)}</div>
  </section>
}
