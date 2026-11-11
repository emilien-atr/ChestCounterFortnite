from PIL import Image, ImageDraw
import os

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

# Fond arrondi, dégradé vertical sombre.
bg = Image.new("RGBA", (S, S), (0, 0, 0, 0))
bd = ImageDraw.Draw(bg)
top = (20, 28, 47)
bottom = (7, 10, 17)
for y in range(S):
    t = y / (S - 1)
    col = tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,)
    bd.line([(0, y), (S, y)], fill=col)
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=228, fill=255)
img.paste(bg, (0, 0), mask)

# Halo doré derrière le coffre.
glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
gd = ImageDraw.Draw(glow)
for r in range(430, 0, -8):
    a = int(30 * (1 - r / 430) ** 1.7)
    gd.ellipse([S / 2 - r, S * 0.55 - r, S / 2 + r, S * 0.55 + r], fill=(246, 185, 59, a))
img.alpha_composite(glow)

d = ImageDraw.Draw(img)
GOLD = (246, 185, 59, 255)
GOLD2 = (255, 209, 102, 255)
BODY = (23, 32, 50, 255)
LID = (28, 38, 58, 255)
DARK = (12, 18, 32, 255)

# Corps et couvercle du coffre.
d.rounded_rectangle([196, 470, 828, 806], radius=54, fill=BODY, outline=GOLD, width=30)
d.rounded_rectangle([196, 356, 828, 520], radius=82, fill=LID, outline=GOLD2, width=30)
d.line([214, 520, 810, 520], fill=(246, 185, 59, 190), width=8)
d.line([304, 470, 304, 790], fill=(246, 185, 59, 80), width=14)
d.line([720, 470, 720, 790], fill=(246, 185, 59, 80), width=14)

# Serrure.
d.rounded_rectangle([462, 470, 562, 604], radius=22, fill=GOLD2)
d.ellipse([492, 503, 532, 543], fill=DARK)
d.polygon([(500, 540), (524, 540), (531, 592), (493, 592)], fill=DARK)

# Éclat et touche violette.
d.polygon([(802, 236), (820, 292), (876, 310), (820, 328), (802, 384), (784, 328), (728, 310), (784, 292)],
          fill=(255, 224, 130, 235))
d.ellipse([228, 262, 268, 302], fill=(139, 92, 246, 225))

img.save("logo_preview.png")

out = os.path.join("src", "ChestCounter", "Assets", "ChestCounterFortnite.ico")
img.save(out, format="ICO", sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)])
print("ICO:", out, os.path.getsize(out), "octets")
