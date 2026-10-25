#!/usr/bin/env python3
"""Turns the palettes in tools/palettes/*.toml into Selene themes: theme/Palettes/<id>.xaml plus palettes.json.

Each .xaml overrides the color keys of theme/Constants.xaml; Selene Core layers it on top at startup.
Re-run after adding or editing a palette: python3 tools/make_palettes.py
"""
import json
import tomllib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCES = ROOT / "tools" / "palettes"
OUT = ROOT / "theme" / "Palettes"
WALLPAPERS = ROOT / "theme" / "Wallpapers"

NAMES = {
    "catppuccin": "Catppuccin", "catppuccin-latte": "Catppuccin Latte", "ethereal": "Ethereal", "everforest": "Everforest",
    "flexoki-light": "Flexoki Light", "gruvbox": "Gruvbox", "hackerman": "Hackerman", "kanagawa": "Kanagawa",
    "last-horizon": "Last Horizon", "lumon": "Frost", "lupine": "Lupine", "matte-black": "Matte Black", "miasma": "Miasma",
    "nord": "Nord", "osaka-jade": "Osaka Jade", "retro-82": "Retro 82", "ristretto": "Ristretto", "rose-pine": "Rosé Pine",
    "solitude": "Solitude", "tokyo-night": "Tokyo Night", "vantablack": "Vantablack", "white": "White",
}

# Selene's own moonlight look (the values in theme/Constants.xaml), first in the list and the default
SELENE = {
    "mode": "dark", "accent": "#9CCBFF", "background": "#0A142C", "dark_background": "#0A142C",
    "darker_background": "#03081A", "lighter_background": "#1A2A4A", "foreground": "#EBEBEB",
    "dark_foreground": "#757575", "light_foreground": "#B8C7DD", "bright_foreground": "#EAF3FF", "red": "#FF6B6B",
    "top_background": "#102A5C",
    # Selene keeps its white focus, black scrims and neutral glass; the other themes derive these from their palette
    "focus": "#FFFFFF", "on_focus": "#111111", "ink": "#FFFFFF", "scrim": "#000000", "glass": "#14181F",
    "green": "#3DDC84",
}


def argb(color, alpha="FF"):
    return "#" + alpha + color.lstrip("#").upper()


# brushes of theme/Constants.xaml, written out with literal colors: a brush in a resource dictionary keeps the color it
# had when it loaded, so a theme has to replace the brushes themselves for DynamicResource users to change
BRUSHES = [
    ("TextBrush", "TextColor", 1), ("TextBrushDark", "TextColorDark", 1), ("GlyphBrush", "GlyphColor", 1),
    ("GlyphLightBrush", "GlyphLightColor", 1), ("ControlBackgroundBrush", "ControlBackgroundColor", 1),
    ("ControlBackgroundDarkBrush", "ControlBackgroundColorDark", 1), ("SelectionLightBrush", "SelectionLightColor", 1),
    ("OverlayBrush", "OverlayColor", 1), ("CheckBoxCheckMarkBkBrush", "ControlBackgroundColorDark", 1),
    ("SeleneFocusBrush", "SeleneFocusColor", 1), ("SeleneOnFocusBrush", "SeleneOnFocusColor", 1),
    ("SeleneInkBrush", "SeleneInkColor", 1), ("SeleneInk90Brush", "SeleneInkColor", 0.9),
    ("SeleneInk60Brush", "SeleneInkColor", 0.6), ("SeleneInk25Brush", "SeleneInkColor", 0.25),
    ("SeleneInk15Brush", "SeleneInkColor", 0.15), ("SeleneScrimBrush", "SeleneScrimColor", 1),
    ("SeleneScrim65Brush", "SeleneScrimColor", 0.65), ("SeleneScrim45Brush", "SeleneScrimColor", 0.45),
    ("SeleneGlassBrush", "SeleneGlassColor", 0.75), ("SeleneGlassSolidBrush", "SeleneGlassColor", 0.88),
    ("SeleneGlassLightBrush", "SeleneGlassColor", 0.45), ("SeleneSuccessBrush", "SeleneSuccessColor", 1),
]


def luminance(color):
    def channel(v):
        v /= 255
        return v / 12.92 if v <= 0.03928 else ((v + 0.055) / 1.055) ** 2.4
    h = color.lstrip("#")
    r, g, b = (channel(int(h[i:i + 2], 16)) for i in (0, 2, 4))
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def shade(color, keep):
    """the color darkened toward black, keeping `keep` of its brightness"""
    h = color.lstrip("#")
    return "#" + "".join("%02X" % round(int(h[i:i + 2], 16) * keep) for i in (0, 2, 4))


def saturation(color):
    h = color.lstrip("#")
    r, g, b = (int(h[i:i + 2], 16) / 255 for i in (0, 2, 4))
    hi, lo = max(r, g, b), min(r, g, b)
    return 0 if hi == 0 else (hi - lo) / hi


def contrast(a, b):
    hi, lo = sorted((luminance(a), luminance(b)), reverse=True)
    return (hi + 0.05) / (lo + 0.05)


def xaml(theme_id, name, c, wallpaper):
    bg = c["background"]
    dark = c.get("dark_background", bg)
    darker = c.get("darker_background", dark)
    lighter = c.get("lighter_background", c.get("selection", bg))
    fg = c["foreground"]
    top = c.get("top_background", lighter)
    colors = {
        "GlyphColor": argb(c["accent"]),
        "GlyphLightColor": argb(c.get("bright_foreground", fg)),
        "SelectionLightColor": argb(c.get("light_foreground", fg)),
        "OverlayColor": argb(darker, "CC"),
        "ControlBackgroundColor": argb(lighter),
        "ControlBackgroundColorDark": argb(dark),
        "TextColor": argb(fg),
        "TextColorDark": argb(c.get("dark_foreground", c.get("muted", fg))),
    }
    # Selene's own keys: focus fill/ring (the accent), text on it (whichever of background/foreground reads better),
    # text over game art, the scrim that darkens (or, in light themes, lightens) art, glass panels, success green
    focus = c.get("focus", c["accent"])
    # a grey accent (White, Vantablack...) makes a dull focus on the dark glass: focus in the theme's lightest color
    # instead (its background in light themes, its bright text in dark ones)
    if "focus" not in c and saturation(focus) < 0.15:
        focus = bg if c.get("mode", "dark") == "light" else c.get("bright_foreground", fg)
    bright = c.get("bright_foreground", fg)
    on_focus = c.get("on_focus", max((bg, bright), key=lambda x: contrast(x, focus)))
    # warnings sit on the dark glass and over art: a red that doesn't stand out there (grey palettes) becomes a clear red
    warning = c.get("red", "#FF6B6B")
    if contrast(warning, c.get("glass", shade(fg, 0.55) if c.get("mode", "dark") == "light" else dark)) < 3 or saturation(warning) < 0.3:
        warning = "#FF6B6B"
    # over game art a light scrim washes the picture out, so light themes keep a dark scrim and glass (a deep shade of
    # their own text color) with their light background color as the text on it; their light side stays in the
    # gradient, wallpaper, accent and Playnite's own panels
    light = c.get("mode", "dark") == "light"
    ink = c.get("ink", bg if light else bright)
    scrim = c.get("scrim", shade(fg, 0.42) if light else darker)
    glass = c.get("glass", shade(fg, 0.55) if light else dark)
    # the dim behind Playnite's menus and dialogs: a light one turned the whole home bright, so light themes dim with
    # the dark scrim too
    if light:
        colors["OverlayColor"] = argb(scrim, "CC")
    colors.update({
        "SeleneFocusColor": argb(focus),
        "SeleneOnFocusColor": argb(on_focus),
        "SeleneInkColor": argb(ink),
        "SeleneScrimColor": argb(scrim),
        "SeleneGlassColor": argb(glass),
        "SeleneSuccessColor": argb(c.get("green", "#3DDC84")),
    })
    lines = [
        '<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"',
        '                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"',
        '                    xmlns:sys="clr-namespace:System;assembly=mscorlib">',
        f"    <!-- Generated by tools/make_palettes.py from tools/palettes/{theme_id}.toml - edit the .toml, not this file -->",
        f'    <sys:String x:Key="SeleneThemeId">{theme_id}</sys:String>',
        f'    <sys:String x:Key="SeleneThemeName">{name}</sys:String>',
        f'    <sys:String x:Key="SeleneThemeMode">{c.get("mode", "dark")}</sys:String>',
        f'    <sys:String x:Key="SeleneDefaultWallpaper">{wallpaper}</sys:String>',
        "",
    ]
    lines += [f'    <Color x:Key="{k}">{v}</Color>' for k, v in colors.items()]
    lines.append("")
    lines += [f'    <SolidColorBrush x:Key="{k}" Color="{colors[ck]}"' + (f' Opacity="{o}"' if o != 1 else "") + " />"
              for k, ck, o in BRUSHES]
    focus_c = colors["SeleneFocusColor"]
    lines += [
        f'    <DropShadowEffect x:Key="SeleneFocusGlow" ShadowDepth="0" BlurRadius="18" Color="{focus_c}" Opacity="0.35" />',
        f'    <DropShadowEffect x:Key="SeleneFocusGlowStrong" ShadowDepth="0" BlurRadius="18" Color="{focus_c}" Opacity="0.55" />',
        '    <LinearGradientBrush x:Key="SelectionBrush" StartPoint="0.5,-0.5" EndPoint="1,1.5">',
        f'        <GradientStop Color="{focus_c}" Offset="0" />',
        f'        <GradientStop Color="{colors["GlyphColor"]}" Offset="1.0" />',
        "    </LinearGradientBrush>",
    ]
    lines += [
        "",
        '    <RadialGradientBrush x:Key="SeleneBaseBrush" GradientOrigin="0.5,0.1" Center="0.5,0.2" RadiusX="0.5" RadiusY="0.7">',
        f'        <GradientStop Color="{argb(top)}" Offset="0" />',
        f'        <GradientStop Color="{argb(darker)}" Offset="1.0" />',
        "    </RadialGradientBrush>",
        '    <LinearGradientBrush x:Key="TopBarBackgroundBrush" StartPoint="-0.5,0.5" EndPoint="1,0.5">',
        f'        <GradientStop Color="{argb(dark)}" Offset="0" />',
        f'        <GradientStop Color="{argb(darker, "AA")}" Offset="1.0" />',
        "    </LinearGradientBrush>",
        '    <LinearGradientBrush x:Key="BottomBarBackgroundBrush" StartPoint="-0.5,0.5" EndPoint="1.5,1">',
        f'        <GradientStop Color="{argb(dark)}" Offset="0" />',
        f'        <GradientStop Color="{argb(darker, "AA")}" Offset="1.0" />',
        "    </LinearGradientBrush>",
        '    <LinearGradientBrush x:Key="OverlayMenuBackgroundBrush" StartPoint="0.5,0" EndPoint="0.5,1.5">',
        f'        <GradientStop Color="{argb(top, "F2")}" Offset="0" />',
        f'        <GradientStop Color="{argb(darker, "F2")}" Offset="1.0" />',
        "    </LinearGradientBrush>",
        '    <LinearGradientBrush x:Key="GameDetailsBackgroundBrush" StartPoint="0.5,0" EndPoint="0.5,0.4">',
        f'        <GradientStop Color="{argb(dark, "00")}" Offset="0.0" />',
        f'        <GradientStop Color="{argb(darker, "EE")}" Offset="0.8" />',
        "    </LinearGradientBrush>",
        f'    <SolidColorBrush x:Key="WarningBrush" Color="{argb(warning)}" />',
        "</ResourceDictionary>",
        "",
    ]
    return "\n".join(lines)


def default_wallpaper(theme_id):
    folder = WALLPAPERS / theme_id
    files = sorted(p.name for p in folder.iterdir() if p.is_file()) if folder.is_dir() else []
    return f"Wallpapers/{theme_id}/{files[0]}" if files else ""


OUT.mkdir(parents=True, exist_ok=True)
themes = [("selene", "Selene", SELENE)]
themes += [(p.stem, NAMES.get(p.stem, p.stem.replace("-", " ").title()), tomllib.loads(p.read_text(encoding="utf-8")))
           for p in sorted(SOURCES.glob("*.toml"))]

SWATCHES = ("background", "lighter_background", "foreground", "accent", "red", "orange", "yellow", "green", "cyan", "blue", "magenta")
index = []
for theme_id, name, colors in themes:
    wallpaper = default_wallpaper(theme_id)
    (OUT / f"{theme_id}.xaml").write_text(xaml(theme_id, name, colors, wallpaper), encoding="utf-8")
    index.append({"Id": theme_id, "Name": name, "Mode": colors.get("mode", "dark"), "Accent": colors["accent"],
                  "Background": colors["background"], "Foreground": colors["foreground"], "Wallpaper": wallpaper,
                  # the strip of colors under the theme step's preview: its background shades, text, accent, then the rest
                  "Swatches": list(dict.fromkeys(colors[k].lower() for k in SWATCHES if k in colors))})
(OUT / "palettes.json").write_text(json.dumps(index, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(f"wrote {len(index)} themes to {OUT.relative_to(ROOT)}")
