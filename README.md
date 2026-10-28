<div align="center">

<img src="assets/icon.png" alt="Glance Icon" width="160" height="160" />

# Glance for macOS

**The blazingly fast, native macOS photo viewer and pro culling tool.**  
*Instant full-screen folder browsing, zero-latency keyboard navigation, and professional RAW culling.*

[![Release](https://img.shields.io/github/v/release/mortalezz/glance?color=00c7b7&style=flat-square)](https://github.com/mortalezz/glance/releases/latest)
[![macOS](https://img.shields.io/badge/macOS-13.0%2B-000000?style=flat-square&logo=apple&logoColor=white)](https://github.com/mortalezz/glance)
[![Swift](https://img.shields.io/badge/Swift-5.9%20%2F%206-FA7343?style=flat-square&logo=swift&logoColor=white)](https://github.com/mortalezz/glance)
[![Architecture](https://img.shields.io/badge/Engine-Zero%20Electron%20%7C%20Pure%20Native-blueviolet?style=flat-square)](https://github.com/mortalezz/glance)
[![Performance](https://img.shields.io/badge/Navigation-Zero--Latency%20Lookahead-brightgreen?style=flat-square)](https://github.com/mortalezz/glance)
[![Metadata](https://img.shields.io/badge/Ecosystem-Adobe%20XMP%20Sidecars-orange?style=flat-square)](https://github.com/mortalezz/glance)
[![License](https://img.shields.io/badge/License-MIT-blue?style=flat-square)](LICENSE)

</div>

---

## 🕊️ In Loving Memory of Phoenix Slides: The Premise of Glance

For years, one utility reigned supreme on the Mac: **Phoenix Slides**. It solved a deceptively simple problem better than anything else before or since:

> **Open any picture from a folder straight into a full-screen view, and immediately fly through the entire directory with pure keyboard navigation.**

As macOS evolved and Phoenix Slides faded into history, a frustrating void emerged. Modern image viewers became bloated, slow, web-tech-wrapped viewers, or overcomplicated catalog managers that demand importing photos into proprietary databases before you can even look at them. Simple full-screen folder traversal with zero latency became virtually nonexistent on modern macOS.

**Glance was born directly out of that frustration.**

The founding goal of Glance is the **preservation of this singular, beloved feature**:
1. Open any photo directly from Finder straight into edge-to-edge full screen.
2. Navigate the entire folder instantly with arrow keys or `Space` — zero lag, zero spinners, zero waiting.
3. Clean, distraction-free viewing with native macOS Apple Vibrancy frosted glass overlays that vanish when you don't need them.

From this uncompromising foundation, Glance adds modern pro photographer culling superpowers: 1:1 pixel peeping, burst compare, Adobe XMP sidecar sync, hardware-accelerated histograms, and exposure blinkies.

> 💡 **Community Note**: Glance is open and actively evolving. Testing, feature ideas, and feedback from everyday Mac users and professional photographers alike are always warmly welcome!

---

## ⚡ Proud Features

- 🚀 **Zero-Latency Keyboard Browsing**: Speculative triple-buffering and asynchronous background lookahead decode make folder navigation instantaneous even on 45MP+ RAW and high-resolution JPEG files.
- 🔍 **Instant 100% 1:1 Pixel Peep (`Z` / Click to Zoom)**: Instant zoom to actual device sensor pixels centered right under your mouse cursor, with smooth trackpad and mouse panning.
- 👥 **2-Up Burst Compare (`S` / `Tab`)**: Side-by-side comparison to instantly catch micro-blur, closed eyes, and critical focus differences between consecutive frames, complete with synchronous 1:1 panning.
- 🏷️ **Industry-Standard Adobe XMP Sidecar Sync**: Real-time non-destructive rating (`★`), color tagging, and flag management fully compatible with **Adobe Lightroom Classic, Capture One, Darktable, and Photo Mechanic**.
- 🗂️ **RAW + JPEG Sibling Stacking**: Automatically pairs camera RAWs (`.ARW`, `.CR2`, `.CR3`, `.NEF`, `.RAF`, `.DNG`, etc.) with companion JPEGs, browsing the fast preview and inspecting the master RAW sensor data during zoom.
- 📊 **Real-Time RGB & Luminance Histogram (`H`)**: Hardware-accelerated 256-bin RGB and Rec. 709 Luminance curves rendered in a sleek frosted-glass HUD with shadow/highlight clipping indicators.
- 🚨 **Exposure Clipping Warnings / "Blinkies" (`O`)**: Animated pulsating overlays for overexposed blown highlights (Red) and underexposed crushed shadows (Blue).
- 📷 **Dedicated EXIF HUD (`I`)**: Frosted glass HUD displaying lens model, camera body, shutter speed, aperture, ISO, focal length, and capture date.
- 🔄 **Lossless EXIF Rotation (`R` / `]` and `L` / `[` )**: Rotates JPEGs, HEICs, and RAW XMP sidecars in 90° increments without re-compressing pixels.
- ⚡ **One-Key Quick Export (`E`)**: Copies all flagged Picks (or current photo) and companion sidecars to `./Picks` with sound confirmation and Finder reveal.
- 📽️ **Hands-Free Slideshow (`Return`)**: Full-screen automated slideshow with instant interruption on any user interaction.

---

## ⌨️ Complete Keyboard Shortcuts Cheatsheet

Press **`?`** (`Shift + /`) at any time inside Glance to bring up the interactive frosted-glass shortcuts cheatsheet.

### Culling & Rating
| Key | Action | Description |
| :--- | :--- | :--- |
| **`P`** | **Pick / Flag** | Green badge keeper |
| **`X`** | **Reject** | Red badge reject (marked for trash) |
| **`U`** | **Unflag** | Neutral / clear pick status |
| **`1` – `5`** | **Star Rating** | Rates image `★` to `★★★★★` (vibrant gold) |
| **`0`** | **Clear Rating** | Clears star rating |
| **`6` – `9`** | **Color Labels** | 6: Red, 7: Yellow, 8: Green, 9: Blue |
| **`C`** | **Auto-Advance** | **Speed Culling**: Any tag/rating automatically steps to next image |

### Review, Focus & Comparison
| Key | Action | Description |
| :--- | :--- | :--- |
| **`Z`** / **Click** | **100% Pixel Peep** | Zoom to 1:1 sensor pixels at cursor; drag to pan |
| **`S`** / **`Tab`** | **2-Up Compare** | Side-by-side comparison with synchronous 1:1 zoom |
| **`\`** | **Cycle Filters** | Cycles: `All` ➔ `Hide Rejects` ➔ `Picks Only` ➔ `Rejects Only` ➔ `Unrated` ➔ `3+ Stars` ➔ `5 Stars` |

### Exposure & Information Overlays
| Key | Action | Description |
| :--- | :--- | :--- |
| **`H`** | **RGB Histogram** | Live 256-bin RGB & Luminance histogram HUD |
| **`O`** | **Exposure Clipping** | Pulsing highlight (Red) and shadow (Blue) "blinkies" |
| **`I`** | **EXIF Info** | Frosted-glass camera, lens, and exposure parameters overlay |
| **`?`** | **Cheatsheet** | Toggle shortcuts cheatsheet |

### Workflow & Productivity
| Key | Action | Description |
| :--- | :--- | :--- |
| **`R`** or **`]`** | **Rotate CW** | Lossless 90° clockwise rotation |
| **`L`** or **`[`** | **Rotate CCW** | Lossless 90° counter-clockwise rotation |
| **`E`** | **Quick Export** | Copies all Picks (or current photo) to `./Picks` folder |
| **`Return`** | **Slideshow** | Start/stop automated hands-free slideshow |
| **`Delete`** / **`Backspace`** | **Trash** | Move active file (and its RAW / XMP pair) to macOS Trash |
| **`Shift + Delete`** | **Purge Rejects** | Safely batch moves all rejected (`X`) images to macOS Trash |

### Navigation
| Key | Action |
| :--- | :--- |
| **Right Arrow**, **Space**, **Down**, **N** | Next image |
| **Left Arrow**, **Up**, **Page Up**, **K** | Previous image |
| **Home** / **End** | First / Last image in folder |
| **F** | Toggle Full Screen |
| **Esc**, **Q** | Close / Exit |

---

## 🚀 Download & Installation

### Option 1: Direct Download (Recommended)
Download the latest pre-compiled application bundle from the [**Releases Page**](https://github.com/mortalezz/glance/releases/latest):
1. Download **`Glance-v1.0.0-macOS.zip`**.
2. Unzip and drag `Glance.app` into your `/Applications` folder.
3. Right-click any image in Finder ➔ *Open With* ➔ *Glance* (or set Glance as your default image viewer).

### Option 2: Build from Source
```bash
# Clone the repository
git clone https://github.com/mortalezz/glance.git
cd glance

# Build and package the release application bundle
./scripts/build_app.sh

# Launch Glance with any folder or photo:
open Glance.app /path/to/folder
```

---

## 🧪 Engineering Test Suite

Glance comes with a comprehensive standalone verification test suite covering XMP parsing, RAW+JPEG stacking, histogram math, clipping algorithms, and lossless rotation:

```bash
swiftc -o /tmp/test_glance \
  Sources/Glance/Model/CullingMetadata.swift \
  Sources/Glance/Model/SlideItem.swift \
  Sources/Glance/Model/ExifMetadata.swift \
  Sources/Glance/Engine/SupportedFormats.swift \
  Sources/Glance/Engine/DirectoryScanner.swift \
  Sources/Glance/Engine/XmpSidecarManager.swift \
  Sources/Glance/Engine/HistogramEngine.swift \
  Sources/Glance/Engine/ClippingEngine.swift \
  Sources/Glance/Engine/ExportManager.swift \
  Sources/Glance/Engine/LosslessRotationEngine.swift \
  scripts/test_xmp.swift && /tmp/test_glance
```

---

## 📄 License

Glance is open-source software licensed under the [MIT License](LICENSE).
