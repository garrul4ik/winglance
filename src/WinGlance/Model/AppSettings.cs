using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinGlance.Gpu;

namespace WinGlance.Model;

public enum ThemePreference
{
    System,
    Dark,
    Light
}

public sealed class AppSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public bool AutoAdvance { get; set; } = true;
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppSettingsJsonContext : JsonSerializerContext
{
}

public static class SettingsStore
{
    private static readonly IntPtr HKEY_CURRENT_USER = new(unchecked((int)0x80000001));
    private const uint RRF_RT_REG_DWORD = 0x00000010;

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int RegGetValueW(
        IntPtr hkey,
        string? lpSubKey,
        string? lpValue,
        uint dwFlags,
        out uint pdwType,
        out uint pvData,
        ref uint pcbData);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinGlance",
        "settings.json"
    );

    public static AppSettings Load(string? path = null)
    {
        var target = path ?? DefaultPath;
        try
        {
            if (!File.Exists(target)) return new AppSettings();
            string json = File.ReadAllText(target);
            return JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings, string? path = null)
    {
        var target = path ?? DefaultPath;
        try
        {
            var dir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
            var tempPath = $"{target}.tmp.{Guid.NewGuid():N}";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, target, overwrite: true);
        }
        catch
        {
            // Ignore IO/permission errors for non-critical settings persistence
        }
    }

    public static ThemeMode DetectWindowsSystemTheme()
    {
        try
        {
            uint size = sizeof(uint);
            int res = RegGetValueW(
                HKEY_CURRENT_USER,
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                RRF_RT_REG_DWORD,
                out _,
                out uint lightTheme,
                ref size);

            if (res == 0)
            {
                return lightTheme == 1 ? ThemeMode.Light : ThemeMode.Dark;
            }
        }
        catch { }

        return ThemeMode.Dark;
    }

    public static ThemeMode ResolveTheme(ThemePreference preference) => preference switch
    {
        ThemePreference.Dark => ThemeMode.Dark,
        ThemePreference.Light => ThemeMode.Light,
        _ => DetectWindowsSystemTheme()
    };
}
