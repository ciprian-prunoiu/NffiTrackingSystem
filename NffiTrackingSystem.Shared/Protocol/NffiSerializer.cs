using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NffiTrackingSystem.Shared.Models;
namespace NffiTrackingSystem.Shared.Protocol;
public static class NffiSerializer {
    private static readonly JsonSerializerOptions Opt = new() {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    public static byte[] Serialize(NffiMessage m) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m, Opt));
    public static NffiMessage Deserialize(byte[] p) {
        var msg = JsonSerializer.Deserialize<NffiMessage>(Encoding.UTF8.GetString(p), Opt);
        return msg ?? throw new InvalidOperationException("Invalid NFFI payload.");
    }
    public static string ToXml(NffiMessage m) {
        var sb = new StringBuilder();
        sb.Append("<NFFI>");
        sb.Append($"<latitude>{m.PositionalData.Coordinates.Latitude:F6}</latitude>");
        sb.Append($"<longitude>{m.PositionalData.Coordinates.Longitude:F6}</longitude>");
        sb.Append($"<unitId>{m.Identification.UnitId}</unitId>");
        sb.Append($"<timestamp>{m.Status.TimestampUtc:O}</timestamp>");
        sb.Append("</NFFI>");
        return sb.ToString();
    }
}