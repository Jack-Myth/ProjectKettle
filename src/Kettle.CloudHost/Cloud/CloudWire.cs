using System.Text;

namespace Kettle.Cloud;

public sealed record ProtoField(int Number, ulong Value, byte[]? Data);
public static class CloudWire
{
    public static byte[] Message(params (int Field, object Value)[] fields)
    {
        using var stream = new MemoryStream();
        foreach (var (number, value) in fields)
        {
            var bytes = value is string text ? Encoding.UTF8.GetBytes(text) : value as byte[];
            Var(stream, (ulong)(number * 8 + (bytes is null ? 0 : 2)));
            if (bytes is null)
                Var(stream, Convert.ToUInt64(value));
            else
            {
                Var(stream, (ulong)bytes.Length);
                stream.Write(bytes);
            }
        }

        return stream.ToArray();
    }

    public static ProtoField[] Parse(byte[] data)
    {
        var result = new List<ProtoField>();
        int offset = 0;
        while (offset < data.Length)
        {
            var key = Read(data, ref offset);
            var number = checked((int)(key >> 3));
            if (number == 0)
                throw new InvalidDataException("Invalid protobuf field.");
            switch (key & 7)
            {
                case 0:
                    result.Add(new(number, Read(data, ref offset), null));
                    break;
                case 2:
                    var size = checked((int)Read(data, ref offset));
                    if (size < 0 || size > data.Length - offset)
                        throw new InvalidDataException("Truncated protobuf.");
                    result.Add(new(number, 0, data.AsSpan(offset, size).ToArray()));
                    offset += size;
                    break;
                case 1:
                    if (offset + 8 > data.Length)
                        throw new InvalidDataException();
                    result.Add(new(number, BitConverter.ToUInt64(data, offset), null));
                    offset += 8;
                    break;
                case 5:
                    if (offset + 4 > data.Length)
                        throw new InvalidDataException();
                    result.Add(new(number, BitConverter.ToUInt32(data, offset), null));
                    offset += 4;
                    break;
                default:
                    throw new InvalidDataException("Unsupported protobuf wire type.");
            }
        }

        return result.ToArray();
    }

    public static ulong Number(this IEnumerable<ProtoField> fields, int number) => fields.FirstOrDefault(f => f.Number == number)?.Value ?? 0;
    public static byte[] Bytes(this IEnumerable<ProtoField> fields, int number) => fields.FirstOrDefault(f => f.Number == number)?.Data ?? [];
    public static string Text(this IEnumerable<ProtoField> fields, int number) => Encoding.UTF8.GetString(fields.Bytes(number));
    private static void Var(Stream stream, ulong n)
    {
        while (n >= 128)
        {
            stream.WriteByte((byte)((n & 127) | 128));
            n >>= 7;
        }

        stream.WriteByte((byte)n);
    }

    private static ulong Read(byte[] data, ref int p)
    {
        ulong n = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (p >= data.Length)
                throw new InvalidDataException();
            var b = data[p++];
            n |= (ulong)(b & 127) << shift;
            if ((b & 128) == 0)
                return n;
        }

        throw new InvalidDataException();
    }
}
