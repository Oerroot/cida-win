"""Derive Windows static fonts and ICO from the pinned upstream's OFL/SVG assets.
Run with fonttools and Pillow available. Never requires an image-generation service.
"""
import hashlib
import json
import math
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "artifacts/experience/python"))
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont
from fontTools.pens.basePen import BasePen
from fontTools.svgLib.path import parse_path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
fonts = root / "windows/Cida.Desktop/Assets/Fonts"
brand = root / "windows/Cida.Desktop/Assets/Brand"
source = root / "artifacts/experience/sourcefonts"
source.mkdir(parents=True, exist_ok=True)
records = []
for filename, axes, family in [
    ("SourceSerif4[opsz,wght].ttf", {"wght": 400, "opsz": 20}, "Cida Serif"),
    ("NotoSerifSC[wght].ttf", {"wght": 400}, "Cida Chinese Serif"),
]:
    original = fonts / filename
    if original.exists():
        original.replace(source / filename)
    original = source / filename
    font = instantiateVariableFont(TTFont(original), axes, inplace=True)
    # Rename derivatives so reserved upstream font names are not used for modified fonts.
    for item in font["name"].names:
        if item.nameID in (1, 4, 6, 16, 17):
            name = "Regular" if item.nameID == 17 else family.replace(" ", "") if item.nameID == 6 else family
            item.string = name.encode(item.getEncoding())
        elif item.nameID == 2:
            item.string = "Regular".encode(item.getEncoding())
    output = fonts / (family.replace(" ", "") + ".ttf")
    font.save(output)
    records.append({"source": filename, "source_sha256": hashlib.sha256(original.read_bytes()).hexdigest(), "axes": axes,
                    "output": output.name, "sha256": hashlib.sha256(output.read_bytes()).hexdigest()})

class RasterPen(BasePen):
    def __init__(self, draw, transform, fill):
        super().__init__(None)
        self.draw, self.transform, self.fill = draw, transform, fill
        self.points = []
    def point(self, point):
        tx, ty, scale = self.transform
        return ((point[0] * scale + tx) * 2, (point[1] * scale + ty) * 2)
    def _moveTo(self, point): self.points = [self.point(point)]
    def _lineTo(self, point): self.points.append(self.point(point))
    def _qCurveToOne(self, control, end):
        start = self._getCurrentPoint()
        for index in range(1, 17):
            t = index / 16
            p = tuple((1-t)**2 * start[i] + 2*(1-t)*t*control[i] + t*t*end[i] for i in range(2))
            self.points.append(self.point(p))
    def _curveToOne(self, c1, c2, end):
        start = self._getCurrentPoint()
        for index in range(1, 17):
            t = index / 16
            p = tuple((1-t)**3 * start[i] + 3*(1-t)**2*t*c1[i] + 3*(1-t)*t*t*c2[i] + t**3*end[i] for i in range(2))
            self.points.append(self.point(p))
    def _closePath(self):
        if len(self.points) > 2: self.draw.polygon(self.points, fill=self.fill)
        self.points = []
    def _endPath(self): self._closePath()

image = Image.new("RGBA", (2048, 2048), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((48, 48, 2000, 2000), radius=390, fill="#F5F3ED")
glyph = ET.parse(brand / "glyph.svg").getroot()[0]
numbers = [float(n) for n in re.findall(r"-?\d+(?:\.\d+)?", glyph.attrib["transform"])]
parse_path(glyph.attrib["d"], RasterPen(draw, tuple(numbers), "#242922"))
caret = ET.parse(brand / "caret.svg").getroot()[0].attrib
x, y, width, height = [float(caret[k]) * 2 for k in ("x", "y", "width", "height")]
draw.rectangle((x, y, x+width, y+height), fill="#346847")
image.resize((256, 256), Image.Resampling.LANCZOS).save(brand / "Cida.png")
image.resize((256, 256), Image.Resampling.LANCZOS).save(brand / "Cida.ico", sizes=[(16,16),(20,20),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
(brand.parent / "provenance.json").write_text(json.dumps({"upstream": "Xuanwo/cida", "commit": "473013c93e052e08603ffd2faccda0d6dafbb5be", "font_derivatives": records, "brand_source": ["glyph.svg", "caret.svg"], "ocr_data": {"repository": "tesseract-ocr/tessdata_fast", "commit": "87416418657359cb625c412a48b6e1d6d41c29bd"}}, ensure_ascii=False, indent=2), encoding="utf-8")
print("Static fonts, Windows ICO and provenance written.")
