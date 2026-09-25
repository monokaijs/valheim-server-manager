type Props = { title: string; message: string; persistent: boolean }

export function ClientNoticePreview({ title, message, persistent }: Props) {
  return <div className="space-y-3">
    {persistent ? <div className="border-l-4 border-amber-300 bg-[#13161a] p-5 text-slate-100 shadow-lg">
      <p className="text-[11px] font-medium tracking-widest text-slate-400">SERVER MANAGER</p>
      <p className="mt-3 break-words text-xl font-semibold">{title}</p>
      <p className="mt-3 max-h-64 overflow-y-auto whitespace-pre-wrap break-words text-sm leading-relaxed">{message || "Enter a message to preview it."}</p>
      <div className="mt-6 flex flex-wrap gap-3" aria-hidden="true"><span className="rounded border border-slate-600 px-4 py-2 text-xs">Dismiss</span><span className="rounded border border-slate-600 px-4 py-2 text-xs">Copy message</span></div>
    </div> : <div className="rounded-lg bg-[#13161a] p-6 text-center text-slate-100">
      <p className="text-[11px] text-slate-400">VALHEIM MESSAGE HUD</p>
      <p className="mt-3 whitespace-pre-wrap break-words text-sm">{message || "Enter a message to preview it."}</p>
    </div>}
    <p className="text-xs leading-relaxed text-muted-foreground">{persistent
      ? "Compatible clients keep this notice at the menu until dismissed. Kick and ban reasons also use Valheim's standard HUD before disconnect."
      : "Valheim shows this through its standard message HUD during play."}</p>
    <p className="text-[11px] text-muted-foreground">Approximate preview. The game screen has no Server Manager panel.</p>
  </div>
}
