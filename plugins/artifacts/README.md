# Bootstrap artifact

`XomNghien-ServerModBootstrap-2.2.0.zip` is built from
[`monokaijs/Valheim-Server-Mods`](https://github.com/monokaijs/Valheim-Server-Mods),
with package model support for `sha256` and `contentBase64`.

Version 2.2 validates embedded packages by declared byte size and SHA-256,
validates the inner Thunderstore manifest, and stages them through the same
atomic cache path as HTTPS Thunderstore downloads. Embedded packages cannot
also declare a download URL. The added bootstrap test creates, verifies, and
caches an embedded ZIP. This is required for VSM to deliver its server-specific
client companion without exposing the authenticated dashboard to the internet.
