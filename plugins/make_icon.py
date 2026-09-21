import struct, sys, zlib

width = height = 256
rows = []
for y in range(height):
    row = bytearray([0])
    for x in range(width):
        d = ((x - 128) ** 2 + (y - 128) ** 2) ** .5
        if d < 92:
            r, g, b = (180, 220, 131)
            if 112 < x < 144 or (92 < x < 164 and 70 < y < 92): r, g, b = (21, 31, 26)
        else:
            r, g, b = (17, 24, 21)
        row.extend((r, g, b, 255))
    rows.append(bytes(row))
raw = b''.join(rows)
def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)
png = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b'')
open(sys.argv[1], 'wb').write(png)

