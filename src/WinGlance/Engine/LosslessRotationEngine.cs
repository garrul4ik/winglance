using System;
using WinGlance.Model;

namespace WinGlance.Engine;

public enum RotationDirection
{
    Clockwise,        // 90° CW ("]" or "R")
    CounterClockwise  // 90° CCW ("[" or "L")
}

public static class LosslessRotationEngine
{
    public static int NextOrientation(int current, RotationDirection direction)
    {
        var curr = (current is >= 1 and <= 8) ? current : 1;
        return direction switch
        {
            RotationDirection.Clockwise => curr switch
            {
                1 => 6,
                6 => 3,
                3 => 8,
                8 => 1,
                2 => 7,
                7 => 4,
                4 => 5,
                5 => 2,
                _ => 6
            },
            RotationDirection.CounterClockwise => curr switch
            {
                1 => 8,
                8 => 3,
                3 => 6,
                6 => 1,
                2 => 5,
                5 => 4,
                4 => 7,
                7 => 2,
                _ => 8
            },
            _ => 1
        };
    }

    public static int Rotate(SlideItem item, RotationDirection direction)
    {
        // 1. Read current orientation from XMP sidecar or EXIF
        var currentMeta = XmpSidecarManager.ReadMetadata(item.Path) ?? new CullingMetadata();
        var currentOrientation = currentMeta.Orientation ?? 1;

        var newOrientation = NextOrientation(currentOrientation, direction);

        // 2. Update XMP sidecars
        var sidecarTargets = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(item.RawPath))
        {
            sidecarTargets.Add(item.RawPath);
        }
        else
        {
            sidecarTargets.Add(item.Path);
        }

        foreach (var target in sidecarTargets)
        {
            var meta = XmpSidecarManager.ReadMetadata(target) ?? new CullingMetadata();
            meta = meta with { Orientation = newOrientation };
            XmpSidecarManager.Save(meta, target);
        }

        return newOrientation;
    }
}
