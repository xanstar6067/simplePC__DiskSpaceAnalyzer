using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using DiskSpaceAnalyzer.Models;
using Microsoft.Win32.SafeHandles;

namespace DiskSpaceAnalyzer.Services;

public static class SystemInterop
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint InvalidFileSize = 0xFFFFFFFF;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int StandardQuery = 0;
    private const int BusTypeNvme = 17;

    public static long GetSizeOnDisk(string path, long logicalSize)
    {
        try
        {
            var low = GetCompressedFileSizeW(path, out var high);
            if (low == InvalidFileSize)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    return logicalSize;
                }
            }

            return ((long)high << 32) + low;
        }
        catch
        {
            return logicalSize;
        }
    }

    public static string GetFileId(string path)
    {
        try
        {
            using var handle = CreateFileW(
                path,
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                return string.Empty;
            }

            if (!GetFileInformationByHandle(handle, out var info))
            {
                return string.Empty;
            }

            var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
            return $"{info.VolumeSerialNumber:X8}:{index:X16}";
        }
        catch (Win32Exception)
        {
            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool TryDetectStorageKind(string rootPath, out StorageKind storageKind)
    {
        storageKind = StorageKind.Unknown;

        var root = Path.GetPathRoot(rootPath);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
        {
            return false;
        }

        var volumePath = $@"\\.\{char.ToUpperInvariant(root[0])}:";
        try
        {
            using var handle = CreateFileW(
                volumePath,
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                return false;
            }

            if (TryGetStorageBusType(handle, out var busType) && busType == BusTypeNvme)
            {
                storageKind = StorageKind.NvmeSsd;
                return true;
            }

            if (TryGetSeekPenalty(handle, out var incursSeekPenalty))
            {
                storageKind = incursSeekPenalty ? StorageKind.Hdd : StorageKind.Ssd;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryGetStorageBusType(SafeFileHandle handle, out int busType)
    {
        busType = 0;
        var query = new StoragePropertyQuery
        {
            PropertyId = StorageDeviceProperty,
            QueryType = StandardQuery
        };
        var buffer = new byte[1024];

        if (!DeviceIoControl(
                handle,
                IoctlStorageQueryProperty,
                ref query,
                Marshal.SizeOf<StoragePropertyQuery>(),
                buffer,
                buffer.Length,
                out _,
                IntPtr.Zero))
        {
            return false;
        }

        if (buffer.Length < 29)
        {
            return false;
        }

        busType = buffer[28];
        return true;
    }

    private static bool TryGetSeekPenalty(SafeFileHandle handle, out bool incursSeekPenalty)
    {
        incursSeekPenalty = false;
        var query = new StoragePropertyQuery
        {
            PropertyId = StorageDeviceSeekPenaltyProperty,
            QueryType = StandardQuery
        };

        if (!DeviceIoControl(
                handle,
                IoctlStorageQueryProperty,
                ref query,
                Marshal.SizeOf<StoragePropertyQuery>(),
                out DeviceSeekPenaltyDescriptor descriptor,
                Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(),
                out _,
                IntPtr.Zero))
        {
            return false;
        }

        incursSeekPenalty = descriptor.IncursSeekPenalty;
        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer,
        int nInBufferSize,
        byte[] lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer,
        int nInBufferSize,
        out DeviceSeekPenaltyDescriptor lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;

        [MarshalAs(UnmanagedType.U1)]
        public bool IncursSeekPenalty;
    }
}
