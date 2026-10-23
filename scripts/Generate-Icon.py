"""Rebuild the code-native LocaleStack icon; Pillow is only needed by this developer tool."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parent.parent
image = Image.new('RGBA', (1024, 1024))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((0, 0, 1023, 1023), radius=208, fill='#121a24')
draw.polygon([(512,176),(840,352),(512,528),(184,352)], fill='#8ce8bd')
for y in (512, 680):
    draw.line([(184,y),(512,y+176),(840,y)], fill='#8ce8bd', width=56, joint='curve')
    for x in (184,840): draw.ellipse((x-28,y-28,x+28,y+28), fill='#8ce8bd')
image = image.resize((256,256), Image.Resampling.LANCZOS)
image.save(root/'assets'/'app.ico', sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
