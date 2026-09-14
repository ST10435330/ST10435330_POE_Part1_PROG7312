using System.Text.Json.Serialization;
namespace SmartX;

// T stays strongly typed from JSON deserialisation to historical storage.
public sealed record TelemetryPacket<T>([property: JsonRequired] Guid SensorId, [property: JsonRequired] DateTimeOffset Timestamp, [property: JsonRequired] T Value) where T : struct;
public readonly record struct MeterReading(int Watts)
{
    public static MeterReading operator +(MeterReading a, MeterReading b) => new(checked(a.Watts + b.Watts));
    public static MeterReading operator -(MeterReading a, MeterReading b) => new(checked(a.Watts - b.Watts));
}
public sealed record DeploymentNode(string Name, bool Enabled, List<DeploymentNode> Children);
public static class DeploymentValidator
{
    public static string? Validate(DeploymentNode? node, string[] path, int index = 0)
    {
        if (node is null || path.Length == 0 || path.Length > 8 || index >= 8) return "Deployment path must contain 1–8 existing levels.";
        if (!node.Enabled) return $"Deployment level '{node.Name}' is disabled.";
        if (!string.Equals(node.Name, path[index], StringComparison.Ordinal)) return "Deployment path does not match the configured tree.";
        if (index == path.Length - 1) return node.Children.Count == 0 ? null : "Select a leaf deployment location.";
        var child = node.Children.FirstOrDefault(c => c.Name == path[index + 1]);
        return child is null ? "Unknown deployment level." : Validate(child, path, index + 1);
    }
}
public sealed record SensorRegistration(string Identifier, string Category, string[] Path, int ExpectedIntervalSeconds);
public sealed record Sensor(Guid Id, string Identifier, string Category, string[] Path, int ExpectedIntervalSeconds, DateTimeOffset RegisteredAt);
public sealed record Attachment(Guid Id, Guid SensorId, string Name, long Size, DateTimeOffset UploadedAt);
public sealed class History<T> where T : struct
{
    private readonly List<TelemetryPacket<T>> packets = new(2048);
    public int Count => packets.Count;
    public TelemetryPacket<T>? Latest => packets.Count == 0 ? null : packets[^1];
    public void Append(TelemetryPacket<T> packet)
    {
        // Bounded list avoids unlimited retention on an edge gateway.
        if (packets.Count >= 2048) packets.RemoveRange(0, 256);
        packets.Add(packet);
    }
    public void Import(TelemetryPacket<T>[][] batches)
    {
        // Jagged arrays represent variable-sized sequential telemetry batches.
        foreach (var batch in batches) foreach (var packet in batch) Append(packet);
    }
    public TelemetryPacket<T>[] Tail(int count) => packets.TakeLast(Math.Clamp(count, 1, 2048)).ToArray();
}
public sealed class GatewayException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}
