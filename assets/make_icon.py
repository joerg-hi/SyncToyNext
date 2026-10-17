"""
Erzeugt das Programmsymbol von SyncToyNext.

    pip install cairosvg pillow
    python assets/make_icon.py

Ergebnis:
    assets/logo.svg                            Vektorgrafik (Quelle)
    assets/logo-256.png                        Vorschau
    src/SyncToyNext.App/Assets/SyncToyNext.ico Symbol für Fenster, Taskleiste und .exe

Die Geometrie der Pfeile wird auch als XAML-Pfad ausgegeben (für das Logo im Programm,
siehe src/SyncToyNext.App/Resources/Logo.xaml).
"""

import io
import math
from pathlib import Path

import cairosvg
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
CENTER = 128.0
RADIUS = 60.0
STROKE = 22.0
HEAD = 30.0  # Länge der Pfeilspitze


def point(angle_deg: float, radius: float = RADIUS) -> tuple[float, float]:
    a = math.radians(angle_deg)
    return CENTER + radius * math.cos(a), CENTER + radius * math.sin(a)


def fmt(p: tuple[float, float]) -> str:
    return f"{p[0]:.1f},{p[1]:.1f}"


def arrow(start_deg: float, end_deg: float) -> tuple[str, str]:
    """Kreisbogen im Uhrzeigersinn plus Pfeilspitze am Ende. Liefert (Bogen, Spitze) als Pfaddaten."""
    arc = f"M{fmt(point(start_deg))} A{RADIUS:.0f},{RADIUS:.0f} 0 0 1 {fmt(point(end_deg))}"

    end = point(end_deg)
    a = math.radians(end_deg)
    tangent = (-math.sin(a), math.cos(a))
    normal = (math.cos(a), math.sin(a))
    half = HEAD * 0.62
    tip = (end[0] + tangent[0] * HEAD * 0.9, end[1] + tangent[1] * HEAD * 0.9)
    outer = (end[0] + normal[0] * half, end[1] + normal[1] * half)
    inner = (end[0] - normal[0] * half, end[1] - normal[1] * half)
    head = f"M{fmt(outer)} L{fmt(tip)} L{fmt(inner)} Z"
    return arc, head


ARC_TOP, HEAD_TOP = arrow(195, 318)
ARC_BOTTOM, HEAD_BOTTOM = arrow(15, 138)

SVG = f"""<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#2AA8F2"/>
      <stop offset="1" stop-color="#0A5CC2"/>
    </linearGradient>
    <linearGradient id="shine" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.28"/>
      <stop offset="1" stop-color="#FFFFFF" stop-opacity="0"/>
    </linearGradient>
  </defs>
  <rect x="12" y="12" width="232" height="232" rx="56" fill="url(#bg)"/>
  <rect x="12" y="12" width="232" height="116" rx="56" fill="url(#shine)"/>
  <g fill="none" stroke="#FFFFFF" stroke-width="{STROKE:.0f}" stroke-linecap="round">
    <path d="{ARC_TOP}"/>
    <path d="{ARC_BOTTOM}"/>
  </g>
  <g fill="#FFFFFF" stroke="#FFFFFF" stroke-width="4" stroke-linejoin="round">
    <path d="{HEAD_TOP}"/>
    <path d="{HEAD_BOTTOM}"/>
  </g>
</svg>
"""


def main() -> None:
    assets = ROOT / "assets"
    (assets / "logo.svg").write_text(SVG, encoding="utf-8")

    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    images = []
    for size in sizes:
        png = cairosvg.svg2png(bytestring=SVG.encode(), output_width=size, output_height=size)
        images.append(Image.open(io.BytesIO(png)).convert("RGBA"))

    images[-1].save(assets / "logo-256.png")

    ico = ROOT / "src" / "SyncToyNext.App" / "Assets" / "SyncToyNext.ico"
    ico.parent.mkdir(parents=True, exist_ok=True)
    images[-1].save(ico, format="ICO", sizes=[(s, s) for s in sizes], append_images=images[:-1])

    print("Pfeil oben:   ", ARC_TOP, "|", HEAD_TOP)
    print("Pfeil unten:  ", ARC_BOTTOM, "|", HEAD_BOTTOM)


if __name__ == "__main__":
    main()
