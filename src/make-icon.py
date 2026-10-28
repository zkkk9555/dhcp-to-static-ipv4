from PIL import Image, ImageDraw, ImageFont
size = 256
img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
d = ImageDraw.Draw(img)
d.rounded_rectangle([6, 6, size-6, size-6], radius=52, fill=(37, 99, 235, 255))
d.rounded_rectangle([6, 6, size-6, size-6], radius=52, outline=(21, 63, 160, 255), width=6)
font = ImageFont.truetype(r"C:\Windows\Fonts\arialbd.ttf", 120)
text = "IP"
bbox = d.textbbox((0, 0), text, font=font)
w = bbox[2] - bbox[0]
h = bbox[3] - bbox[1]
x = (size - w) / 2 - bbox[0]
y = (size - h) / 2 - bbox[1] - 6
d.text((x, y), text, font=font, fill=(255, 255, 255, 255))
# little network dots
for cx, cy in [(64, 200), (128, 214), (192, 200)]:
    d.ellipse([cx-9, cy-9, cx+9, cy+9], fill=(255, 255, 255, 230))
img.save(r"C:\Users\24310\Documents\DHCP改静态IPV4\app.ico", sizes=[(256,256),(128,128),(64,64),(48,48),(32,32),(16,16)])
print("ico written")
