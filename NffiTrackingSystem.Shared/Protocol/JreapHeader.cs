using System.Buffers.Binary;
namespace NffiTrackingSystem.Shared.Protocol;
public sealed class JreapHeader {
    public uint Magic { get; set; } = JreapConstants.Magic;
    public ushort MessageType { get; set; } = JreapConstants.MessageTypeNffiPpli;
    public ushort SequenceNumber { get; set; }
    public long TimestampUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public byte[] ToBytes() {
        var b = new byte[JreapConstants.HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(0,4), Magic);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4,2), MessageType);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6,2), SequenceNumber);
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(8,8), TimestampUnixMs);
        return b;
    }
    public static JreapHeader FromBytes(ReadOnlySpan<byte> buf) {
        if (buf.Length < JreapConstants.HeaderSize) throw new ArgumentException("Buffer too small.");
        return new JreapHeader {
            Magic = BinaryPrimitives.ReadUInt32BigEndian(buf.Slice(0,4)),
            MessageType = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(4,2)),
            SequenceNumber = BinaryPrimitives.ReadUInt16BigEndian(buf.Slice(6,2)),
            TimestampUnixMs = BinaryPrimitives.ReadInt64BigEndian(buf.Slice(8,8))
        };
    }
}