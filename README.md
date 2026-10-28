<div align="center">

<img src="assets/icon.png" alt="WinGlance Icon" width="160" height="160" />

# 📸 WinGlance for Windows 11 ⚡

### The ultra-fast, native Windows 11 photo viewer & pro culling tool 🚀  
*Instant full-screen folder browsing, zero-latency keyboard navigation, and pro RAW culling.*

[![Release 🎉](https://img.shields.io/github/v/release/garrul4ik/winglance?color=00c7b7&style=flat-square&label=Release%20%F0%9F%8E%89)](https://github.com/garrul4ik/winglance/releases/latest)
[![Platform](https://img.shields.io/badge/Platform-Windows%2011%20%2F%2010-0078D6?style=flat-square&logo=windows&logoColor=white)](https://github.com/garrul4ik/winglance)
[![Framework](https://img.shields.io/badge/.NET-8.0%20%2F%209.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://github.com/garrul4ik/winglance)
[![Port of Glance](https://img.shields.io/badge/Port%20of-mortalezz%2Fglance-blueviolet?style=flat-square&logo=github)](https://github.com/mortalezz/glance)
[![Engine](https://img.shields.io/badge/Zero%20Electron-Pure%20Native-blueviolet?style=flat-square&label=%E2%9A%A1%20Engine)](https://github.com/garrul4ik/winglance)
[![Speed](https://img.shields.io/badge/Lookahead-Zero--Latency-brightgreen?style=flat-square&label=%F0%9F%9A%80%20Speed)](https://github.com/garrul4ik/winglance)
[![Metadata](https://img.shields.io/badge/Adobe%20XMP-Lightroom%20%26%20C1-orange?style=flat-square&label=%F0%9F%8F%B7%EF%B8%8F%20XMP)](https://github.com/garrul4ik/winglance)
[![License](https://img.shields.io/badge/License-MIT-blue?style=flat-square&label=%F0%9F%93%84%20License)](LICENSE)

</div>

---

> [!NOTE]
> ### 🕊️ Standalone Windows Port of Glance for macOS
> **WinGlance** is an open-source Windows 11 standalone port of [**Glance**](https://github.com/mortalezz/glance), originally designed and created for macOS by [**@mortalezz**](https://github.com/mortalezz).
>
> WinGlance faithfully preserves the founding premise of Glance:
> 1. 🖼️ **Instant Full-Screen Launch**: Open any photo directly from Explorer straight into borderless full screen.
> 2. ⚡ **Zero-Latency Traversal**: Fly through thousands of 45MP+ RAW and high-resolution JPEG shots using arrow keys or `Space` — zero lag, zero spinners, zero waiting.
> 3. 🫧 **Windows 11 Native Aesthetic**: Frosted-glass floating HUDs that appear only when needed and vanish automatically.
> 4. 🏷️ **Pro Culling Superpowers**: 1:1 pixel peeping, 2-up burst compare, Adobe XMP sidecar sync, real-time RGB histograms, and exposure clipping blinkies.

---

## ⚡ Key Features

| Feature | Highlight |
| :--- | :--- |
| 🚀 **Zero-Latency Keyboard Navigation** | Speculative triple-buffering and asynchronous background lookahead decode make folder traversal instantaneous on 45MP+ RAW and high-resolution JPEG files. |
| 🔍 **100% 1:1 Pixel Peep (`Z` / Click)** | Instant zoom to actual device sensor pixels centered under your mouse cursor, with smooth mouse panning. |
| 👥 **2-Up Burst Compare (`S` / `Tab`)** | Side-by-side split comparison to catch micro-blur, closed eyes, and focus differences between consecutive frames, complete with synchronous panning. |
| 🏷️ **Adobe XMP Sidecar Synchronization** | Real-time, non-destructive rating (`★`), color tagging, and flag management fully compatible with **Adobe Lightroom Classic, Capture One, Darktable, and Photo Mechanic**. |
| 🗂️ **RAW + JPEG Sibling Stacking** | Automatically pairs camera RAWs (`.ARW`, `.CR2`, `.CR3`, `.NEF`, `.RAF`, `.DNG`, etc.) with companion JPEGs, browsing the fast preview and inspecting master RAW during zoom. |
| 📊 **Hardware RGB & Luminance Histogram (`H`)** | 256-bin multi-channel histogram rendered with outlier suppression, binomial smoothing, and shadow/highlight clipping indicators. |
| 🚨 **Exposure Clipping Warnings / "Blinkies" (`O`)** | Pulsating zebra overlays for overexposed blown highlights (Red) and underexposed crushed shadows (Blue). |
| 📷 **Frosted EXIF HUD (`I`)** | Displays lens model, camera body, shutter speed, aperture, ISO, and focal length in a clean floating overlay. |
| 🔄 **Lossless EXIF Rotation (`R` / `]` and `L` / `[` )** | Rotates JPEGs, HEICs, and RAW XMP sidecars in 90° increments without re-compressing pixels. |
| 📦 **One-Key Quick Export (`E`)** | Copies all flagged Picks (or current photo) and companion sidecars to `./Picks`. |
| 📽️ **Hands-Free Slideshow (`Return`)** | Full-screen automated slideshow with live `+` / `-` speed stepping (1s to 30s) and instant pause on any interaction. |
| 🪶 **Zero Bloat, Zero Electron** | Pure native .NET desktop application, compiling down to a single standalone binary with zero external DLLs. |

---

## ⌨️ Complete Keyboard Shortcuts Cheatsheet

Press **`?`** at any time inside WinGlance to toggle the interactive shortcuts cheatsheet.

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
| **`\`** | **Cycle Filters 🔀** | Cycles: `All` ➔ `Hide Rejects` ➔ `Picks Only` ➔ `Rejects Only` ➔ `Unrated` ➔ `3+ Stars` ➔ `5 Stars` |

### 🔍 Review, Focus & Comparison
| Key | Action | Description |
| :---: | :--- | :--- |
| **`Z`** / **Click** | **100% Pixel Peep 🔍** | Zoom to 1:1 sensor pixels at cursor; drag to pan |
| **`S`** / **`Tab`** | **2-Up Compare 👥** | Side-by-side comparison with synchronous zoom & pan |

### 📊 Exposure & Information Overlays
| Key | Action | Description |
| :---: | :--- | :--- |
| **`H`** | **RGB Histogram 📊** | Live 256-bin RGB & Rec. 709 Luminance histogram HUD |
| **`O`** | **Exposure Clipping 🚨** | Pulsing highlight (Red) and shadow (Blue) "blinkies" |
| **`I`** | **EXIF Info HUD 📷** | Camera, lens, and exposure parameters overlay |
| **`?`** | **Cheatsheet 💡** | Toggle shortcuts cheatsheet |

### 🛠️ Workflow & Output
| Key | Action | Description |
| :---: | :--- | :--- |
| **`R`** or **`]`** | **Rotate CW ↷** | Lossless 90° clockwise rotation |
| **`L`** or **`[`** | **Rotate CCW ↶** | Lossless 90° counter-clockwise rotation |
| **`E`** | **Quick Export 📦** | Copies all Picks to `./Picks` folder |
| **`Return`** | **Slideshow 📽️** | Start/stop automated hands-free slideshow |
| **`+`** / **`-`** | **Slideshow Speed ⏱️** | Increase / decrease slideshow interval (1s – 30s) |
| **`Delete`** / **`Backspace`** | **Trash 🗑️** | Move active file (and its RAW / XMP pair) to Windows Recycle Bin |

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

### Option 1: Direct Download (Portable Standalone) 📦
Download the pre-compiled standalone binary from the [**Releases Page**](https://github.com/garrul4ik/winglance/releases/latest):
1. Download **`WinGlance-v1.0.0-Windows-x64.zip`** (or `WinGlance.exe`).
2. Unzip and run `WinGlance.exe` anywhere — completely portable, zero installer, zero dependencies.
3. Drag any image or photo folder onto `WinGlance.exe` or set it as default in Windows Explorer!

### Option 2: Build from Source 🛠️
```powershell
# Clone the repository
git clone https://github.com/garrul4ik/winglance.git
cd winglance

# Build & run tests
dotnet build
dotnet test

# Publish single-file standalone executable
dotnet publish src/WinGlance/WinGlance.csproj -c Release -o publish/win-x64
```

---

## 🤝 Credits & Acknowledgements

- **Original Project & Concept**: [mortalezz/glance](https://github.com/mortalezz/glance) by [**@mortalezz**](https://github.com/mortalezz). All UI design patterns, keyboard shortcuts, and Phoenix Slides revival philosophies originate from Glance for macOS.
- **Windows Standalone Port**: Developed for Windows 11 by [**@garrul4ik**](https://github.com/garrul4ik).

---

## 📄 License ⚖️

WinGlance is open-source software licensed under the [MIT License](LICENSE).
