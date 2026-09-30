# Holds a serial port open (exclusive) so MultiFunPlayer sees it as "busy".
# Uses CreateFile only: it does NOT touch DTR/RTS, so the device will NOT move.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\hold-serial-port.ps1
#   powershell -ExecutionPolicy Bypass -File .\hold-serial-port.ps1 -Port COM12 -Seconds 120

param(
    [string]$Port = 'COM12',
    [int]$Seconds = 90
)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class SerialPortHolder
{
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
                                            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static IntPtr Open(string port)
        => CreateFile(@"\\.\" + port, 0x80000000 | 0x40000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);

    public static void Close(IntPtr handle)
    {
        if (handle != new IntPtr(-1))
            CloseHandle(handle);
    }
}
'@

$handle = [SerialPortHolder]::Open($Port)
if ($handle -eq [IntPtr]::new(-1)) {
    $error = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    $message = (New-Object ComponentModel.Win32Exception($error)).Message
    Write-Host "Could not hold $Port : $message" -ForegroundColor Red
    Write-Host "It is probably already held by another program." -ForegroundColor Yellow
    exit 1
}

Write-Host "$Port is now held exclusively for $Seconds seconds." -ForegroundColor Yellow
Write-Host "Start MultiFunPlayer now; it should report the port as busy." -ForegroundColor Yellow
Write-Host "Press Ctrl+C to release early." -ForegroundColor DarkGray

try {
    for ($remaining = $Seconds; $remaining -gt 0; $remaining--) {
        Write-Host "`rremaining: $remaining s   " -NoNewline
        Start-Sleep -Seconds 1
    }
}
finally {
    [SerialPortHolder]::Close($handle)
    Write-Host "`n$Port released." -ForegroundColor Green
}
