using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using WinGlance.Model;

namespace WinGlance.Engine;

public sealed record ExportResult(int ItemCount, int FilesWritten, string DestinationDirectory);

public static class ExportManager
{
    public const string SelectsFolderName = "Selects";

    public static Task<ExportResult> ExportSingleAsync(
        SlideItem item,
        string currentDirectory,
        Action<string>? onBeforeWrite = null)
    {
        return Task.Run(() =>
        {
            var targetDir = Path.Combine(currentDirectory, SelectsFolderName);
            Directory.CreateDirectory(targetDir);
            int written = CopyItemAndSidecars(item, targetDir, onBeforeWrite);
            return new ExportResult(written > 0 ? 1 : 0, written, targetDir);
        });
    }

    public static Task<ExportResult> ExportPicksAsync(
        IReadOnlyList<SlideItem> items,
        IReadOnlyDictionary<string, CullingMetadata> cullingData,
        string currentDirectory,
        Action<string>? onBeforeWrite = null)
    {
        return Task.Run(() =>
        {
            var targetDir = Path.Combine(currentDirectory, SelectsFolderName);
            var picks = new List<SlideItem>();
            foreach (var item in items)
            {
                if (cullingData.TryGetValue(item.Id, out var meta) && meta.Pick == PickStatus.Pick)
                {
                    picks.Add(item);
                }
            }

            if (picks.Count == 0)
            {
                return new ExportResult(0, 0, targetDir);
            }

            Directory.CreateDirectory(targetDir);

            int totalFilesWritten = 0;
            foreach (var item in picks)
            {
                totalFilesWritten += CopyItemAndSidecars(item, targetDir, onBeforeWrite);
            }

            return new ExportResult(picks.Count, totalFilesWritten, targetDir);
        });
    }

    public static void RevealInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            }
        }
        catch { }
    }

    public static bool OpenInExternalEditor(SlideItem item)
    {
        string target = item.RawPath ?? item.Path;
        if (!File.Exists(target)) return false;

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1155) // ERROR_NO_ASSOCIATION
        {
            try
            {
                Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL \"{target}\"")
                {
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static int CopyItemAndSidecars(SlideItem item, string targetDir, Action<string>? onBeforeWrite)
    {
        int filesWritten = 0;

        foreach (var path in item.AllPaths)
        {
            if (File.Exists(path))
            {
                var dest = Path.Combine(targetDir, Path.GetFileName(path));
                onBeforeWrite?.Invoke(dest);
                File.Copy(path, dest, overwrite: true);
                filesWritten++;
            }
        }

        foreach (var sidecar in XmpSidecarManager.GetAllExistingSidecarPaths(item))
        {
            var destSidecar = Path.Combine(targetDir, Path.GetFileName(sidecar));
            onBeforeWrite?.Invoke(destSidecar);
            File.Copy(sidecar, destSidecar, overwrite: true);
            filesWritten++;
        }

        return filesWritten;
    }
}
