# Answers a modal dialog by focusing it and pressing a key (Enter by default, i.e. the default button).
#
# Deliberately blunt, and deliberately gated: the caller must have matched the dialog's title against an
# allowlist first. Pressing the default button of an unknown dialog is a real way to agree to something
# nobody intended, so this is never invoked speculatively.
param(
    [Parameter(Mandatory = $true)][long]$Handle,
    [string]$Key = '{ENTER}'
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class AgenticWindowFocus
{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int command);

    private const int SW_RESTORE = 9;

    public static bool Focus(long handle)
    {
        var hWnd = new IntPtr(handle);
        if (!IsWindow(hWnd)) return false;
        ShowWindow(hWnd, SW_RESTORE);
        return SetForegroundWindow(hWnd);
    }
}
'@

$focused = [AgenticWindowFocus]::Focus($Handle)
if (-not $focused) {
    ConvertTo-Json -InputObject @{ focused = $false; sent = $false; reason = 'window is gone or refused focus' } -Compress
    exit 0
}

Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait($Key)
Start-Sleep -Milliseconds 250

$stillThere = [AgenticWindowFocus]::IsWindow([IntPtr]::new($Handle))
ConvertTo-Json -InputObject @{ focused = $true; sent = $true; key = $Key; windowStillExists = $stillThere } -Compress
