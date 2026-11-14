using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ollameter.Core;

/// <summary>
/// Follows a log file by polling, which handles Ollama renaming <c>server.log</c> to <c>server-1.log</c> when it
/// restarts.
/// </summary>
/// <remarks>
/// The file is opened afresh on every read and always with delete sharing, so this never stops Ollama from
/// rotating its log. A new file is told from the old one by its file ID, since Windows hands a file created
/// under a recently used name the old one's creation time.
/// </remarks>
public sealed class LogTailer(string path)
{
    private const int ChunkSize = 1 << 18;
    private const int LongestLine = 1 << 20;

    private (ulong Volume, ulong Index)? identity;
    private long offset;
    private byte[] partialLine = [];

    /// <summary>Positions at the end of the file and returns up to <paramref name="backlogBytes"/> of complete lines before it.</summary>
    public List<string> OpenAtEnd(int backlogBytes)
    {
        using var stream = Open();
        if (stream is null) return [];
        identity = Identity(stream.SafeFileHandle);
        partialLine = [];
        offset = Math.Max(stream.Length - backlogBytes, 0);
        var startedMidFile = offset > 0;
        var lines = Read(stream);
        if (startedMidFile && lines.Count > 0) lines.RemoveAt(0);
        return lines;
    }

    /// <summary>Returns complete lines appended since the last call. An unfinished last line is held back.</summary>
    public List<string> ReadNew()
    {
        using var stream = Open();
        if (stream is null) return [];
        var current = Identity(stream.SafeFileHandle);
        if (identity is null || current != identity || stream.Length < offset)
        {
            identity = current;
            offset = 0;
            partialLine = [];
        }
        return Read(stream);
    }

    private FileStream? Open()
    {
        try
        {
            return new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private List<string> Read(FileStream stream)
    {
        var bytes = new MemoryStream();
        var buffer = new byte[ChunkSize];
        stream.Position = offset;
        while (true)
        {
            int count;
            try { count = stream.Read(buffer, 0, buffer.Length); }
            catch (IOException) { break; }
            if (count <= 0) break;
            bytes.Write(buffer, 0, count);
            offset += count;
            if (count < buffer.Length) break;
        }
        if (bytes.Length == 0) return [];

        var data = new byte[partialLine.Length + bytes.Length];
        partialLine.CopyTo(data, 0);
        bytes.GetBuffer().AsSpan(0, (int)bytes.Length).CopyTo(data.AsSpan(partialLine.Length));

        var lines = new List<string>();
        var start = 0;
        int newline;
        while ((newline = Array.IndexOf(data, (byte)'\n', start)) >= 0)
        {
            var end = newline > start && data[newline - 1] == '\r' ? newline - 1 : newline;
            lines.Add(Encoding.UTF8.GetString(data, start, end - start));
            start = newline + 1;
        }
        partialLine = data[start..];
        if (partialLine.Length > LongestLine) partialLine = [];
        return lines;
    }

    private static (ulong, ulong)? Identity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) return (0, 0);
        return GetFileInformationByHandle(handle, out var info)
            ? (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow)
            : null;
    }

    // FILETIME is two DWORDs with 4-byte alignment, so each one is spelled out rather than declared as a long.
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
