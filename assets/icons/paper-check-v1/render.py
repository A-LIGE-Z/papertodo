"""Export PaperTodo's editable SVG artwork. Requires Pillow and resvg-py."""
from io import BytesIO
from pathlib import Path
import argparse
from copy import deepcopy
import shutil
import xml.etree.ElementTree as ET

from PIL import Image, ImageDraw, ImageFont
from resvg_py import svg_to_bytes

ROOT = Path(__file__).resolve().parent
REPOSITORY = ROOT.parents[2]
SVG = '{http://www.w3.org/2000/svg}'


def render(size: int, small: bool = False) -> Image.Image:
    source = ROOT / ('papertodo-small.svg' if small else 'papertodo.svg')
    data = svg_to_bytes(svg_path=str(source), width=size, height=size,
                        skip_system_fonts=True)
    return Image.open(BytesIO(data)).convert('RGBA')


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont:
    return ImageFont.truetype('C:/Windows/Fonts/msyhbd.ttc' if bold else
                              'C:/Windows/Fonts/msyh.ttc', size)


def preview() -> None:
    canvas = Image.new('RGBA', (1240, 820), '#F4F6FB')
    draw = ImageDraw.Draw(canvas)
    draw.text((80, 42), 'PAPERTODO', font=font(20, True), fill='#2866D9')
    draw.text((76, 88), '一张纸，轻一点。', font=font(52, True), fill='#18243B')
    draw.text((80, 168), '蓝白纸片 · 轻量待办', font=font(21), fill='#728099')
    canvas.alpha_composite(render(512), (93, 225))

    draw.rounded_rectangle((710, 235, 1158, 466), radius=24,
                           fill='#FFFFFF', outline='#E6EAF2', width=1)
    draw.text((738, 257), '小尺寸，也清晰', font=font(18, True), fill='#3A4861')
    for size, center in [(24, 764), (32, 849), (48, 950), (64, 1070)]:
        canvas.alpha_composite(render(size, small=size <= 32),
                               (center - size // 2, 368 - size))
        label = f'{size} px'
        draw.text((center, 395), label, anchor='mm', font=font(14), fill='#8590A4')

    draw.rounded_rectangle((710, 490, 1158, 736), radius=24, fill='#19243A')
    draw.text((738, 512), '深色桌面', font=font(18, True), fill='#E2EAF8')
    canvas.alpha_composite(render(136), (866, 564))
    draw.text((80, 770), '折角纸片  /  清晰勾选  /  克制的蓝色',
              font=font(15), fill='#8691A4')
    draw.text((1158, 779), 'SVG  ·  PNG  ·  ICO', anchor='rm',
              font=font(15), fill='#8691A4')
    canvas.convert('RGB').save(ROOT / 'preview.png', optimize=True)


def adaptive_sources() -> tuple[str, str]:
    ET.register_namespace('', SVG[1:-1])
    source = ET.parse(ROOT / 'papertodo.svg').getroot()
    foreground = deepcopy(source)
    # Android supplies the outer mask and background; retain only the paper art.
    for child in list(foreground):
        if child.tag == SVG + 'rect':
            foreground.remove(child)
    art = ET.SubElement(foreground, SVG + 'g',
                        {'transform': 'translate(512 512) scale(.8) translate(-512 -512)'})
    for child in list(foreground):
        if child is not art and child.tag == SVG + 'g':
            foreground.remove(child)
            art.append(child)

    sheet = source.find(f"{SVG}defs/{SVG}path[@id='sheet']")
    check = next(path for path in source.iter(SVG + 'path')
                 if path.get('stroke') == 'url(#check)')
    back = source.find(f"{SVG}g/{SVG}rect")
    assert sheet is not None and back is not None
    monochrome = ET.Element(SVG + 'svg', {'width': '1024', 'height': '1024',
                                         'viewBox': '0 0 1024 1024', 'fill': 'none'})
    definitions = ET.SubElement(monochrome, SVG + 'defs')
    mask = ET.SubElement(definitions, SVG + 'mask', {'id': 'cutout', 'maskUnits': 'userSpaceOnUse',
                                                   'x': '0', 'y': '0', 'width': '1024', 'height': '1024'})
    ET.SubElement(mask, SVG + 'rect', {'width': '1024', 'height': '1024', 'fill': 'white'})
    cutout = deepcopy(check)
    cutout.set('stroke', 'black')
    mask.append(cutout)
    group = ET.SubElement(monochrome, SVG + 'g', {'transform': art.get('transform')})
    silhouette = ET.SubElement(group, SVG + 'g', {'mask': 'url(#cutout)'})
    rear = deepcopy(back)
    rear.set('fill', 'white')
    silhouette.append(rear)
    ET.SubElement(silhouette, SVG + 'path', {'d': sheet.get('d'), 'fill': 'white'})
    return ET.tostring(foreground, encoding='unicode'), ET.tostring(monochrome, encoding='unicode')


def install() -> None:
    foreground, monochrome = adaptive_sources()
    (ROOT / 'papertodo-adaptive.svg').write_text(foreground, encoding='utf-8')
    (ROOT / 'papertodo-monochrome.svg').write_text(monochrome, encoding='utf-8')
    shutil.copyfile(ROOT / 'papertodo.ico', REPOSITORY / 'assets/icons/PaperTodo.ico')
    shutil.copyfile(ROOT / 'papertodo.ico', REPOSITORY / 'docs/assets/favicon.ico')
    resources = REPOSITORY / 'mobile/android/app/src/main/res'
    for density, legacy_size, adaptive_size in [
        ('mdpi', 48, 108), ('hdpi', 72, 162), ('xhdpi', 96, 216),
        ('xxhdpi', 144, 324), ('xxxhdpi', 192, 432),
    ]:
        directory = resources / f'mipmap-{density}'
        directory.mkdir(exist_ok=True)
        render(legacy_size).save(directory / 'ic_launcher.png', optimize=True)
        for name, source in [('foreground', foreground), ('monochrome', monochrome)]:
            data = svg_to_bytes(svg_string=source, width=adaptive_size,
                                height=adaptive_size, skip_system_fonts=True)
            (directory / f'ic_launcher_{name}.png').write_bytes(data)
    # Solid parts of the themed mark must fit Android's 66 dp safe circle.
    mask = Image.open(resources / 'mipmap-xxxhdpi/ic_launcher_monochrome.png').convert('RGBA')
    radius = max(((x - 215.5) ** 2 + (y - 215.5) ** 2) ** .5
                 for y in range(432) for x in range(432) if mask.getpixel((x, y))[3] >= 128)
    assert radius < 432 * 33 / 108, radius
    print(f'Installed Windows, website and Android icons; safe-circle radius {radius:.1f}/132 px.')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--install', action='store_true')
    parser.add_argument('--skip-preview', action='store_true')
    options = parser.parse_args()
    for size in [1024, 512, 256]:
        render(size).save(ROOT / f'papertodo-{size}.png', optimize=True)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [render(size, small=size <= 32) for size in sizes]
    frames[-1].save(ROOT / 'papertodo.ico', format='ICO',
                    sizes=[(size, size) for size in sizes],
                    append_images=frames[:-1])
    if not options.skip_preview:
        preview()
    icon = Image.open(ROOT / 'papertodo.ico')
    assert icon.ico.sizes() == {(size, size) for size in sizes}
    for size in [1024, 512, 256]:
        png = Image.open(ROOT / f'papertodo-{size}.png')
        assert png.mode == 'RGBA' and png.size == (size, size)
        assert png.getpixel((0, 0))[3] == 0
    print('Exported transparent PNGs, 9-size ICO, and preview.')
    if options.install:
        install()
