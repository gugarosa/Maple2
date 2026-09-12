import argparse
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont, ImageOps

parser = argparse.ArgumentParser()
parser.add_argument("website")
parser.add_argument("output")
args = parser.parse_args()
source = Path(args.website)
output = Path(args.output)
output.mkdir(parents=True, exist_ok=True)
hero = Image.open(source / "ms2-world-mobile.webp").convert("RGB")
logo = Image.open(source / "ms2-logo.png").convert("RGBA")
monster = Image.open(source / "ms2-mushroom.webp").convert("RGBA")
font = ImageFont.truetype(r"C:\Windows\Fonts\segoeuib.ttf", 40)
small = ImageFont.truetype(r"C:\Windows\Fonts\segoeui.ttf", 24)

for theme, background, ink, accent in (
    ("light", "#fbfdff", "#19334a", "#086f94"),
    ("dark", "#101d29", "#e8f1f7", "#80d2ef"),
):
    panel = Image.new("RGB", (534, 1022), background)
    draw = ImageDraw.Draw(panel)
    draw.text((34, 40), "MapleTime MS2", font=font, fill=ink)
    draw.text((36, 106), "PRIVATE PILOT", font=small, fill=accent)
    game_logo = ImageOps.contain(logo, (440, 180), Image.Resampling.LANCZOS)
    panel.paste(game_logo, ((534 - game_logo.width) // 2, 192), game_logo)
    scene = ImageOps.fit(hero, (534, 660), Image.Resampling.LANCZOS, centering=(0.60, 0.5))
    panel.paste(scene, (0, 362))
    panel.save(output / f"wizard-{theme}.png", optimize=True)
    badge = Image.new("RGB", (256, 256), background)
    badge_logo = ImageOps.contain(logo, (232, 156), Image.Resampling.LANCZOS)
    badge.paste(badge_logo, ((256 - badge_logo.width) // 2, (256 - badge_logo.height) // 2), badge_logo)
    badge.save(output / f"wizard-small-{theme}.png", optimize=True)
monster.save(output / "setup.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64)])
print(json.dumps({"artwork": "NEXON MS2 art, authorized for this project", "files": 5}))
