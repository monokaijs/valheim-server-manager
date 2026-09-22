# Server Manager

Server Manager is the single installable mod for the self-hosted Valheim Server Manager control plane. On dedicated servers it connects the game server to the web dashboard. On player clients it receives the exact client runtime and required mod set selected by that server.

The BepInEx plugin ID is `dev.creaton.valheim-server-manager`. This package is published under the independent `Creaton` namespace.

## Installation

Deploy the complete manager with Docker by following the [project documentation](https://github.com/monokaijs/valheim-server-manager). The minimal setup is `cp .env.example .env`, `docker compose pull`, then `docker compose up -d`; the container installs and configures the agent automatically.

Install this same package on the dedicated server. Player installation is optional in the default vanilla-compatible mode. If the owner enables server-owned characters, install the same package on each player client; there is no separate companion or bootstrap mod.

## Security

The dashboard uses Steam OpenID authentication and checks administrators against Valheim's `adminlist.txt`. Put the dashboard behind HTTPS before exposing it to the internet, and keep its agent token private.

This project is unofficial and is not affiliated with Iron Gate AB or Coffee Stain Publishing.
