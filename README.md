# Glance for macOS (v1.0.0)

A blazingly fast, native macOS photo viewer and professional culling tool for photographers and photo editors. Designed for zero-latency folder navigation, 100% pixel peeping, dual-image burst comparison, Adobe Lightroom/Capture One XMP sidecar synchronization, and real-time tonal analysis.

---

## ⚡ Key Highlights

- **Zero-Latency Keyboard Browsing**: Speculative triple-buffering and background lookahead decode make folder navigation instantaneous even on 45MP+ RAW and high-resolution JPEG files.
- **100% 1:1 Pixel Peep (`Z` / Click to Zoom)**: Instant zoom to actual device pixels centered around the mouse cursor with smooth trackpad and mouse panning.
- **2-Up Burst Compare (`S` / `Tab`)**: Side-by-side split screen comparison to easily detect micro-blur, closed eyes, and critical focus differences between consecutive frames, with synchronous 1:1 panning.
- **Industry-Standard XMP Sidecar Sync**: Real-time non-destructive rating (`★`), color tagging, and flag management fully compatible with Adobe Lightroom Classic, Capture One, Darktable, and Photo Mechanic.
- **RAW + JPEG Sibling Stacking**: Automatically pairs RAW files (`.ARW`, `.CR2`, `.CR3`, `.NEF`, `.RAF`, `.DNG`, etc.) with companion JPEGs, browsing the fast preview and inspecting the master RAW sensor data during zoom.
- **Real-Time RGB & Luminance Histogram (`H`)**: Hardware-accelerated 256-bin RGB and Rec. 709 Luminance curves rendered in a sleek frosted-glass HUD with clipping indicators.
- **Exposure Clipping Warnings / "Blinkies" (`O`)**: Animated pulsating overlays for overexposed blown highlights (Red) and underexposed crushed shadows (Blue).
- **Dedicated EXIF HUD (`I`)**: Frosted glass HUD showing camera, lens, shutter speed, aperture, ISO, focal length, and timestamp.
- **Lossless EXIF Rotation (`R` / `]` and `L` / `[` )**: Rotates JPEGs, HEICs, and RAW XMP sidecars in 90° increments without re-compressing pixels.
- **One-Key Quick Export (`E`)**: Automatically exports flagged Picks and companion sidecars into `./Picks` with audio feedback and Finder reveal.
- **Hands-Free Slideshow (`Return`)**: Full-screen automated slideshow with instant interruption on any user interaction.

---

## ⌨️ Complete Keyboard Shortcuts Cheatsheet

Press **`?`** (`Shift + /`) at any time in Glance to toggle the on-screen frosted-glass cheatsheet.

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

## 🚀 Building & Running

### Prerequisites
- macOS 13.0 (Ventura) or newer
- Xcode 15+ / Swift 5.9+ toolchain

### Build the Application Bundle
```bash
./scripts/build_app.sh
```
This produces `Glance.app` in the repository root, removes quarantine flags, and applies ad-hoc code signing.

### Launch
```bash
# Open a folder directly with Glance:
open Glance.app /path/to/folder

# Or run via Swift CLI:
swift run Glance /path/to/folder
```

---

## 🧪 Testing
Glance includes an end-to-end engineering verification suite:
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
MIT License.
