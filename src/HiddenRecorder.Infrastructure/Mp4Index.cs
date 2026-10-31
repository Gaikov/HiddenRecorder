using System.Buffers.Binary;
using System.Text;

namespace HiddenRecorder.Infrastructure;

// Validates the ordinary MP4 sample tables before copying a crash-interrupted file.
// The fragmented format from older releases is handled separately by FragmentRecovery.
internal sealed record Mp4Index(ulong FirstByte, ulong EndByte)
{
    public static Mp4Index? Read(byte[] moov)
    {
        var children = Boxes(moov);
        if (children.Any(box => box.Type == "mvex")) return null;
        ulong first = ulong.MaxValue, end = 0;
        var tracks = children.Where(box => box.Type == "trak").ToArray();
        if (tracks.Length == 0) return null;
        foreach (var track in tracks)
        {
            var table = Child(Child(Child(track.Data, "mdia"), "minf"), "stbl");
            var sizes = Child(table, "stsz");
            var fixedSize = U32(sizes, 4); var samples = U32(sizes, 8);
            if (samples == 0) return null; // No playable snapshot yet.
            if (fixedSize == 0 && 12UL + samples * 4UL > (ulong)sizes.Length) throw Invalid();
            var mapping = Child(table, "stsc"); var entries = U32(mapping, 4);
            if (entries == 0 || 8UL + entries * 12UL > (ulong)mapping.Length) throw Invalid();
            var offsets = Boxes(table).SingleOrDefault(box => box.Type is "stco" or "co64") ?? throw Invalid();
            var wide = offsets.Type == "co64"; var chunks = U32(offsets.Data, 4);
            if (chunks == 0 || 8UL + chunks * (wide ? 8UL : 4UL) > (ulong)offsets.Data.Length) throw Invalid();
            uint previous = 0;
            for (uint i = 0; i < entries; i++)
            {
                var chunk = U32(mapping, checked(8 + (int)i * 12));
                if (chunk <= previous || chunk > chunks || (i == 0 && chunk != 1) ||
                    U32(mapping, checked(12 + (int)i * 12)) == 0) throw Invalid();
                previous = chunk;
            }
            uint entry = 0, sample = 0;
            for (uint chunk = 1; chunk <= chunks; chunk++)
            {
                if (entry + 1 < entries && chunk == U32(mapping, checked(8 + (int)(entry + 1) * 12))) entry++;
                var count = U32(mapping, checked(12 + (int)entry * 12));
                if (count > samples - sample) throw Invalid();
                var offsetPosition = checked(8 + (int)(chunk - 1) * (wide ? 8 : 4));
                ulong offset = wide ? BinaryPrimitives.ReadUInt64BigEndian(offsets.Data.Span[offsetPosition..]) : U32(offsets.Data, offsetPosition);
                ulong length = (ulong)fixedSize * count;
                if (fixedSize == 0)
                    for (uint i = 0; i < count; i++) length += U32(sizes, checked(12 + (int)(sample + i) * 4));
                sample += count;
                if (length == 0 || offset > ulong.MaxValue - length) throw Invalid();
                first = Math.Min(first, offset); end = Math.Max(end, offset + length);
            }
            if (sample != samples) throw Invalid();
        }
        return new(first, end);
    }

    private sealed record Box(string Type, ReadOnlyMemory<byte> Data);
    private static List<Box> Boxes(ReadOnlyMemory<byte> data)
    {
        var boxes = new List<Box>(); var position = 0;
        while (position < data.Length)
        {
            if (data.Length - position < 8) throw Invalid();
            ulong size = U32(data, position); var header = 8;
            var type = Encoding.ASCII.GetString(data.Span.Slice(position + 4, 4));
            if (size == 1)
            {
                if (data.Length - position < 16) throw Invalid();
                size = BinaryPrimitives.ReadUInt64BigEndian(data.Span[(position + 8)..]); header = 16;
            }
            if (size < (ulong)header || size > (ulong)(data.Length - position)) throw Invalid();
            boxes.Add(new(type, data.Slice(position + header, (int)size - header)));
            position += (int)size;
        }
        return boxes;
    }
    private static ReadOnlyMemory<byte> Child(ReadOnlyMemory<byte> parent, string type) =>
        Boxes(parent).SingleOrDefault(box => box.Type == type)?.Data ?? throw Invalid();
    private static uint U32(ReadOnlyMemory<byte> data, int position)
    {
        if (position < 0 || position > data.Length - 4) throw Invalid();
        return BinaryPrimitives.ReadUInt32BigEndian(data.Span[position..]);
    }
    private static InvalidDataException Invalid() => new("Повреждён индекс MP4; исходный файл сохранён.");
}
