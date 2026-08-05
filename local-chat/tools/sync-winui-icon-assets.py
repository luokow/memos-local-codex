"""Synchronize the packaged WinUI logo assets from the canonical app icon.

 The title-bar mark, page mark, package logos, taskbar icon, and desktop shortcut
 must use one source of truth. Windows App SDK turns the scale-qualified PNGs
 below into the package-facing names from Package.appxmanifest during the build.
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
ASSETS = ROOT / "src" / "QwenLocalChat.WinUI" / "Assets"
SOURCE = ASSETS / "AppIcon.ico"


def centered_icon(source: Image.Image, width: int, height: int, side: int) -> Image.Image:
    canvas = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    icon = source.resize((side, side), Image.Resampling.LANCZOS)
    canvas.alpha_composite(icon, ((width - side) // 2, (height - side) // 2))
    return canvas


def save_square(source: Image.Image, name: str, size: int, side: int) -> None:
    centered_icon(source, size, size, side).save(ASSETS / name, format="PNG", optimize=True)


def save_mark(source: Image.Image) -> None:
    """Create the small transparent speech mark used by XAML surfaces.

    The canonical app tile includes a rounded-square frame. That frame is useful
    for package identity, but it becomes a muddy black tile when reduced to a
    16px title-bar icon. Crop the canonical bubble, keep its anti-aliased white
    edge, and make the dark tile plus the three dark dots transparent so the
    same mark can compose on both the title bar and the page container.
    """
    bubble = source.crop((42, 60, 214, 204)).resize((196, 164), Image.Resampling.LANCZOS)
    pixels = bubble.load()
    for y in range(bubble.height):
        for x in range(bubble.width):
            red, green, blue, _ = pixels[x, y]
            luminance = (red + green + blue) / 3
            if luminance < 70:
                pixels[x, y] = (255, 255, 255, 0)
            else:
                alpha = max(0, min(255, int((luminance - 70) * 255 / 185)))
                pixels[x, y] = (255, 255, 255, alpha)

    canvas = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
    canvas.alpha_composite(bubble, (30, 46))
    canvas.save(ASSETS / "AppIconMark.png", format="PNG", optimize=True)


def main() -> None:
    if not SOURCE.is_file():
        raise FileNotFoundError(SOURCE)

    source = Image.open(SOURCE).convert("RGBA")

    save_mark(source)
    save_square(source, "LockScreenLogo.scale-200.png", 48, 44)
    save_square(source, "Square150x150Logo.scale-200.png", 300, 272)
    save_square(source, "Square44x44Logo.scale-200.png", 88, 80)
    save_square(source, "Square44x44Logo.targetsize-24_altform-unplated.png", 24, 22)
    save_square(source, "Square44x44Logo.targetsize-48_altform-lightunplated.png", 48, 44)
    save_square(source, "StoreLogo.png", 50, 44)
    centered_icon(source, 620, 300, 220).save(
        ASSETS / "Wide310x150Logo.scale-200.png", format="PNG", optimize=True
    )
    centered_icon(source, 1240, 600, 280).save(
        ASSETS / "SplashScreen.scale-200.png", format="PNG", optimize=True
    )

    print(f"Synchronized WinUI package logos from {SOURCE}")


if __name__ == "__main__":
    main()
