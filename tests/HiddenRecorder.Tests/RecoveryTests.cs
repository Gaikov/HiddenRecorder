using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HiddenRecorder.Infrastructure;
using Xunit;

namespace HiddenRecorder.Tests;
public sealed class RecoveryTests
{
    [Fact] public async Task CopiesCompleteFragmentsAndPreservesOriginal()
    {
        var folder = Path.Combine(Path.GetTempPath(), "HR-test-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
        try
        {
            var original = Path.Combine(folder, "HR_test_part000.partial.mp4");
            var complete = Box("ftyp", [0,0,0,0]).Concat(Box("moov", [0])).Concat(Box("moof", [1])).Concat(Box("mdat", [2,3,4])).ToArray();
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
        try { await File.WriteAllBytesAsync(source, Box("ftyp", [0]).Concat(Box("moov", [0])).ToArray()); await Assert.ThrowsAsync<InvalidDataException>(() => new FragmentRecovery().RecoverAsync(source)); }
        finally { File.Delete(source); }
    }
    private static byte[] Box(string type, byte[] content)
    {
        var box = new byte[8 + content.Length]; BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length); Encoding.ASCII.GetBytes(type).CopyTo(box, 4); content.CopyTo(box, 8); return box;
    }
}
