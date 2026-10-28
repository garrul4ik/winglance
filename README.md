<div align="center">

<img src="assets/icon.png" alt="Glance Icon" width="160" height="160" />

# 📸 Glance for macOS ⚡

### The ultra-fast, native macOS photo viewer & pro culling tool 🚀  
*Instant full-screen folder browsing, zero-latency keyboard navigation, and pro RAW culling.*

[![Release 🎉](https://img.shields.io/github/v/release/mortalezz/glance?color=00c7b7&style=flat-square&label=Release%20%F0%9F%8E%89)](https://github.com/mortalezz/glance/releases/latest)
[![macOS ](https://img.shields.io/badge/macOS-13.0%2B-000000?style=flat-square&logo=apple&logoColor=white&label=%EF%A3%BF%20macOS)](https://github.com/mortalezz/glance)
[![Swift 🧡](https://img.shields.io/badge/Swift-5.9%20%2F%206-FA7343?style=flat-square&logo=swift&logoColor=white&label=%F0%9F%A7%A1%20Swift)](https://github.com/mortalezz/glance)
[![Engine ⚡](https://img.shields.io/badge/Zero%20Electron-Pure%20Native-blueviolet?style=flat-square&label=%E2%9A%A1%20Engine)](https://github.com/mortalezz/glance)
[![Speed 🚀](https://img.shields.io/badge/Lookahead-Zero--Latency-brightgreen?style=flat-square&label=%F0%9F%9A%80%20Speed)](https://github.com/mortalezz/glance)
[![Metadata 🏷️](https://img.shields.io/badge/Adobe%20XMP-Lightroom%20%26%20C1-orange?style=flat-square&label=%F0%9F%8F%B7%EF%B8%8F%20XMP)](https://github.com/mortalezz/glance)
[![License 📄](https://img.shields.io/badge/License-MIT-blue?style=flat-square&label=%F0%9F%93%84%20License)](LICENSE)

</div>

---

> [!NOTE]
> ### 🕊️ In Loving Memory of Phoenix Slides: The Premise of Glance
>
> For years, one lightweight utility reigned supreme on the Mac: **Phoenix Slides**. It solved a deceptively simple problem better than anything else before or since:
>
> 🎯 **Open any picture from a folder straight into edge-to-edge full screen, and immediately fly through the entire directory with zero-latency keyboard navigation.**
>
> As macOS evolved and Phoenix Slides faded into history, a frustrating void emerged. Modern image viewers became bloated, sluggish web-tech wrappers, or overcomplicated catalog managers demanding that photos be imported into proprietary databases before you can even view them. Simple full-screen folder traversal with zero latency became virtually nonexistent on modern macOS.
>
> **Glance was born directly out of that frustration.**
>
> The founding goal of Glance is the **uncompromising preservation of this timeless experience**:
> 1. 🖼️ **Instant Full-Screen Launch**: Open any photo directly from Finder straight into full screen.
> 2. ⚡ **Zero-Latency Traversal**: Fly through thousands of 45MP+ RAW and JPEG shots using arrow keys or `Space` — zero lag, zero spinners, zero waiting.
> 3. 🫧 **Distraction-Free Vibrancy**: Native macOS Apple Vibrancy frosted-glass squircle HUDs that appear only when needed and vanish automatically.
>
> On top of this rock-solid foundation, Glance equips photographers with pro culling superpowers: 1:1 pixel peeping, 2-up burst comparison, Adobe XMP sidecar sync, real-time RGB histograms, and exposure clipping blinkies.

> [!TIP]
> 💡 **Community Testing & Feedback Welcome!**  
> Glance is open-source and actively evolving. Feedback, feature requests, and testing on real-world photo shoots from photographers and Mac enthusiasts are always warmly welcomed!

---

## ⚡ Proud Features

| Feature | Highlight |
| :--- | :--- |
| 🚀 **Zero-Latency Keyboard Navigation** | Speculative triple-buffering and asynchronous background lookahead decode make folder traversal instantaneous on 45MP+ RAW and high-resolution JPEG files. |
| 🔍 **100% 1:1 Pixel Peep (`Z` / Click)** | Instant zoom to actual device sensor pixels centered right under your mouse cursor, with silky-smooth trackpad and mouse panning. |
| 👥 **2-Up Burst Compare (`S` / `Tab`)** | Side-by-side split comparison to catch micro-blur, closed eyes, and critical focus differences between consecutive frames, complete with synchronous 1:1 panning. |
| 🏷️ **Adobe XMP Sidecar Synchronization** | Real-time, non-destructive rating (`★`), color tagging, and flag management fully compatible with **Adobe Lightroom Classic, Capture One, Darktable, and Photo Mechanic**. |
| 🗂️ **RAW + JPEG Sibling Stacking** | Automatically pairs camera RAWs (`.ARW`, `.CR2`, `.CR3`, `.NEF`, `.RAF`, `.DNG`, etc.) with companion JPEGs, browsing the fast preview and inspecting master RAW sensor data during zoom. |
| 📊 **Hardware RGB & Luminance Histogram (`H`)** | 256-bin SIMD/vImage hardware-accelerated histogram rendered inside an Apple Vibrancy frosted-glass HUD with shadow and highlight clipping indicators. |
| 🚨 **Exposure Clipping Warnings / "Blinkies" (`O`)** | Animated pulsating zebra overlays for overexposed blown highlights (Red) and underexposed crushed shadows (Blue). |
| 📷 **Dedicated Frosted EXIF HUD (`I`)** | Displays lens model, camera body, shutter speed, aperture, ISO, focal length, and capture date in a clean glass overlay. |
| 🔄 **Lossless EXIF Rotation (`R` / `]` and `L` / `[` )** | Rotates JPEGs, HEICs, and RAW XMP sidecars in 90° increments without re-compressing pixels. |
| 📦 **One-Key Quick Export (`E`)** | Copies all flagged Picks (or current photo) and companion sidecars to `./Picks` with sound confirmation and Finder reveal. |
| 📽️ **Hands-Free Slideshow (`Return`)** | Full-screen automated slideshow with instant interruption on any user interaction. |
| 🪶 **Zero Bloat, Zero Electron** | Pure native Swift & AppKit, compiling down to a lightweight standalone binary without web runtimes. |

---

## ⌨️ Complete Keyboard Shortcuts Cheatsheet

Press **`?`** (`Shift + /`) at any time inside Glance to toggle the interactive frosted-glass shortcuts cheatsheet.

### 🏷️ Culling & Rating
| Key | Action | Description |
| :---: | :--- | :--- |
| **`P`** | **Pick / Flag 🟢** | Mark photo as a keeper with vibrant green badge |
| **`X`** | **Reject 🔴** | Mark photo as a reject for deletion |
| **`U`** | **Unflag ⚪** | Clear pick / reject status to neutral |
| **`1` – `5`** | **Star Rating ⭐** | Rates image `★` to `★★★★★` (vibrant gold) |
| **`0`** | **Clear Rating ✖️** | Clears star rating |
| **`6` – `9`** | **Color Labels 🎨** | `6`: Red 🟥 &nbsp; `7`: Yellow 🟨 &nbsp; `8`: Green 🟩 &nbsp; `9`: Blue 🟦 |
| **`C`** | **Auto-Advance ⚡** | **Speed Culling**: Any tag/rating automatically steps to the next photo |

### 🔍 Review, Focus & Comparison
| Key | Action | Description |
| :---: | :--- | :--- |
| **`Z`** / **Click** | **100% Pixel Peep 🔍** | Zoom to 1:1 sensor pixels at cursor; drag to pan |
| **`S`** / **`Tab`** | **2-Up Compare 👥** | Side-by-side comparison with synchronous 1:1 zoom & pan |
| **`\`** | **Cycle Filters 🔀** | Cycles: `All` ➔ `Hide Rejects` ➔ `Picks Only` ➔ `Rejects Only` ➔ `Unrated` ➔ `3+ Stars` ➔ `5 Stars` |

### 📊 Exposure & Information Overlays
| Key | Action | Description |
| :---: | :--- | :--- |
| **`H`** | **RGB Histogram 📊** | Live 256-bin RGB & Rec. 709 Luminance histogram HUD |
| **`O`** | **Exposure Clipping 🚨** | Pulsing highlight (Red) and shadow (Blue) "blinkies" |
| **`I`** | **EXIF Info HUD 📷** | Frosted-glass camera, lens, and exposure parameters overlay |
| **`?`** | **Cheatsheet 💡** | Toggle shortcuts cheatsheet |

### 🛠️ Workflow & Output
| Key | Action | Description |
| :---: | :--- | :--- |
| **`R`** or **`]`** | **Rotate CW ↷** | Lossless 90° clockwise rotation |
| **`L`** or **`[`** | **Rotate CCW ↶** | Lossless 90° counter-clockwise rotation |
| **`E`** | **Quick Export 📦** | Copies all Picks (or current photo) to `./Picks` folder |
| **`Return`** | **Slideshow 📽️** | Start/stop automated hands-free slideshow |
| **`Delete`** / **`Backspace`** | **Trash 🗑️** | Move active file (and its RAW / XMP pair) to macOS Trash |
| **`Shift + Delete`** | **Purge Rejects 💥** | Safely batch moves all rejected (`X`) images to macOS Trash |

### 🧭 Navigation
| Key | Action |
| :---: | :--- |
| **`→`**, **`Space`**, **`↓`**, **`N`** | Next image ⏩ |
| **`←`**, **`↑`**, **`Page Up`**, **`K`** | Previous image ⏪ |
| **`Home`** / **`End`** | First / Last image in folder 🏁 |
| **`F`** | Toggle Full Screen ⛶ |
| **`Esc`**, **`Q`** | Close / Exit ✕ |

---

## 🚀 Download & Installation

### Option 1: Direct Download (Recommended) 📦
Download the latest pre-compiled application bundle from the [**Releases Page**](https://github.com/mortalezz/glance/releases/latest):
1. Download **`Glance-v1.0.0-macOS.zip`**.
2. Unzip and drag `Glance.app` into your `/Applications` folder.
3. Right-click any image in Finder ➔ *Open With* ➔ *Glance* (or set Glance as your default image viewer for instant full-screen glory).

### Option 2: Build from Source 🛠️
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

## 🧪 Engineering Test Suite 🔬

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

## 📄 License ⚖️

Glance is open-source software licensed under the [MIT License](LICENSE).
