using System;
using System.Collections.Generic;
using System.IO;

namespace WinGlance.Model;

public sealed record SlideItem
{
    public string Id { get; }
    public string Path { get; }
    public string? RawPath { get; }
    public string? CompanionPath { get; }
    public IReadOnlyList<string> AllPaths { get; }
    public string Filename { get; }
    public string DisplayName { get; }
    public long FileSize { get; }
    public long TotalStackSize { get; }
    public bool IsStacked { get; }

    public SlideItem(
        string path,
        string? rawPath = null,
        string? companionPath = null,
        IReadOnlyList<string>? allPaths = null,
        long fileSize = 0,
        long? totalStackSize = null)
    {
        Path = path;
        RawPath = rawPath;
        CompanionPath = companionPath;

        if (allPaths != null && allPaths.Count > 0)
        {
            AllPaths = allPaths;
        }
        else
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(rawPath)) list.Add(rawPath);
            if (!string.IsNullOrEmpty(companionPath) && !string.Equals(companionPath, rawPath, StringComparison.OrdinalIgnoreCase))
                list.Add(companionPath);
            if (list.Count == 0) list.Add(path);
            AllPaths = list;
        }

        IsStacked = !string.IsNullOrEmpty(rawPath) &&
                    !string.IsNullOrEmpty(companionPath) &&
                    !string.Equals(rawPath, companionPath, StringComparison.OrdinalIgnoreCase);

        var primary = rawPath ?? path;
        Id = primary;
        Filename = System.IO.Path.GetFileName(primary);

        if (IsStacked && !string.IsNullOrEmpty(rawPath) && !string.IsNullOrEmpty(companionPath))
        {
            var compExt = System.IO.Path.GetExtension(companionPath).TrimStart('.').ToUpperInvariant();
            DisplayName = $"{System.IO.Path.GetFileName(rawPath)} + {compExt}";
        }
        else
        {
            DisplayName = Filename;
        }

        FileSize = fileSize;
        TotalStackSize = totalStackSize ?? fileSize;
    }
}
