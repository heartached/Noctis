namespace Noctis.Services.Loon;

// ── Server → Client messages ──

internal sealed class Constraints
{
    public ulong ChunkSize { get; init; }
    public ulong MaxContentSize { get; init; }
    public List<string> AcceptedContentTypes { get; init; } = [];
    public uint CacheDuration { get; init; }
}

internal sealed class HelloMessage
{
    public string BaseUrl { get; init; } = "";
    public string ClientId { get; init; } = "";
    public byte[] ConnectionSecret { get; init; } = [];
    public Constraints Constraints { get; init; } = new();
}

internal sealed class RequestMessage
{
    public ulong Id { get; init; }
    public string Path { get; init; } = "";
}

internal sealed class RequestClosedMessage
{
    public ulong RequestId { get; init; }
    public string Message { get; init; } = "";
}

internal sealed class SuccessMessage
{
    public ulong RequestId { get; init; }
}

internal enum CloseReason
{
    Unspecified = 0,
    Closed = 1,
    Error = 2,
    InvalidClientMessage = 3,
    InvalidRequestId = 4,
    ForbiddenContentType = 5,
    InvalidContentSize = 6,
    ContentChunkOutOfSequence = 7,
    InvalidChunkSize = 8,
    InvalidFilename = 9,
    TimedOut = 10,
}

internal sealed class CloseMessage
{
    public CloseReason Reason { get; init; }
    public string Message { get; init; } = "";
}

// ── Parsed wrapper ──

internal enum ServerMessageType { Hello, Request, Success, RequestClosed, Close }

internal sealed class ServerMessage
{
    public ServerMessageType Type { get; init; }
    public HelloMessage? Hello { get; init; }
    public RequestMessage? Request { get; init; }
    public SuccessMessage? Success { get; init; }
    public RequestClosedMessage? RequestClosed { get; init; }
    public CloseMessage? Close { get; init; }
}

// ── Codec ──

internal static class LoonMessageCodec
{
    // ── Decode server messages ──

    public static ServerMessage DecodeServerMessage(ReadOnlySpan<byte> data)
    {
        var reader = new LoonProtobuf.ProtoReader(data);
        while (reader.HasMore)
        {
            var (field, wire) = reader.ReadTag();
            switch (field)
            {
                case 1: return new ServerMessage { Type = ServerMessageType.Hello, Hello = DecodeHello(reader.ReadSubMessage()) };
                case 2: return new ServerMessage { Type = ServerMessageType.Request, Request = DecodeRequest(reader.ReadSubMessage()) };
                case 3: return new ServerMessage { Type = ServerMessageType.Success, Success = DecodeSuccess(reader.ReadSubMessage()) };
                case 4: return new ServerMessage { Type = ServerMessageType.RequestClosed, RequestClosed = DecodeRequestClosed(reader.ReadSubMessage()) };
                case 5: return new ServerMessage { Type = ServerMessageType.Close, Close = DecodeClose(reader.ReadSubMessage()) };
                default: reader.Skip(wire); break;
            }
        }
        throw new InvalidDataException("Empty ServerMessage");
    }

    private static HelloMessage DecodeHello(LoonProtobuf.ProtoReader r)
    {
        string baseUrl = "", clientId = "";
        byte[] secret = [];
        Constraints? constraints = null;

        while (r.HasMore)
        {
            var (f, w) = r.ReadTag();
            switch (f)
            {
                case 1: baseUrl = r.ReadString(); break;
                case 2: clientId = r.ReadString(); break;
                case 3: secret = r.ReadBytes(); break;
                case 4: constraints = DecodeConstraints(r.ReadSubMessage()); break;
                default: r.Skip(w); break;
            }
        }

        return new HelloMessage
        {
            BaseUrl = baseUrl,
            ClientId = clientId,
            ConnectionSecret = secret,
            Constraints = constraints ?? new Constraints(),
        };
    }

    private static Constraints DecodeConstraints(LoonProtobuf.ProtoReader r)
    {
        ulong chunkSize = 0, maxSize = 0;
        uint cacheDuration = 0;
        var types = new List<string>();

        while (r.HasMore)
        {
            var (f, w) = r.ReadTag();
            switch (f)
            {
                case 1: chunkSize = r.ReadUInt64(); break;
                case 2: maxSize = r.ReadUInt64(); break;
                case 3: types.Add(r.ReadString()); break;
                case 4: cacheDuration = r.ReadUInt32(); break;
                default: r.Skip(w); break;
            }
        }

        return new Constraints
        {
            ChunkSize = chunkSize,
            MaxContentSize = maxSize,
            AcceptedContentTypes = types,
            CacheDuration = cacheDuration,
        };
    }

    private static RequestMessage DecodeRequest(LoonProtobuf.ProtoReader r)
    {
        ulong id = 0;
        string path = "";

        while (r.HasMore)
        {
            var (f, w) = r.ReadTag();
            switch (f)
            {
                case 1: id = r.ReadUInt64(); break;
                case 2: r.Skip(w); break; // timestamp — not needed
                case 3: path = r.ReadString(); break;
                default: r.Skip(w); break;
            }
        }

        return new RequestMessage { Id = id, Path = path };
    }

    private static SuccessMessage DecodeSuccess(LoonProtobuf.ProtoReader r)
    {
        ulong id = 0;
        while (r.HasMore)
        {
            var (f, w) = r.ReadTag();
            if (f == 1) id = r.ReadUInt64(); else r.Skip(w);
        }
        return new SuccessMessage { RequestId = id };
    }

    private static RequestClosedMessage DecodeRequestClosed(LoonProtobuf.ProtoReader r)
    {
        ulong id = 0;
        string msg = "";
        while (r.HasMore)
        {
            var (f, w) = r.ReadTag();
            switch (f)
            {
                case 1: id = r.ReadUInt64(); break;
                case 2: msg = r.ReadString(); break;
                default: r.Skip(w); break;
            }
        }
        return new RequestClosedMessage { RequestId = id, Message = msg };
    }

    private static CloseMessage DecodeClose(LoonProtobuf.ProtoReader r)
    {
        CloseReason reason = CloseReason.Unspecified;
        string msg = "";
        while (r.HasMore)
        {
            var (f, w) = r.ReadTag();
            switch (f)
            {
                case 1: reason = (CloseReason)r.ReadUInt32(); break;
                case 2: msg = r.ReadString(); break;
                default: r.Skip(w); break;
            }
        }
        return new CloseMessage { Reason = reason, Message = msg };
    }

    // ── Encode client messages ──

    /// <summary>Encodes an EmptyResponse wrapped in ClientMessage.</summary>
    public static byte[] EncodeEmptyResponse(ulong requestId)
    {
        var w = new LoonProtobuf.ProtoWriter();
        w.WriteSubMessageField(1, inner => // ClientMessage.empty_response = field 1
        {
            inner.WriteVarintField(1, requestId); // EmptyResponse.request_id
        });
        return w.ToArray();
    }

    /// <summary>Encodes a ContentHeader wrapped in ClientMessage.</summary>
    public static byte[] EncodeContentHeader(ulong requestId, string contentType, ulong contentSize)
    {
        var w = new LoonProtobuf.ProtoWriter();
        w.WriteSubMessageField(2, inner => // ClientMessage.content_header = field 2
        {
            inner.WriteVarintField(1, requestId);       // request_id
            inner.WriteStringField(2, contentType);     // content_type
            inner.WriteVarintField(3, contentSize);     // content_size
        });
        return w.ToArray();
    }

    /// <summary>Encodes a ContentChunk wrapped in ClientMessage.</summary>
    public static byte[] EncodeContentChunk(ulong requestId, ulong sequence, byte[] data)
    {
        var w = new LoonProtobuf.ProtoWriter();
        w.WriteSubMessageField(3, inner => // ClientMessage.content_chunk = field 3
        {
            inner.WriteVarintField(1, requestId);   // request_id
            inner.WriteVarintField(2, sequence);    // sequence
            inner.WriteBytesField(3, data);         // data
        });
        return w.ToArray();
    }

    /// <summary>Encodes a CloseResponse wrapped in ClientMessage.</summary>
    public static byte[] EncodeCloseResponse(ulong requestId)
    {
        var w = new LoonProtobuf.ProtoWriter();
        w.WriteSubMessageField(4, inner => // ClientMessage.close_response = field 4
        {
            inner.WriteVarintField(1, requestId); // request_id
        });
        return w.ToArray();
    }
}
