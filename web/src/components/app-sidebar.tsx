import * as React from "react"
import { LogOutIcon, ServerIcon } from "lucide-react"

import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar"
import { Button } from "@/components/ui/button"
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuBadge,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarRail,
  useSidebar,
} from "@/components/ui/sidebar"

export type DashboardNavItem = {
  id: string
  label: string
  hint: string
  icon: React.ElementType
}

type AppSidebarProps = React.ComponentProps<typeof Sidebar> & {
  items: DashboardNavItem[]
  activeId: string
  onNavigate: (id: string) => void
  userName: string
  steamId?: string
  avatarUrl?: string
  onlinePlayers: number
  agentConnected: boolean
  onLogout: () => void
}

export function AppSidebar({ items, activeId, onNavigate, userName, steamId, avatarUrl, onlinePlayers, agentConnected, onLogout, ...props }: AppSidebarProps) {
  const { isMobile, setOpenMobile } = useSidebar()
  const navigate = (id: string) => { onNavigate(id); if (isMobile) setOpenMobile(false) }
  return (
    <Sidebar collapsible="offcanvas" {...props}>
      <SidebarHeader>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton size="lg" onClick={() => navigate("overview")} tooltip="Valheim Manager">
              <div className="grid size-8 place-items-center rounded-lg bg-primary font-semibold text-primary-foreground">ᛉ</div>
              <div className="grid flex-1 text-left text-sm leading-tight">
                <span className="truncate font-semibold">Valheim Manager</span>
                <span className="truncate text-xs text-muted-foreground">Server control</span>
              </div>
            </SidebarMenuButton>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarHeader>
      <SidebarContent>
        <SidebarGroup>
          <SidebarGroupLabel>Manage</SidebarGroupLabel>
          <SidebarGroupContent>
            <SidebarMenu>
              {items.map((item) => (
                <SidebarMenuItem key={item.id}>
                  <SidebarMenuButton isActive={activeId === item.id} tooltip={item.hint} onClick={() => navigate(item.id)}>
                    <item.icon />
                    <span>{item.label}</span>
                  </SidebarMenuButton>
                  {item.id === "players" && onlinePlayers > 0 && <SidebarMenuBadge>{onlinePlayers}</SidebarMenuBadge>}
                </SidebarMenuItem>
              ))}
            </SidebarMenu>
          </SidebarGroupContent>
        </SidebarGroup>
      </SidebarContent>
      <SidebarFooter>
        <div className="mb-1 flex items-center gap-2 px-2 text-xs text-muted-foreground">
          <ServerIcon className={agentConnected ? "size-3.5 text-emerald-400" : "size-3.5 text-amber-400"} />
          <span>{agentConnected ? "Agent connected" : "Agent disconnected"}</span>
        </div>
        <SidebarMenu>
          <SidebarMenuItem>
            <div className="flex items-center gap-2 rounded-lg p-2">
              <Avatar className="size-8 rounded-lg">
                {avatarUrl && <AvatarImage src={avatarUrl} alt="" className="rounded-lg" />}
                <AvatarFallback className="rounded-lg bg-muted text-xs">{userName.slice(0, 2).toUpperCase()}</AvatarFallback>
              </Avatar>
              <div className="min-w-0 flex-1 text-left text-sm leading-tight">
                <span className="block truncate font-medium">{userName}</span>
                {steamId && <a className="block truncate text-[11px] text-muted-foreground hover:text-primary hover:underline" href={`https://steamcommunity.com/profiles/${steamId}`} target="_blank" rel="noreferrer">View Steam profile</a>}
              </div>
              <Button variant="ghost" size="icon-sm" onClick={onLogout} aria-label="Sign out"><LogOutIcon /></Button>
            </div>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarFooter>
      <SidebarRail />
    </Sidebar>
  )
}
