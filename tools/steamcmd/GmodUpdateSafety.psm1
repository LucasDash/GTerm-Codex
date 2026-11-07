Set-StrictMode -Version Latest

# These checks are deliberately conservative: any dedicated Source server can
# share game data through hardlinks, even when its install root differs.
function Assert-NoDedicatedProcesses {
    try {
        $records = @(Get-CimInstance -ClassName Win32_Process -Filter "Name LIKE 'srcds%.exe' OR Name='gmod.exe'" -ErrorAction Stop)
    } catch {
        throw 'Cannot inspect dedicated-server processes; refusing update.'
    }
    Assert-SafeProcessRecords -Records $records
}

function Assert-SafeProcessRecords {
    param([AllowEmptyCollection()][object[]]$Records)
    foreach ($record in $Records) {
        $name = [string]$record.Name
        if ($name -match '(?i)^srcds.*\.exe$') {
            throw 'A Source dedicated-server process is running; stop all dedicated servers before updating.'
        }
        if ($name -ieq 'gmod.exe') {
            if ([string]::IsNullOrWhiteSpace([string]$record.CommandLine)) {
                throw 'Cannot classify a GMod process; refusing update.'
            }
            if ([string]$record.CommandLine -match '(?i)(?:^|\s)"?-dedicated(?:"|\s|$)') {
                throw 'A GMod dedicated-server process is running; stop all dedicated servers before updating.'
            }
        }
    }
}

function Initialize-FileLinkInspector {
    if ('GTermDedicatedFileLinkSafety' -as [type]) { return }
    Add-Type -ErrorAction Stop -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class GTermDedicatedFileLinkSafety {
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime { public uint Low; public uint High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation {
        public uint Attributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerial;
        public uint SizeHigh;
        public uint SizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);

    public static uint LinkCount(string path) {
        const uint FILE_READ_ATTRIBUTES = 0x80;
        const uint SHARE_READ_WRITE_DELETE = 7;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        using (SafeFileHandle file = CreateFileW(path, FILE_READ_ATTRIBUTES, SHARE_READ_WRITE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero)) {
            if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            FileInformation info;
            if (!GetFileInformationByHandle(file, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & (FILE_ATTRIBUTE_REPARSE_POINT | FILE_ATTRIBUTE_DIRECTORY)) != 0 || info.NumberOfLinks == 0)
                throw new InvalidOperationException("Invalid or non-regular file.");
            return info.NumberOfLinks;
        }
    }
}
'@
}

function Assert-NoLinkedFiles {
    param([Parameter(Mandatory)][string]$ServerRoot)
    if (-not (Test-Path -LiteralPath $ServerRoot -ErrorAction Stop)) { return }
    try {
        Initialize-FileLinkInspector
        $root = Get-Item -LiteralPath $ServerRoot -Force -ErrorAction Stop
        if (-not $root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Invalid root.'
        }
        Get-ChildItem -LiteralPath $ServerRoot -Force -Recurse -ErrorAction Stop | ForEach-Object {
            if (($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Reparse point detected.'
            }
            if (-not $_.PSIsContainer -and [GTermDedicatedFileLinkSafety]::LinkCount($_.FullName) -ne 1) {
                throw 'Hardlinked file detected.'
            }
        }
    } catch {
        throw 'Cannot establish that the server tree contains only independent regular files; refusing update.'
    }
}

function Assert-GmodUpdateSafe {
    param([Parameter(Mandatory)][string]$ServerRoot)
    Assert-NoDedicatedProcesses
    Assert-NoLinkedFiles -ServerRoot $ServerRoot
}

Export-ModuleMember -Function Assert-GmodUpdateSafe
