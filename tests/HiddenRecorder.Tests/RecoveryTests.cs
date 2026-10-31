using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HiddenRecorder.Infrastructure;
using Xunit;

namespace HiddenRecorder.Tests;
public sealed class RecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task CopiesIndexedSnapshotAndPreservesOriginal(bool closedMedia, bool wideOffsets)
    {
        var folder = Path.Combine(Path.GetTempPath(), "HR-test-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "HR_test_part000.partial.mp4");
            var bytes = IndexedFile(closedMedia, wideOffsets);
            await File.WriteAllBytesAsync(source, bytes);
            var recovered = await new FragmentRecovery().RecoverAsync(source);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(recovered));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact] public async Task RejectsIndexPointingBeyondRecordedData()
    {
        var source = Path.GetTempFileName();
        try
        {
            var bytes = IndexedFile(false, false)[..^3];
            await File.WriteAllBytesAsync(source, bytes);
            await Assert.ThrowsAsync<InvalidDataException>(() => new FragmentRecovery().RecoverAsync(source));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        }
        finally { File.Delete(source); }
    }
    [Fact] public async Task RejectsEmptyIndexedSnapshot()
    {
        var source = Path.GetTempFileName();
        try
        {
            var bytes = Box("ftyp", [0]).Concat(Index(0, false, 0)).Concat(Box("mdat", [1,2])).ToArray();
            await File.WriteAllBytesAsync(source, bytes);
            await Assert.ThrowsAsync<InvalidDataException>(() => new FragmentRecovery().RecoverAsync(source));
        }
        finally { File.Delete(source); }
    }
    [Fact] public async Task CopiesCompleteFragmentsAndPreservesOriginal()
    {
        var folder = Path.Combine(Path.GetTempPath(), "HR-test-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
        try
        {
            var original = Path.Combine(folder, "HR_test_part000.partial.mp4");
            var complete = Box("ftyp", [0,0,0,0]).Concat(Box("moov", Box("mvex", []))).Concat(Box("moof", [1])).Concat(Box("mdat", [2,3,4])).ToArray();
            var bytes = complete.Concat(Box("moof", [5])).Concat(Box("mdat", new byte[100]).Take(20)).ToArray();
            await File.WriteAllBytesAsync(original, bytes); var hash = SHA256.HashData(bytes);
            var recovered = await new FragmentRecovery().RecoverAsync(original);
            Assert.Equal(complete, await File.ReadAllBytesAsync(recovered));
            Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(original)));
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact] public async Task RejectsIncompleteFirstFragment()
    {
        var source = Path.GetTempFileName();
        try { await File.WriteAllBytesAsync(source, Box("ftyp", [0]).Concat(Box("moov", Box("mvex", []))).ToArray()); await Assert.ThrowsAsync<InvalidDataException>(() => new FragmentRecovery().RecoverAsync(source)); }
        finally { File.Delete(source); }
    }
    private static byte[] IndexedFile(bool closedMedia, bool wideOffsets)
    {
        var ftyp = Box("ftyp", [0]);
        // The other index slot is hidden inside free, even if interrupted halfway through writing it.
        var reserved = Box("free", [0,0,0,80,109,111,111,118,1]);
        var offset = (uint)(ftyp.Length + reserved.Length + Index(0, wideOffsets).Length + 8);
        var media = Box("mdat", [1,2,3,4,5]); // Four indexed bytes plus an unindexed tail.
        if (!closedMedia) BinaryPrimitives.WriteUInt32BigEndian(media, 0);
        return ftyp.Concat(reserved).Concat(Index(offset, wideOffsets)).Concat(media).ToArray();
    }
    private static byte[] Index(uint offset, bool wide, uint samples = 2)
    {
        var sizes = Box("stsz", Words(0, 0, samples, 2, 2));
        var mapping = Box("stsc", Words(0, 1, 1, 2, 1));
        var offsets = Box(wide ? "co64" : "stco", wide ? Words(0, 1, 0, offset) : Words(0, 1, offset));
        return Box("moov", Box("trak", Box("mdia", Box("minf", Box("stbl", sizes.Concat(mapping).Concat(offsets).ToArray())))));
    }
    private static byte[] Words(params uint[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }
    private static byte[] Box(string type, byte[] content)
    {
        var box = new byte[8 + content.Length]; BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length); Encoding.ASCII.GetBytes(type).CopyTo(box, 4); content.CopyTo(box, 8); return box;
    }
}
