# Glance for macOS

A high-performance, ultra-responsive macOS image and RAW viewer with instant full-screen folder browsing, zero-latency keyboard navigation, and a pro photographer culling workflow.

---

## Culling Workflow & Keyboard Shortcuts

Glance delivers single-finger muscle memory culling for large photo shoots:

### 1. Rapid Tagging & Rating
| Key | Action | Description |
| :--- | :--- | :--- |
| **`P`** | **Pick / Flag** | Green badge keeper |
| **`X`** | **Reject** | Red badge reject (marked for trash) |
| **`U`** | **Unflag** | Neutral / clear pick status |
| **`1` – `5`** | **Star Rating** | Rates image `★` to `★★★★★` (in vibrant yellow) |
| **`0`** | **Clear Rating** | Clears star rating |
| **`6` – `9`** | **Color Labels** | 6: Red, 7: Yellow, 8: Green, 9: Blue |
| **`C`** | **Auto-Advance** | **Speed Culling**: Any tag/rating automatically steps to the next image instantly! |

### 2. Burst Compare & Review
| Key | Action | Description |
| :--- | :--- | :--- |
| **`S`** or **`Tab`** | **2-Up Burst Compare** | Splits the screen to compare `[Current]` and `[Next]` side-by-side to detect micro-blur/closed eyes |
| **`\`** (Backslash) | **Cycle Filters** | Cycles: `All` ➔ `Hide Rejects` ➔ `Picks Only` ➔ `Rejects Only` ➔ `Unrated` ➔ `3+ Stars` ➔ `5 Stars` |

### 3. Deletion & Batch Purge
| Key | Action | Description |
| :--- | :--- | :--- |
| **`Delete`** / **`Backspace`** | **Trash Current** | Asynchronously moves the active file to macOS Trash |
| **`Shift + Delete`** | **Batch Purge Rejects** | Prompts to move **all rejected (`X`)** images in the shoot to macOS Trash in one background operation |

### 4. Navigation & View
| Key | Action |
| :--- | :--- |
| **Right Arrow**, **Space**, **Down**, **N** | Next image (instant from lookahead buffer) |
| **Left Arrow**, **Up**, **Page Up**, **K** | Previous image |
| **Home** / **End** | First / Last image |
| **F** | Toggle Full Screen |
| **I** | Toggle Frosted Glass HUD visibility (or let it auto-pulse) |
| **Esc**, **Q** | Close / Exit |

---

## Architectural Highlights

- **Triple Buffering / Speculative Lookahead**: Zero-latency transitions between high-resolution frames.
- **Frequency-Domain Mipmapping**: Automatically downscales huge 45MP+ RAWs and JPEGs during decompression directly to screen Retina bounds.
- **Pure Non-Blocking Architecture**: Background threads handle directory scanning, sorting, ImageIO decode, and Trash I/O; the main thread only draws and captures keyboard input.
- **Apple Vibrancy Squircle HUD**: Uses native `NSVisualEffectView` with `maskImage` clipping and depth shadows.
- **Quarantine & Gatekeeper Neutralizer**: Automatically removes corrupt `LaunchServices.OpenWith` and quarantine attributes from camera exports on the fly.

---

## Running Glance

```bash
# Launch as standalone app:
open Glance.app /path/to/folder

# Or run via Swift CLI:
swift run Glance /path/to/folder
```
