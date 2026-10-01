#!/usr/bin/env python3
"""Deterministic original PNG artwork; no game assets or runtime rasterization."""
from pathlib import Path
from PIL import Image, ImageDraw
root = Path(__file__).resolve().parents[1] / 'plugins/ValheimServerManager.Client/Assets/Voice'
root.mkdir(parents=True, exist_ok=True)
scale = 4
for waves in range(4):
    im = Image.new('RGBA', (128 * scale, 100 * scale))
    draw = ImageDraw.Draw(im)
    def xy(values): return tuple(round(v * scale) for v in values)
    # Dark contour survives snow and bright HUD backgrounds; tintable white interior.
    for color, extra in [((12, 16, 18, 235), 3), ((255, 255, 255, 255), 0)]:
        draw.rounded_rectangle(xy((31-extra, 12-extra, 51+extra, 55+extra)), radius=(10+extra)*scale, fill=color)
        draw.arc(xy((20, 28, 62, 72)), 0, 180, fill=color, width=(5+extra*2)*scale)
        draw.line(xy((41, 72, 41, 85)), fill=color, width=(5+extra*2)*scale)
        draw.line(xy((29, 85, 53, 85)), fill=color, width=(5+extra*2)*scale)
        for wave in range(waves):
            radius = 17 + 13 * wave
            draw.arc(xy((57-radius, 46-radius, 57+radius, 46+radius)), -48, 48, fill=color, width=(4+extra*2)*scale)
    im.resize((128, 100), Image.Resampling.LANCZOS).save(root / f'microphone-{waves}.png', optimize=True)
