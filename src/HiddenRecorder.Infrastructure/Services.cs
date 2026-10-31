using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using HiddenRecorder.Core;

namespace HiddenRecorder.Infrastructure;

public sealed class JsonSettingsStore : ISettingsStore
{
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HiddenRecorder");
    private readonly string path;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public JsonSettingsStore(string? path = null) => this.path = Path.GetFullPath(path ?? Path.Combine(DataDirectory, "settings.json"));
    public async Task<RecorderSettings> LoadAsync()
    {
        if (!File.Exists(path)) return new();
        using var input = File.OpenRead(path);
        var settings = await JsonSerializer.DeserializeAsync<RecorderSettings>(input) ?? throw new InvalidDataException("The settings file is invalid.");
        settings.Validate(false); return settings;
    }
    public async Task SaveAsync(RecorderSettings settings)
    {
        settings.Validate(false); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { await JsonSerializer.SerializeAsync(output, settings, Options); await output.FlushAsync(); }
        File.Move(temporary, path, true);
    }
}
public sealed class StorageGuard : IStorageGuard
{
    public void Check(RecorderSettings settings, bool probeWrite)
    {
        if (probeWrite) Directory.CreateDirectory(settings.OutputDirectory);
        if (!GetDiskFreeSpaceEx(settings.OutputDirectory, out var available, out _, out _)) throw new IOException("Could not check free space in the output folder.");
        if (available < (ulong)settings.ReserveBytes) throw new IOException("Not enough free disk space. Existing recordings have been preserved.");
        if (probeWrite)
        {
            using var probe = new FileStream(Path.Combine(settings.OutputDirectory, ".write-test-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            probe.WriteByte(0);
        }
    }
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
}
public sealed class RollingLog : IAppLog
{
    private readonly object gate = new();
    public void Write(string message, Exception? exception = null)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(JsonSettingsStore.DataDirectory);
                var path = Path.Combine(JsonSettingsStore.DataDirectory, "app.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024)
                {
                    for (var i = 3; i >= 1; i--) if (File.Exists(path + "." + i)) File.Move(path + "." + i, path + "." + (i + 1), true);
                    File.Move(path, path + ".1", true);
                }
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message} {exception}\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
public sealed class PowerGuard : IPowerGuard, IDisposable
{
    private nint request;
    public void SetMonitoring(bool enabled)
    {
        if (enabled && request == 0)
        {
            var reason = new ReasonContext { Version = 0, Flags = 1, Reason = "HiddenRecorder: camera and microphone monitoring" };
            request = PowerCreateRequest(ref reason);
            if (request == -1) { request = 0; throw new IOException("Could not prevent automatic sleep."); }
            if (!PowerSetRequest(request, 1)) { CloseHandle(request); request = 0; throw new IOException("Could not enable sleep prevention."); }
        }
        else if (!enabled && request != 0) { PowerClearRequest(request, 1); CloseHandle(request); request = 0; }
    }
    public void Dispose() => SetMonitoring(false);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Size = 32)] private struct ReasonContext { public uint Version, Flags; [MarshalAs(UnmanagedType.LPWStr)] public string Reason; }
    [DllImport("kernel32", SetLastError = true)] private static extern nint PowerCreateRequest(ref ReasonContext context);
    [DllImport("kernel32", SetLastError = true)] private static extern bool PowerSetRequest(nint request, int type);
    [DllImport("kernel32")] private static extern bool PowerClearRequest(nint request, int type);
    [DllImport("kernel32")] private static extern bool CloseHandle(nint handle);
}

/// <summary>Copies an indexed MP4 snapshot or complete legacy fMP4 fragments. Never modifies the original.</summary>
public sealed class FragmentRecovery
{
    public IReadOnlyList<string> Find(string folder) => Directory.Exists(folder)
        ? Directory.EnumerateFiles(folder, "HR_*_part*.partial.mp4", new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false
        }).OrderBy(x => x).ToArray() : [];
    public async Task<string> RecoverAsync(string source, CancellationToken cancellationToken = default)
    {
        var target = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + "." + Guid.NewGuid().ToString("N")[..8] + ".recovered.mp4");
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        var header = new byte[16]; long validEnd = 0;
        bool haveMoov = false, pendingMoof = false;
        Mp4Index? index = null;
        while (input.Position + 8 <= input.Length)
        {
            var start = input.Position;
            await input.ReadExactlyAsync(header.AsMemory(0, 8), cancellationToken);
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            int headerLength = 8;
            if (size == 1)
            {
                if (input.Position + 8 > input.Length) break;
                await input.ReadExactlyAsync(header.AsMemory(8, 8), cancellationToken);
                size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)); headerLength = 16;
            }
            // A robust recording leaves mdat open to EOF until normal finalization.
            if (size == 0 && type == "mdat" && index is not null) size = (ulong)(input.Length - start);
            if (size < (ulong)headerLength || size > (ulong)(input.Length - start)) break;
            if (type == "moov")
            {
                if (size > 32 * 1024 * 1024) throw new InvalidDataException("The MP4 index exceeds the supported size.");
                var payload = new byte[(int)size - headerLength];
                await input.ReadExactlyAsync(payload, cancellationToken);
                index = Mp4Index.Read(payload);
                haveMoov = true;
            }
            input.Position = start + (long)size;
            if (type == "moof") pendingMoof = true;
            if (type == "mdat")
            {
                if (index is not null)
                {
                    if (index.FirstByte < (ulong)(start + headerLength) || index.EndByte > (ulong)input.Position)
                        throw new InvalidDataException("MP4 data referenced by the index is incomplete. The original file has been preserved.");
                    validEnd = input.Position;
                }
                else if (pendingMoof && haveMoov) { validEnd = input.Position; pendingMoof = false; }
            }
        }
        if (validEnd == 0) throw new InvalidDataException("The file has no saved index or complete MP4 fragment to recover.");
        input.Position = 0;
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        var buffer = new byte[65536];
        while (input.Position < validEnd)
        {
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, validEnd - input.Position)), cancellationToken);
            if (count == 0) throw new EndOfStreamException();
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        output.Flush(true); return target;
    }
}
