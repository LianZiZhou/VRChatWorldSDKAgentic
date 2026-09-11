# Lists every visible top-level window owned by a process, as JSON.
#
# Used to name the modal dialog that is blocking Unity. Unity's own dialogs ("detected external changes,
# reload?", "Verify Saving Assets", API-update prompts) are separate top-level windows owned by the editor
# process, so this is enough to identify what is in the way. Get-Process only exposes MainWindowTitle,
# which is exactly the window that is *not* the dialog.
param(
    [Parameter(Mandatory = $true)][int]$ProcessId
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class AgenticWindowProbe
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    private const uint GW_OWNER = 4;

    public sealed class WindowInfo
    {
        public long Handle;
        public string Title;
        public string ClassName;
        public bool Visible;
        public bool Enabled;
        public bool HasOwner;
    }

    public static List<WindowInfo> ListFor(uint targetProcessId)
    {
        var found = new List<WindowInfo>();

        EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
        {
            uint owningProcess;
            GetWindowThreadProcessId(hWnd, out owningProcess);
            if (owningProcess != targetProcessId) return true;
            if (!IsWindowVisible(hWnd)) return true;

            var title = new StringBuilder(512);
            GetWindowTextW(hWnd, title, title.Capacity);

            var className = new StringBuilder(256);
            GetClassNameW(hWnd, className, className.Capacity);

            found.Add(new WindowInfo
            {
                Handle = hWnd.ToInt64(),
                Title = title.ToString(),
                ClassName = className.ToString(),
                Visible = true,
                Enabled = IsWindowEnabled(hWnd),
                // A window with an owner is a dialog or tool window rather than the main window.
                HasOwner = GetWindow(hWnd, GW_OWNER) != IntPtr.Zero
            });
            return true;
        }, IntPtr.Zero);

        return found;
    }
}
'@

$windows = [AgenticWindowProbe]::ListFor([uint32]$ProcessId)
ConvertTo-Json -InputObject @($windows) -Depth 3 -Compress
