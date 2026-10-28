using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WinGlance.Model;

namespace WinGlance.Engine;

public static class ExportManager
{
    public static async Task<int> ExportPicksAsync(
        IReadOnlyList<SlideItem> items,
        IReadOnlyDictionary<string, CullingMetadata> cullingData,
        string currentDirectory)
    {
        return await Task.Run(() =>
        {
            var picks = new List<SlideItem>();
            foreach (var item in items)
            {
                if (cullingData.TryGetValue(item.Id, out var meta) && meta.Pick == PickStatus.Pick)
                {
                    picks.Add(item);
                }
            }

            if (picks.Count == 0) return 0;

            var targetDir = Path.Combine(currentDirectory, "Picks");
            Directory.CreateDirectory(targetDir);

            int copiedCount = 0;
            foreach (var item in picks)
            {
                foreach (var path in item.AllPaths)
                {
                    if (File.Exists(path))
                    {
                        var dest = Path.Combine(targetDir, Path.GetFileName(path));
                        File.Copy(path, dest, overwrite: true);
                        copiedCount++;
                    }

                    // Also copy accompanying XMP sidecar if present
                    var sidecar = XmpSidecarManager.ExistingSidecarPath(path);
                    if (sidecar != null && File.Exists(sidecar))
                    {
                        var destSidecar = Path.Combine(targetDir, Path.GetFileName(sidecar));
                        File.Copy(sidecar, destSidecar, overwrite: true);
                    }
                }
            }

            return copiedCount;
        });
    }
}
