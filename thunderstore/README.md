# Server Manager

Server Manager is the dedicated-server agent for the self-hosted Valheim Server Manager control plane. It connects the game server to the web dashboard for live administration, access control, safe console commands, mod configuration, event webhooks, client-mod coordination, and server-owned characters.

The BepInEx plugin ID is `dev.creaton.valheim-server-manager`. This package is published under the independent `Creaton` namespace.

## Installation

Deploy the complete manager with Docker by following the [project documentation](https://github.com/monokaijs/valheim-server-manager). The container installs and configures this agent automatically. The agent is not a standalone dashboard and expects the manager service to be reachable through its configured loopback WebSocket URL.

This is a server-side package. Players only need the separate client companion when the server enables features that require it, such as server-owned characters.

## Security

The dashboard uses Steam OpenID authentication and checks administrators against Valheim's `adminlist.txt`. Put the dashboard behind HTTPS before exposing it to the internet, and keep its agent token private.

This project is unofficial and is not affiliated with Iron Gate AB or Coffee Stain Publishing.
