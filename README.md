<div align="center">

<img src="assets/icon.png" alt="WinGlance Icon" width="160" height="160" />

# 📸 WinGlance for Windows 11 ⚡

### The ultra-fast, native Windows 11 photo viewer & pro culling tool 🚀  
*Instant full-screen folder browsing, zero-latency keyboard navigation, and pro RAW culling.*

[![Platform](https://img.shields.io/badge/Platform-Windows%2011%20%2F%2010-0078D6?style=flat-square&logo=windows&logoColor=white)](https://github.com/garrul4ik/winglance)
[![Framework](https://img.shields.io/badge/.NET-8.0%20%2F%209.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](https://github.com/garrul4ik/winglance)
[![Engine](https://img.shields.io/badge/Zero%20Electron-Pure%20Native-blueviolet?style=flat-square&label=%E2%9A%A1%20Engine)](https://github.com/garrul4ik/winglance)
[![Speed](https://img.shields.io/badge/Lookahead-Zero--Latency-brightgreen?style=flat-square&label=%F0%9F%9A%80%20Speed)](https://github.com/garrul4ik/winglance)
[![Metadata](https://img.shields.io/badge/Adobe%20XMP-Lightroom%20%26%20C1-orange?style=flat-square&label=%F0%9F%8F%B7%EF%B8%8F%20XMP)](https://github.com/garrul4ik/winglance)
[![License](https://img.shields.io/badge/License-MIT-blue?style=flat-square&label=%F0%9F%93%84%20License)](LICENSE)

</div>

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
| 🪶 **Zero Bloat, Zero Electron** | Pure native .NET desktop application, compiling down to a lightweight standalone binary without web runtimes. |

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

## 🛠️ Building & Running

### Requirements
- Windows 10 / 11 (x64)
- .NET 8.0 SDK or later

### Build & Run
```powershell
# Build solution
dotnet build

# Run unit test suite
dotnet test

# Run application
dotnet run --project src/WinGlance

# Publish standalone single-file binary
dotnet publish src/WinGlance/WinGlance.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish/win-x64
```

---

## 📄 License ⚖️

WinGlance is open-source software licensed under the [MIT License](LICENSE).
