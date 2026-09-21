# Server Manager

Server Manager is the single installable mod for the self-hosted Valheim Server Manager control plane. On dedicated servers it connects the game server to the web dashboard. On player clients it receives the exact client runtime and required mod set selected by that server.

The BepInEx plugin ID is `dev.creaton.valheim-server-manager`. This package is published under the independent `Creaton` namespace.

## Installation

Deploy the complete manager with Docker by following the [project documentation](https://github.com/monokaijs/valheim-server-manager). The container installs and configures this agent automatically. The agent is not a standalone dashboard and expects the manager service to be reachable through its configured loopback WebSocket URL.

Install this same package on the dedicated server and player clients. There is no separate companion or bootstrap mod to publish or install. Server-only and client-only components disable themselves outside their intended process.

## Security

The dashboard uses Steam OpenID authentication and checks administrators against Valheim's `adminlist.txt`. Put the dashboard behind HTTPS before exposing it to the internet, and keep its agent token private.

This project is unofficial and is not affiliated with Iron Gate AB or Coffee Stain Publishing.
