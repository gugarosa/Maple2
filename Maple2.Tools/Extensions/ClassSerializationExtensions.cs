using Maple2.PacketLib.Tools;

namespace Maple2.Tools.Extensions;

public static class ClassSerializationExtensions {
    public static byte[] Serialize<T>(this T value) where T : IByteSerializable {
        using var writer = new PoolByteWriter();
        writer.WriteClass<T>(value);
        return writer.ToArray();
    }

    public static T Deserialize<T>(this byte[] bytes) where T : IByteDeserializable {
        var reader = new ByteReader(bytes);
        return reader.ReadClass<T>();
    }
}
