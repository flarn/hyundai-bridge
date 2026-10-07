using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace HyundaiBridge.Hyundai;

// Hyundai EU SDK white-box cipher, derived from hyundai_kia_connect_api.
// The protocol tables and their MIT license are in Gspa/. Not a generic AES cipher.
internal sealed class GspaStamp
{
    private static readonly int[] Shift = [0, 5, 10, 15, 4, 9, 14, 3, 8, 13, 2, 7, 12, 1, 6, 11];
    private readonly Dictionary<string, uint[][]> tables = new();

    internal GspaStamp()
    {
        using var stream = typeof(GspaStamp).Assembly.GetManifestResourceStream("HyundaiBridge.Hyundai.Gspa.hyundai_cipher_params.json")!;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        var sbox = root.GetProperty("sbox").EnumerateArray().Select(Hex).ToArray();
        foreach (var group in new[] { "X4", "X27", "GAP", "POST_X27", "X8" })
        {
            var entries = root.GetProperty(group);
            var lookup = new uint[entries.EnumerateObject().Count()][];
            foreach (var entry in entries.EnumerateObject())
            {
                var p = entry.Value;
                var values = new uint[256];
                if (p.TryGetProperty("M1", out var m1))
                {
                    var a = p.GetProperty("a").GetUInt32();
                    var t2 = p.GetProperty("t2").GetUInt32();
                    var c = p.GetProperty("c").GetUInt32();
                    var first = m1.EnumerateArray().Select(Hex).ToArray();
                    var second = p.GetProperty("M2").EnumerateArray().Select(Hex).ToArray();
                    var linear = p.GetProperty("Lcols").EnumerateArray().Select(Hex).ToArray();
                    var isByte = p.GetProperty("is_byte").GetBoolean();
                    for (uint i = 0; i < 256; i++)
                    {
                        var mapped = Apply(second, sbox[Apply(first, i ^ a)]) ^ t2;
                        values[i] = isByte ? (c ^ mapped) & 255 : c ^ Apply(linear, mapped);
                    }
                }
                else
                {
                    var columns = p.GetProperty("lcols").EnumerateArray().Select(Hex).ToArray();
                    var constant = p.GetProperty("lconst").GetUInt32();
                    for (uint i = 0; i < 256; i++) values[i] = constant ^ Apply(columns, i);
                }
                lookup[int.Parse(entry.Name, CultureInfo.InvariantCulture)] = values;
            }
            tables[group] = lookup;
        }
    }

    private static uint Hex(JsonElement element) => uint.Parse(element.GetString()!.Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    private static uint Apply(uint[] columns, uint value)
    {
        uint result = 0;
        for (var i = 0; value != 0; i++, value >>= 1)
            if ((value & 1) != 0) result ^= columns[i];
        return result;
    }

    internal byte[] EncryptBlock(byte[] block)
    {
        if (block.Length != 16) throw new ArgumentException("Stamp block must be 16 bytes");
        var state = block.ToArray();
        foreach (var (substitution, linear, rounds) in new[] { ("X4", "X27", 8), ("GAP", "POST_X27", 5) })
            for (var round = 0; round < rounds; round++)
            {
                state = Shift.Select(i => state[i]).ToArray();
                for (var column = 0; column < 4; column++)
                {
                    var offset = column * 4;
                    var index = round * 16 + offset;
                    uint word = 0, output = 0;
                    for (var j = 0; j < 4; j++) word ^= tables[substitution][index + j][state[offset + j]];
                    for (var j = 0; j < 4; j++) output ^= tables[linear][index + j][(word >> (8 * (3 - j))) & 255];
                    BinaryPrimitives.WriteUInt32BigEndian(state.AsSpan(offset, 4), output);
                }
            }
        return Enumerable.Range(0, 16).Select(i => (byte)tables["X8"][i][state[Shift[i]]]).ToArray();
    }

    internal string Compute(string requestId, long epochSeconds, string userId)
    {
        var bytes = Encoding.UTF8.GetBytes($"{requestId}:{epochSeconds}:{userId}");
        var feedback = Encoding.ASCII.GetBytes("iv.ccsp.stamp.eu");
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var mask = EncryptBlock(feedback);
            var count = Math.Min(16, bytes.Length - offset);
            for (var i = 0; i < count; i++) bytes[offset + i] ^= mask[i];
            if (count == 16) feedback = bytes.AsSpan(offset, 16).ToArray();
        }
        return Convert.ToBase64String(bytes);
    }

    internal static string RequestId(string deviceId, DateTimeOffset now)
    {
        var bytes = new byte[14];
        Span<byte> timestamp = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(timestamp, now.ToUnixTimeMilliseconds() - 1577836800000);
        timestamp[3..].CopyTo(bytes);
        Convert.FromHexString(deviceId.Replace("-", "")[..16]).CopyTo(bytes, 5);
        bytes[13] = 6;
        return Convert.ToBase64String(bytes).TrimEnd('=');
    }
}
