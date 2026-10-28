using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using WinGlance.Model;

namespace WinGlance.Engine;

public sealed record ScanResult(
    IReadOnlyList<SlideItem> Items,
    int InitialIndex,
    string DirectoryPath
);

public static class DirectoryScanner
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string x, string y);

    public sealed class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();
        public int Compare(string? x, string? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            return StrCmpLogicalW(x, y);
        }
    }

    private record FileEntry(string Path, long Size, bool IsRaw);

    public static async Task<ScanResult> ScanAsync(string targetPath)
    {
        return await Task.Run(() =>
        {
            string directoryPath;
            string? selectedFilename = null;

            if (Directory.Exists(targetPath))
            {
                directoryPath = Path.GetFullPath(targetPath);
            }
            else if (File.Exists(targetPath))
            {
                directoryPath = Path.GetDirectoryName(Path.GetFullPath(targetPath)) ?? "";
                selectedFilename = Path.GetFileName(targetPath);
            }
            else
            {
                throw new FileNotFoundException($"Path not found: {targetPath}");
            }

            var dirInfo = new DirectoryInfo(directoryPath);
            var files = dirInfo.EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => (f.Attributes & FileAttributes.Hidden) == 0 && SupportedFormats.IsSupported(f.FullName))
                .ToList();

            var groups = new Dictionary<string, List<FileEntry>>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                var isRaw = SupportedFormats.IsRaw(file.FullName);
                var stem = Path.GetFileNameWithoutExtension(file.FullName).ToLowerInvariant();

                var entry = new FileEntry(file.FullName, file.Length, isRaw);
                if (!groups.TryGetValue(stem, out var list))
                {
                    list = new List<FileEntry>();
                    groups[stem] = list;
                }
                list.Add(entry);
            }

            var items = new List<SlideItem>();

            foreach (var (_, entries) in groups)
            {
                var rawEntries = entries.Where(e => e.IsRaw).ToList();
                var standardEntries = entries.Where(e => !e.IsRaw).ToList();
                var allPaths = entries.Select(e => e.Path).ToList();
                var totalSize = entries.Sum(e => e.Size);

                if (rawEntries.Count > 0 && standardEntries.Count > 0)
                {
                    // Stacking: Use fast companion JPEG for screen-bounded preview, link master RAW
                    var raw = rawEntries[0];
                    var companion = standardEntries[0];

                    items.Add(new SlideItem(
                        path: companion.Path,
                        rawPath: raw.Path,
                        companionPath: companion.Path,
                        allPaths: allPaths,
                        fileSize: companion.Size,
                        totalStackSize: totalSize
                    ));
                }
                else if (rawEntries.Count > 0)
                {
                    var raw = rawEntries[0];
                    items.Add(new SlideItem(
                        path: raw.Path,
                        rawPath: raw.Path,
                        allPaths: allPaths,
                        fileSize: raw.Size,
                        totalStackSize: totalSize
                    ));
                }
                else if (standardEntries.Count > 0)
                {
                    var std = standardEntries[0];
                    items.Add(new SlideItem(
                        path: std.Path,
                        allPaths: allPaths,
                        fileSize: std.Size,
                        totalStackSize: totalSize
                    ));
                }
            }

            // Natural numerical sort: DSC_0001 before DSC_0002 before DSC_0010
            items.Sort((a, b) => NaturalStringComparer.Instance.Compare(a.Filename, b.Filename));

            var initialIndex = 0;
            if (!string.IsNullOrEmpty(selectedFilename))
            {
                var match = items.FindIndex(it =>
                    string.Equals(it.Filename, selectedFilename, StringComparison.OrdinalIgnoreCase) ||
                    (it.IsStacked && string.Equals(Path.GetFileName(it.RawPath), selectedFilename, StringComparison.OrdinalIgnoreCase)) ||
                    (it.IsStacked && string.Equals(Path.GetFileName(it.CompanionPath), selectedFilename, StringComparison.OrdinalIgnoreCase))
                );
                if (match >= 0) initialIndex = match;
            }

            return new ScanResult(items, initialIndex, directoryPath);
        });
    }
}
