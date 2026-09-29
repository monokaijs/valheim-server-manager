# Native character bridge

This small process decodes and edits server-owned `.fch` files. It uses the MIT-licensed [fch-decoder](https://github.com/lanchelms/fch-decoder) module pinned in `go.mod` for version 43 profiles. The bridge reads version 46 profiles directly and rewrites only their inventory bytes, preserving the rest of the save. The manager validates the file checksum, compares revisions, and writes a backup before replacing a save. Other character versions are rejected until their layout is verified.
