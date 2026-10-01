#!/usr/bin/env python3
"""Draws the widget package's images (logos, widget icon and the picker screenshot) from the app icon."""
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
ICON = os.path.join(HERE, "..", "..", "src", "MC1.Windows", "Assets", "AppIcon.png")
OUT = os.path.join(HERE, "Assets")
FONT = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf"
BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"

os.makedirs(OUT, exist_ok=True)
icon = Image.open(ICON).convert("RGBA")


def logo(name, size, pad=0.0):
    canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    inner = max(1, round(size * (1 - 2 * pad)))
    canvas.alpha_composite(icon.resize((inner, inner), Image.LANCZOS), ((size - inner) // 2, (size - inner) // 2))
    canvas.save(os.path.join(OUT, name), optimize=True)


logo("StoreLogo.png", 50)
logo("Square150x150Logo.png", 150, 0.12)
logo("Square44x44Logo.png", 44)
logo("ProviderIcon.png", 64)
logo("WidgetIcon.png", 64)


def battery(d, x, y, w, h, level, fg):
    d.rounded_rectangle((x, y, x + w, y + h), radius=h // 5, outline=fg, width=max(2, h // 9))
    d.rectangle((x + w + 1, y + h // 3, x + w + max(3, h // 6), y + 2 * h // 3), fill=fg)
    inset = max(3, h // 5)
    d.rectangle((x + inset, y + inset, x + inset + (w - 2 * inset) * level, y + h - inset), fill=fg)


def screenshot(name, bg, fg, sub):
    # Picker preview of the medium widget (small on the lock screen shows the number, label and battery).
    w, h = 600, 600
    im = Image.new("RGBA", (w, h), bg)
    d = ImageDraw.Draw(im)
    small = icon.resize((56, 56), Image.LANCZOS)
    im.alpha_composite(small, (40, 36))
    d.text((112, 48), "MeshCore", font=ImageFont.truetype(BOLD, 30), fill=fg)
    d.text((40, 130), "3", font=ImageFont.truetype(BOLD, 200), fill=fg)
    d.text((200, 190), "new messages", font=ImageFont.truetype(FONT, 42), fill=fg)
    d.text((200, 246), "My radio", font=ImageFont.truetype(FONT, 30), fill=sub)
    battery(d, 44, 430, 88, 44, 0.87, fg)
    d.text((160, 428), "87%", font=ImageFont.truetype(BOLD, 42), fill=fg)
    d.text((44, 500), "Radio battery", font=ImageFont.truetype(FONT, 28), fill=sub)
    im.save(os.path.join(OUT, name), optimize=True)


screenshot("WidgetScreenshot.png", (32, 42, 64, 255), (240, 240, 240, 255), (170, 180, 200, 255))
screenshot("WidgetScreenshotLight.png", (243, 245, 249, 255), (28, 36, 56, 255), (90, 100, 120, 255))
print("assets written to", OUT)
