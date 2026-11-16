"""Rebuild the Windows icon from the transparent chest artwork.

The source of truth is docs/icon.png (the same transparent PNG used by the site).
Run this from the .app-remote folder.
"""
from pathlib import Path
from PIL import Image

src = Path('docs/icon.png')
if not src.exists():
    raise SystemExit('docs/icon.png is missing')
img = Image.open(src).convert('RGBA')
for out in [
    Path('src/ChestCounter/Assets/ChestCounterFortnite.ico'),
    Path('src/ChestCounter/ChestCounterFortnite.ico'),
]:
    img.save(out, format='ICO', sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)])
    print('ICO:', out, out.stat().st_size, 'octets')
img.save('src/ChestCounter/Assets/ChestCounter.ico', format='ICO', sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)])
print('Done')
