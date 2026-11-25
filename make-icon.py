from PIL import Image, ImageDraw

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# vertical gradient: #1f8ae0 -> #0b5a9a
grad = Image.new("RGB", (1, S))
for y in range(S):
    t = y / (S - 1)
    grad.putpixel((0, y), (
        int(0x1f + (0x0b - 0x1f) * t),
        int(0x8a + (0x5a - 0x8a) * t),
        int(0xe0 + (0x9a - 0xe0) * t),
    ))
grad = grad.resize((S, S))

mask = Image.new("L", (S, S), 0)
md = ImageDraw.Draw(mask)
md.rounded_rectangle([0, 0, S - 1, S - 1], radius=210, fill=255)
img.paste(grad, (0, 0), mask)

# Wi-Fi arcs + dot
cx, cy = S // 2, int(S * 0.72)
white = (255, 255, 255, 255)
for radius in (150, 300, 450):
    d.arc([cx - radius, cy - radius, cx + radius, cy + radius],
          start=225, end=315, fill=white, width=72)
d.ellipse([cx - 46, cy - 46, cx + 46, cy + 46], fill=white)

sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
img.save(r"C:\Users\17962\Desktop\新建文件夹 (2)\HotspotAuto\src\app.ico",
         format="ICO", sizes=sizes)
print("icon written")
