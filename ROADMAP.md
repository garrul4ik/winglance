# Glance Development Roadmap

This document outlines the strategic engineering roadmap for **Glance** as a pro photographer tool for macOS.

---

## Architecture Principles
1. **Zero-Latency Presentation (0ms UI thread)**: UI thread only paints pixels and reads input; all decoding, I/O, and metadata manipulation run concurrently in background actors.
2. **Memory Bounding**: Downsampled screen-size decoding for fast folder traversal; full-resolution decode on demand for 100% inspection.
3. **Non-Destructive & Reversible**: All batch operations utilize macOS Trash (`FileManager.trashItem`) or standard sidecars without touching master RAW bits.

---

## Phased Milestones

### Phase 1: Critical Sharpness & Inspection (In Progress)
- [x] **100% 1:1 Pixel Peep (`Z` / Click to Zoom)**:
  - Instant zoom to 100% actual device pixels centered around mouse cursor position.
  - Smooth pan & drag with trackpad / mouse.
  - Instant toggle back to fit-to-screen with `Z` or click.
  - Background full-resolution decode pipeline that doesn't block the screen-bounding lookahead cache.

### Phase 2: Professional Ecosystem Interoperability
- [ ] **Industry-Standard XMP Sidecar Sync**:
  - Automatically read & write Adobe XMP sidecar files (`<filename>.xmp`).
  - Full compatibility with Adobe Lightroom Classic, Capture One, and Darktable.
  - Stores Star Ratings (`xmp:Rating`), Color Labels (`xmpG:Label`), and Pick status without touching proprietary RAW files.
- [ ] **RAW + JPEG & Sibling Stacking**:
  - Automatic grouping of paired RAW + JPEG shots (`_DSC001.ARW` + `_DSC001.JPG`) into a single slide.
  - Instant browsing using the embedded preview, with background RAW linking.
  - Synchronized culling tags & trashing so RAWs are never orphaned.

### Phase 3: Exposure & Tone Analysis
- [ ] **Live Frosted-Glass Histogram (`H`)**:
  - Real-time RGB and Luminance histogram overlay in the corner.
  - GPU-accelerated histogram calculation via vImage / Metal Performance Shaders.
- [ ] **Exposure Clipping Warnings ("Blinkies" / `O`)**:
  - Highlights zebra overlay for blown highlights (>99.5% luminance).
  - Shadow clipping indicators for crushed blacks (<0.5% luminance).

### Phase 4: Output & Workflow Productivity
- [ ] **Quick Export / Batch Copy Picks (`E`)**:
  - One-key background copy or symlink of all flagged Picks (`P`) or 4+ stars into a `./Picks` or `./Selected` subfolder.
- [ ] **Lossless EXIF Orientation Rotation (`[` / `]`)**:
  - Lossless rotation of JPEG/HEIC/RAW EXIF tags in 90-degree increments without re-compressing image pixels.
- [ ] **Configurable Hands-Free Slideshow (`Return`)**:
  - Automatic timed slideshow mode with customizable intervals (2s, 3s, 5s).
