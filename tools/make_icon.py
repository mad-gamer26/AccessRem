"""Generate AccessRem's icon (a white bridge on a blue rounded tile) without third-party libraries."""

import math
import struct
import sys
import zlib

BLUE = (29, 78, 216)
WHITE = (255, 255, 255)


def coverage(size, x, y):
	"""Return (tile, glyph) coverage for a pixel using 4x4 supersampling."""
	tile = glyph = 0
	n = 4
	for sy in range(n):
		for sx in range(n):
			px = (x + (sx + 0.5) / n) / size
			py = (y + (sy + 0.5) / n) / size
			if inTile(px, py):
				tile += 1
				if inGlyph(px, py):
					glyph += 1
	return tile / (n * n), glyph / (n * n)


def inTile(px, py, r=0.22):
	x = min(max(px, r), 1 - r)
	y = min(max(py, r), 1 - r)
	return (px - x) ** 2 + (py - y) ** 2 <= r * r


def inGlyph(px, py):
	# Deck.
	if 0.16 <= px <= 0.84 and 0.62 <= py <= 0.70:
		return True
	# Arch: a ring segment centred on the deck.
	d = math.hypot(px - 0.5, py - 0.70)
	if 0.25 <= d <= 0.33 and py <= 0.70:
		return True
	# Hangers between arch and deck.
	for hx in (0.38, 0.5, 0.62):
		if abs(px - hx) <= 0.025 and py <= 0.66:
			top = 0.70 - math.sqrt(max(0.0, 0.29**2 - (hx - 0.5) ** 2))
			if py >= top:
				return True
	# A dot above the arch: the voice carried across.
	return math.hypot(px - 0.5, py - 0.22) <= 0.05


def png(size):
	rows = []
	for y in range(size):
		row = bytearray([0])
		for x in range(size):
			t, g = coverage(size, x, y)
			if t == 0:
				row += bytes((0, 0, 0, 0))
				continue
			gf = g / t
			rgb = [round(BLUE[i] * (1 - gf) + WHITE[i] * gf) for i in range(3)]
			row += bytes((*rgb, round(255 * t)))
		rows.append(bytes(row))

	def chunk(tag, data):
		return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

	header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
	return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(b"".join(rows), 9)) + chunk(b"IEND", b"")


def ico(path, sizes=(16, 20, 24, 32, 40, 48, 64, 256)):
	images = [png(s) for s in sizes]
	out = struct.pack("<HHH", 0, 1, len(images))
	offset = 6 + 16 * len(images)
	for s, data in zip(sizes, images):
		out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
		offset += len(data)
	with open(path, "wb") as f:
		f.write(out + b"".join(images))


if __name__ == "__main__":
	ico(sys.argv[1])
