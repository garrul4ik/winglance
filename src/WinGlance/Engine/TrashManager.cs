using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace WinGlance.Engine;

public static class TrashManager
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT FileOp);

    public static async Task MoveToTrashAsync(string filePath)
    {
        await Task.Run(() =>
        {
            if (!File.Exists(filePath) && !Directory.Exists(filePath)) return;

            // SHFileOperation expects double-null-terminated string
            var pFrom = filePath + '\0' + '\0';

            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = pFrom,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
            };

            int ret = SHFileOperation(ref op);
            if (ret != 0)
            {
                // Fallback to normal delete if Recycle Bin fails
                File.Delete(filePath);
            }
        });
    }
}
