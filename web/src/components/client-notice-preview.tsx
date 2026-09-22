type Props = { title: string; message: string; persistent: boolean }

export function ClientNoticePreview({ title, message, persistent }: Props) {
  return <div className="space-y-3">
    <div className="border-l-4 border-amber-300 bg-[#13161a] p-5 text-slate-100 shadow-lg">
      <p className="text-[11px] font-medium tracking-widest text-slate-400">SERVER MANAGER</p>
      <p className="mt-3 break-words text-xl font-semibold">{title}</p>
      <p className="mt-3 max-h-64 overflow-y-auto whitespace-pre-wrap break-words text-sm leading-relaxed">{message || "Enter a message to preview it."}</p>
      {persistent
        ? <div className="mt-6 flex flex-wrap gap-3" aria-hidden="true"><span className="rounded border border-slate-600 px-4 py-2 text-xs">Dismiss</span><span className="rounded border border-slate-600 px-4 py-2 text-xs">Copy message</span></div>
        : <p className="mt-4 text-xs text-slate-400">From your server</p>}
    </div>
    <p className="text-xs leading-relaxed text-muted-foreground">{persistent
      ? "Compatible clients keep this notice at the menu until dismissed. During play it appears without taking control."
      : "Appears after the character loads, then fades after enough time to read. It does not interrupt play."}</p>
    <p className="text-[11px] text-muted-foreground">Sample wording and approximate in-game layout. Valheim scales the panel to the player's screen.</p>
  </div>
}
