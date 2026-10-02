using System.Buffers.Binary;
using System.Net;
using System.Text;
using PaqetFire.Core.Configuration;

namespace PaqetFire.Broker.Diagnostics;

public sealed record XrayTrafficCounters(long UplinkBytes, long DownlinkBytes);

public interface IXrayTrafficStats
{
    ValueTask<XrayTrafficCounters?> QueryAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Minimal gRPC (HTTP/2 cleartext) client for Xray's loopback StatsService.
/// Protobuf messages are encoded by hand to avoid a gRPC tooling dependency.
/// </summary>
public sealed class XrayStatsClient : IXrayTrafficStats, IDisposable
{
    private const string QueryStatsPath = "/xray.app.stats.command.StatsService/QueryStats";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(1);
    private static readonly string CounterPrefix =
        $"outbound>>>{XrayJsonConfigurationWriter.PaqetOutboundTag}>>>traffic>>>";

    private readonly HttpClient client;

    public XrayStatsClient()
    {
        client = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = RequestTimeout,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{XrayJsonConfigurationWriter.StatsApiPort}"),
            Timeout = RequestTimeout,
        };
    }

    public async ValueTask<XrayTrafficCounters?> QueryAsync(CancellationToken cancellationToken)
    {
        // gRPC needs HTTP/2 prior knowledge over cleartext; a manually created request
        // does not inherit the client's default version.
        using var request = new HttpRequestMessage(HttpMethod.Post, QueryStatsPath)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent(CreateFrame(EncodeQueryRequest(CounterPrefix))),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/grpc");
        request.Headers.TryAddWithoutValidation("TE", "trailers");

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var status = GetGrpcStatus(response);
        if (status is not null and not "0")
        {
            return null;
        }

        return ParseCounters(body);
    }

    public void Dispose() => client.Dispose();

    internal static byte[] EncodeQueryRequest(string pattern)
    {
        var patternBytes = Encoding.UTF8.GetBytes(pattern);
        var message = new List<byte>(patternBytes.Length + 8) { 0x0A };
        WriteVarint(message, (ulong)patternBytes.Length);
        message.AddRange(patternBytes);
        return message.ToArray();
    }

    internal static XrayTrafficCounters ParseCounters(ReadOnlySpan<byte> body)
    {
        long uplink = 0;
        long downlink = 0;
        while (body.Length >= 5)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(body[1..5]);
            if (body[0] != 0 || length < 0 || length > body.Length - 5)
            {
                throw new InvalidDataException("Xray returned an unsupported gRPC frame.");
            }

            var message = body.Slice(5, length);
            body = body[(5 + length)..];
            var reader = new ProtoReader(message);
            while (reader.TryReadField(out var field, out var wireType))
            {
                if (field != 1 || wireType != 2)
                {
                    reader.Skip(wireType);
                    continue;
                }

                var (name, value) = ParseStat(reader.ReadBytes());
                if (name.EndsWith(">>>uplink", StringComparison.Ordinal))
                {
                    uplink += value;
                }
                else if (name.EndsWith(">>>downlink", StringComparison.Ordinal))
                {
                    downlink += value;
                }
            }
        }

        return new XrayTrafficCounters(uplink, downlink);
    }

    private static (string Name, long Value) ParseStat(ReadOnlySpan<byte> stat)
    {
        var name = string.Empty;
        long value = 0;
        var reader = new ProtoReader(stat);
        while (reader.TryReadField(out var field, out var wireType))
        {
            if (field == 1 && wireType == 2)
            {
                name = Encoding.UTF8.GetString(reader.ReadBytes());
            }
            else if (field == 2 && wireType == 0)
            {
                value = (long)reader.ReadVarint();
            }
            else
            {
                reader.Skip(wireType);
            }
        }

        return (name, value);
    }

    private static string? GetGrpcStatus(HttpResponseMessage response)
    {
        if (response.TrailingHeaders.TryGetValues("grpc-status", out var trailer))
        {
            return trailer.FirstOrDefault();
        }

        // Trailers-only responses carry the status in the regular headers.
        return response.Headers.TryGetValues("grpc-status", out var header) ? header.FirstOrDefault() : null;
    }

    private static byte[] CreateFrame(byte[] message)
    {
        var frame = new byte[5 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)message.Length);
        message.CopyTo(frame, 5);
        return frame;
    }

    private static void WriteVarint(List<byte> buffer, ulong value)
    {
        while (value >= 0x80)
        {
            buffer.Add((byte)(value | 0x80));
            value >>= 7;
        }

        buffer.Add((byte)value);
    }
}

internal ref struct ProtoReader(ReadOnlySpan<byte> data)
{
    private ReadOnlySpan<byte> remaining = data;

    public bool TryReadField(out int field, out int wireType)
    {
        if (remaining.IsEmpty)
        {
            field = 0;
            wireType = 0;
            return false;
        }

        var key = ReadVarint();
        field = (int)(key >> 3);
        wireType = (int)(key & 0x07);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (remaining.IsEmpty)
            {
                throw new InvalidDataException("A protobuf varint was truncated.");
            }

            var current = remaining[0];
            remaining = remaining[1..];
            result |= (ulong)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("A protobuf varint was too long.");
    }

    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = ReadVarint();
        if (length > (ulong)remaining.Length)
        {
            throw new InvalidDataException("A protobuf field was truncated.");
        }

        var value = remaining[..(int)length];
        remaining = remaining[(int)length..];
        return value;
    }

    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case 0:
                ReadVarint();
                break;
            case 1:
                Advance(8);
                break;
            case 2:
                ReadBytes();
                break;
            case 5:
                Advance(4);
                break;
            default:
                throw new InvalidDataException($"Protobuf wire type {wireType} is not supported.");
        }
    }

    private void Advance(int count)
    {
        if (remaining.Length < count)
        {
            throw new InvalidDataException("A protobuf field was truncated.");
        }

        remaining = remaining[count..];
    }
}
