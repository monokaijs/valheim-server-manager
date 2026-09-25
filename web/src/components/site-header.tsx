import { RefreshCwIcon } from "lucide-react"

import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { SidebarTrigger } from "@/components/ui/sidebar"

export function SiteHeader({ status, onRefresh }: { status: string; onRefresh: () => void }) {
  const online = status === "running"
  return (
    <header className="sticky top-0 z-20 flex h-(--header-height) shrink-0 items-center border-b bg-background/85 backdrop-blur-xl transition-[width,height] ease-linear group-has-data-[collapsible=icon]/sidebar-wrapper:h-(--header-height)">
      <div className="flex w-full items-center gap-1 px-4 lg:gap-2 lg:px-6">
        <SidebarTrigger className="-ml-1" />
        <div className="ml-auto flex items-center gap-2">
          <Badge variant="outline" className={online ? "text-emerald-300" : "text-amber-300"}>
            <span className={online ? "size-1.5 rounded-full bg-emerald-400" : "size-1.5 rounded-full bg-amber-400"} />
            {status}
          </Badge>
          <Button variant="ghost" size="icon-sm" onClick={onRefresh} aria-label="Refresh status"><RefreshCwIcon /></Button>
        </div>
      </div>
    </header>
  )
}
