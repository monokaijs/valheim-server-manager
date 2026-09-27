# Native character bridge

This small process decodes and edits server-owned `.fch` files. It uses the MIT-licensed [fch-decoder](https://github.com/lanchelms/fch-decoder) module pinned in `go.mod`. The manager validates the file checksum, compares revisions, and writes a backup before replacing a save. The codec rejects character versions it cannot safely round-trip.
