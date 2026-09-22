import { useCallback, useEffect, useRef, useState } from "react"
import { ChevronDown, ChevronRight, FileCode2, FilePlus2, Folder, FolderPlus, HardDrive, LockKeyhole, Pencil, RefreshCw, Save, Search, Trash2 } from "lucide-react"
import { toast } from "sonner"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Badge } from "@/components/ui/badge"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Label } from "@/components/ui/label"
import { cn } from "@/lib/utils"
import { post, request } from "../api"

type Entry = { path: string; name: string; kind: "file" | "directory"; size: number; modifiedAt?: string }
type Tree = { roots: string[]; entries: Entry[] }
type Document = { path: string; content: string; revision: string; modifiedAt: string }
type Mod = { id: string; name: string; namespace: string; protected: boolean }
type Operation = "create" | "mkdir" | "rename" | "delete"
const parent = (path: string) => path.includes("/") ? path.slice(0, path.lastIndexOf("/")) : ""

export function ModFiles({ modId, onChanged }: { modId: string; onChanged?: () => void }) {
  const [tree, setTree] = useState<Tree | null>(null)
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [selected, setSelected] = useState<Entry | null>(null)
  const [document, setDocument] = useState<Document | null>(null)
  const [draft, setDraft] = useState("")
  const [search, setSearch] = useState("")
  const [error, setError] = useState("")
  const [loading, setLoading] = useState(false)
  const [busy, setBusy] = useState(false)
  const [revealed, setRevealed] = useState(false)
  const [operation, setOperation] = useState<Operation | null>(null)
  const [destination, setDestination] = useState("")
  const version = useRef(0)
  const dirty = document !== null && draft !== document.content
  const url = `/api/v1/mods/${modId}/files`
  const load = useCallback(async () => {
    try { const result = await request<Tree>(url); setTree(result); setExpanded(current => new Set([...current, ...result.roots])); setError("") }
    catch (cause) { setError((cause as Error).message) }
  }, [url])
  useEffect(() => { void load(); return () => { version.current++ } }, [load])
  useEffect(() => {
    if (!dirty) return
    const protect = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = "" }
    window.addEventListener("beforeunload", protect)
    return () => window.removeEventListener("beforeunload", protect)
  }, [dirty])
  const read = async (entry: Entry) => {
    const serial = ++version.current
    setLoading(true); setError("")
    try {
      const result = await request<Document>(`${url}/content?path=${encodeURIComponent(entry.path)}`)
      if (serial !== version.current) return
      setDocument(result); setDraft(result.content)
    } catch (cause) { if (serial === version.current) setError((cause as Error).message) }
    finally { if (serial === version.current) setLoading(false) }
  }
  const choose = (entry: Entry) => {
    if (dirty && !window.confirm("Discard the unsaved draft before selecting another file?")) return
    version.current++; setSelected(entry); setDocument(null); setDraft(""); setError("")
    if (entry.kind === "directory") { setExpanded(current => { const next = new Set(current); if (next.has(entry.path)) next.delete(entry.path); else next.add(entry.path); return next }); return }
    if (revealed) void read(entry)
  }
  const begin = (kind: Operation) => {
    if (dirty && !window.confirm("Discard the unsaved draft?")) return
    const selectedFolder = selected?.kind === "directory" ? selected.path : selected ? parent(selected.path) : ""
    const folder = tree?.roots.some(root => selectedFolder === root || selectedFolder.startsWith(root + "/")) ? selectedFolder : tree?.roots[0] || ""
    setDestination(kind === "rename" ? selected?.path || "" : folder ? `${folder}/` : "")
    setOperation(kind)
  }
  const mutate = async () => {
    if (!operation) return
    setBusy(true); setError("")
    try {
      const path = operation === "create" || operation === "mkdir" ? destination : selected?.path
      if (!path) throw new Error("Choose a path first.")
      const content = operation === "create" ? destination.endsWith(".json") ? "{}\n" : destination.endsWith(".xml") ? "<configuration />\n" : "" : undefined
      await post(url, { operation, path, destination: operation === "rename" ? destination : null, directory: operation === "mkdir" || selected?.kind === "directory" && operation === "delete", revision: document?.revision, content })
      setDocument(null); setDraft(""); setSelected(null); setOperation(null); onChanged?.(); await load()
      toast.success("File change saved. Apply a server restart when ready.")
    } catch (cause) { setError((cause as Error).message); toast.error((cause as Error).message) }
    finally { setBusy(false) }
  }
  const save = async () => {
    if (!document || !dirty) return
    setBusy(true)
    try {
      await post(url, { operation: "save", path: document.path, revision: document.revision, content: draft })
      onChanged?.(); if (selected) await read(selected); await load(); toast.success("Saved with a backup. Restart required.")
    } catch (cause) { setError((cause as Error).message) }
    finally { setBusy(false) }
  }
  const draw = (prefix = "", depth = 0): React.ReactNode => (tree?.entries || []).filter(entry => parent(entry.path) === prefix)
    .sort((a, b) => a.kind === b.kind ? a.name.localeCompare(b.name) : a.kind === "directory" ? -1 : 1)
    .filter(entry => !search || entry.path.toLowerCase().includes(search.toLowerCase()) || tree?.entries.some(child => child.path.startsWith(entry.path + "/") && child.path.toLowerCase().includes(search.toLowerCase())))
    .map(entry => <div key={entry.path}><button disabled={busy} onClick={() => choose(entry)} className={cn("flex w-full items-center gap-1.5 rounded-md py-2 pr-2 text-left text-xs transition-colors hover:bg-muted/50", selected?.path === entry.path && "bg-primary/10 text-primary")} style={{ paddingLeft: `${10 + depth * 14}px` }}><span className="grid size-3 place-items-center">{entry.kind === "directory" && (expanded.has(entry.path) || search ? <ChevronDown className="size-3" /> : <ChevronRight className="size-3" />)}</span>{entry.kind === "directory" ? <Folder className="size-3.5 shrink-0 text-amber-300/80" /> : <FileCode2 className="size-3.5 shrink-0 text-muted-foreground" />}<span className="truncate" title={entry.path}>{entry.name}</span>{entry.modifiedAt === null && <span className="ml-auto text-[9px] text-muted-foreground">new</span>}</button>{entry.kind === "directory" && (expanded.has(entry.path) || search) && draw(entry.path, depth + 1)}</div>)

  return <div className="space-y-3">
    <div className="flex flex-wrap items-center justify-between gap-3"><div className="flex items-center gap-2 text-xs text-muted-foreground"><HardDrive className="size-4" /><code>BepInEx/config</code><Badge variant="outline">Package scoped</Badge></div><div className="flex gap-2"><Button variant="outline" size="sm" disabled={busy || !tree?.roots.length} onClick={() => begin("mkdir")}><FolderPlus /> Folder</Button><Button variant="outline" size="sm" disabled={busy || !tree?.roots.length} onClick={() => begin("create")}><FilePlus2 /> File</Button><Button variant="ghost" size="icon-sm" aria-label="Refresh file tree" disabled={busy} onClick={() => void load()}><RefreshCw /></Button></div></div>
    {error && <div role="alert" className="rounded-lg border border-destructive/30 bg-destructive/5 p-3 text-xs text-destructive">{error}<p className="mt-1">Your unsaved draft is preserved. Reload only after copying anything you need to keep.</p></div>}
    <div className="grid min-h-[460px] overflow-hidden rounded-xl border bg-card md:grid-cols-[250px_minmax(0,1fr)]"><aside className="flex max-h-[60vh] min-h-0 flex-col border-b bg-muted/10 md:border-b-0 md:border-r"><div className="relative border-b p-3"><Search className="absolute left-5 top-5 size-3.5 text-muted-foreground" /><Input className="h-8 pl-7 text-xs" value={search} onChange={event => setSearch(event.target.value)} placeholder="Filter files…" aria-label="Filter configuration files" /></div><div className="max-h-44 flex-1 overflow-auto p-2 md:max-h-none">{draw()}{tree && tree.entries.length === 0 && <p className="p-4 text-xs text-muted-foreground">No configuration namespace is available for this package.</p>}</div><p className="border-t p-3 text-[10px] leading-4 text-muted-foreground">{tree?.entries.filter(entry => entry.kind === "file").length || 0} text files · 2 MiB per file<br />DLLs, scripts and protected manager files are not exposed.</p></aside>
      <section className="flex min-w-0 flex-col"><div className="flex min-h-14 items-center justify-between gap-2 border-b px-4"><code className="truncate text-xs text-muted-foreground" title={selected?.path}>{selected?.path || "Select a file or folder"}</code><div className="flex shrink-0 items-center gap-1">{dirty && <Badge variant="outline">Unsaved</Badge>}<Button size="icon-sm" variant="ghost" aria-label="Rename selected file" disabled={busy || !document} onClick={() => begin("rename")}><Pencil /></Button><Button size="icon-sm" variant="ghost" aria-label="Delete selected file or empty folder" disabled={busy || !selected || selected.kind === "file" && !document} onClick={() => begin("delete")}><Trash2 /></Button></div></div>
      {selected?.kind === "file" ? !revealed ? <div className="grid flex-1 place-content-center gap-3 p-8 text-center"><LockKeyhole className="mx-auto size-8 text-primary" /><h3 className="text-sm font-semibold">Raw configuration can contain secrets</h3><p className="mx-auto max-w-sm text-xs leading-5 text-muted-foreground">Unlike the structured Settings editor, this view reveals the complete file to you. Values are not copied to the audit log.</p><Button variant="outline" onClick={() => { setRevealed(true); void read(selected) }}>Open raw editor</Button></div> : loading ? <div className="grid flex-1 place-content-center text-sm text-muted-foreground">Reading file…</div> : document ? <><textarea aria-label={`Edit ${document.path}`} spellCheck={false} disabled={busy} value={draft} onChange={event => setDraft(event.target.value)} className="min-h-80 w-full flex-1 resize-y bg-background/40 p-4 font-mono text-xs leading-6 outline-none focus-visible:ring-1 focus-visible:ring-inset focus-visible:ring-primary" /><div className="flex flex-wrap items-center justify-between gap-2 border-t px-4 py-3"><span className="text-[10px] text-muted-foreground">UTF-8 · {draft.split("\n").length} lines · revision {document.revision.slice(0, 10)}</span><div className="flex gap-2"><Button variant="outline" size="sm" disabled={busy} onClick={() => { if (!dirty || window.confirm("Discard the unsaved draft and reload?")) void read(selected) }}>Reload</Button><Button size="sm" disabled={busy || !dirty} onClick={() => void save()}><Save />{busy ? "Saving…" : "Save for restart"}</Button></div></div></> : <p className="p-8 text-sm text-muted-foreground">Could not open this file. Select it again to retry.</p> : <div className="grid flex-1 place-content-center gap-3 p-8 text-center"><Folder className="mx-auto size-10 text-muted-foreground/40" /><h3 className="text-sm font-semibold">A workspace for this mod</h3><p className="mx-auto max-w-md text-xs leading-5 text-muted-foreground">Create nested folders and configuration files in one of the listed namespaces. The mod must support the path and format you choose; creating a file does not make the mod load it automatically.</p><p className="mx-auto max-w-md break-all font-mono text-[10px] text-muted-foreground">{tree?.roots.join(" · ")}</p></div>}
      </section></div>
    <p className="text-[11px] leading-5 text-muted-foreground">Edits are written atomically with revision checks. Replaced, renamed and deleted files retain backups. Deleting folders is allowed only when empty. Restart from Mods → Apply & restart when finished.</p>
    <Dialog open={operation !== null} onOpenChange={open => !open && !busy && setOperation(null)}><DialogContent className="sm:max-w-lg"><DialogHeader><DialogTitle>{operation === "mkdir" ? "Create folder" : operation === "create" ? "Create configuration file" : operation === "rename" ? "Rename file" : "Delete configuration entry"}</DialogTitle><DialogDescription>{operation === "delete" ? `Delete ${selected?.path}? File backups are retained. Non-empty folders cannot be deleted.` : "Use a path relative to BepInEx/config, inside this package's namespaces."}</DialogDescription></DialogHeader>{operation !== "delete" && <div className="space-y-2"><Label htmlFor="config-destination">Path</Label><Input id="config-destination" value={destination} maxLength={500} onChange={event => setDestination(event.target.value)} placeholder="author.mod/recipes/items.yml" autoFocus /><p className="text-[11px] text-muted-foreground">cfg · json · yaml · yml · toml · ini · txt · xml</p></div>}<div className="flex justify-end gap-2"><Button variant="outline" disabled={busy} onClick={() => setOperation(null)}>Cancel</Button><Button variant={operation === "delete" ? "destructive" : "default"} disabled={busy || operation !== "delete" && !destination.trim()} onClick={() => void mutate()}>{busy ? "Working…" : operation === "delete" ? "Delete" : "Save"}</Button></div></DialogContent></Dialog>
  </div>
}

export function ModFileManagerPage({ onChanged }: { onChanged: () => void }) {
  const [mods, setMods] = useState<Mod[]>([])
  const [modId, setModId] = useState("")
  const [error, setError] = useState("")
  useEffect(() => { void request<Mod[]>("/api/v1/mods").then(items => { const available = items.filter(mod => !mod.protected); setMods(available); setModId(available[0]?.id || "") }).catch(cause => setError((cause as Error).message)) }, [])
  return <div className="space-y-6"><header className="flex flex-wrap items-end justify-between gap-4 border-b pb-6"><div><p className="mb-2 text-[11px] font-semibold uppercase tracking-[.2em] text-primary">Configuration workspace</p><h1 className="text-3xl font-semibold tracking-tight">Files</h1><p className="mt-2 text-sm text-muted-foreground">Structured mod namespaces, guarded edits, and recoverable changes.</p></div><label className="grid gap-2 text-xs text-muted-foreground">Package<select className="min-w-56 rounded-lg border bg-card px-3 py-2 text-sm text-foreground" value={modId} onChange={event => { if (window.confirm("Switch package? Any unsaved draft in this workspace will be discarded.")) setModId(event.target.value) }}>{mods.map(mod => <option value={mod.id} key={mod.id}>{mod.namespace}/{mod.name}</option>)}</select></label></header>{error && <p role="alert" className="text-sm text-destructive">{error}</p>}{modId ? <ModFiles key={modId} modId={modId} onChanged={onChanged} /> : <p className="rounded-xl border p-8 text-center text-sm text-muted-foreground">Install a managed gameplay mod to open its configuration workspace.</p>}</div>
}
