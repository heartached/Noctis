using Noctis.Services.Loon;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Regression cover for the relay message decoder. A decode failure here is not cosmetic:
/// the exception escapes the receive loop, the WebSocket is torn down, and the relay fails
/// Discord's in-flight artwork fetch with a 504 — which Discord renders as a broken-image
/// placeholder. Every message the relay actually sends must decode.
/// </summary>
public class LoonMessageDecodingTests
{
    /// <summary>
    /// A real ServerMessage{Request} captured off the live relay:
    /// field 2 (request) -> { id = 1, timestamp = {seconds, nanos}, path = "artwork/....jpg" }.
    /// The timestamp is a sub-message the client does not need, so it goes through Skip().
    /// </summary>
    private const string CapturedRequestFrame =
        "12420801120C08FD9693D30610A5A1959A031A3061727477" +
        "6F726B2F37616431343130322D653861332D376434342D66" +
        "3739302D3764383665653135303863352E6A7067";

    [Fact]
    public void DecodesARealRequestFrameIncludingTheTimestampSubMessage()
    {
        // google.protobuf.Timestamp arrives in field 2 of every Request. Skipping a
        // length-delimited field used to advance the cursor by the payload length while
        // discarding the length varint's own bytes, so the reader resumed one byte early —
        // on the tail of the nanos varint — and threw "Unknown wire type". The stray byte
        // is nanos-dependent, which made the failure look intermittent.
        var msg = LoonMessageCodec.DecodeServerMessage(Convert.FromHexString(CapturedRequestFrame));

        Assert.Equal(ServerMessageType.Request, msg.Type);
        Assert.NotNull(msg.Request);
        Assert.Equal(1UL, msg.Request!.Id);
        Assert.Equal("artwork/7ad14102-e8a3-7d44-f790-7d86ee1508c5.jpg", msg.Request.Path);
    }

    [Theory]
    // The byte that follows the skipped field is what a desync misreads as a tag. Vary the
    // nanos tail across every wire type so a regression can't hide behind lucky data.
    [InlineData(0x00)] // would decode as field 0 / varint — silently wrong, no throw
    [InlineData(0x03)] // group start — "Unknown wire type: 3"
    [InlineData(0x04)] // group end   — "Unknown wire type: 4"
    [InlineData(0x0A)] // field 1 / length-delimited — desyncs deeper
    [InlineData(0x7F)]
    public void SkippingAnUnknownSubMessageLandsExactlyOnTheNextField(byte nanosTail)
    {
        // Request{ id = 7, timestamp = <2-byte payload ending in nanosTail>, path = "artwork/a.jpg" }
        var path = "artwork/a.jpg";
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(path);

        var request = new List<byte> { 0x08, 0x07 };                 // field 1 varint = 7
        request.AddRange([0x12, 0x02, 0x10, nanosTail]);             // field 2, len 2
        request.AddRange([0x1A, (byte)pathBytes.Length]);            // field 3, len
        request.AddRange(pathBytes);

        var frame = new List<byte> { 0x12, (byte)request.Count };    // ServerMessage.request
        frame.AddRange(request);

        var msg = LoonMessageCodec.DecodeServerMessage(frame.ToArray());

        Assert.Equal(ServerMessageType.Request, msg.Type);
        Assert.Equal(7UL, msg.Request!.Id);
        Assert.Equal(path, msg.Request.Path);
    }

    [Fact]
    public void DecodesAHelloFrameCapturedFromTheLiveRelay()
    {
        // Hello decoded correctly even with the Skip bug (every field is handled explicitly),
        // which is why connecting always worked and only artwork fetches failed.
        const string hello =
            "0AAC010A1F68747470733A2F2F6E6F637469732D6C6F6F6E2E6475636B646E732E6F7267" +
            "121671654A717A71384E64476473495F464C4A6964714A411A408D6932D4E98DF14F4639" +
            "1434101AAA58F04FBA1C9981C30EB7C69322A32733B81BE9AF1939DF26FBF85ED002C25E" +
            "ABF005BD41E21B396CBAFE0F9C632BDC381A222F0880800410808080201A09696D616765" +
            "2F706E671A0A696D6167652F6A7065671A0A696D6167652F7765627020901C";

        var msg = LoonMessageCodec.DecodeServerMessage(Convert.FromHexString(hello));

        Assert.Equal(ServerMessageType.Hello, msg.Type);
        Assert.Equal("https://noctis-loon.duckdns.org", msg.Hello!.BaseUrl);
        Assert.Equal("qeJqzq8NdGdsI_FLJidqJA", msg.Hello.ClientId);
        Assert.Equal(64, msg.Hello.ConnectionSecret.Length);
        Assert.Equal(65536UL, msg.Hello.Constraints.ChunkSize);
        Assert.Equal(67108864UL, msg.Hello.Constraints.MaxContentSize);
    }

    [Fact]
    public void DecodesTheRelaysRequestClosedAndCloseFrames()
    {
        // Both are captured live. RequestClosed tells us the relay gave up on a fetch;
        // Close precedes the relay dropping the session.
        var closed = LoonMessageCodec.DecodeServerMessage(
            Convert.FromHexString("221508011211726571756573742074696D6564206F7574"));
        Assert.Equal(ServerMessageType.RequestClosed, closed.Type);
        Assert.Equal(1UL, closed.RequestClosed!.RequestId);
        Assert.Equal("request timed out", closed.RequestClosed.Message);

        var close = LoonMessageCodec.DecodeServerMessage(Convert.FromHexString(
            "2A3B080A1237526573706F6E736520666F7220636C6F736564207265717565737420776173" +
            "206E6F7420636C6F73656420696E2074696D65205B23315D"));
        Assert.Equal(ServerMessageType.Close, close.Type);
        Assert.Equal(CloseReason.TimedOut, close.Close!.Reason);
    }

    [Fact]
    public void SkippingAnUnknownTrailingFieldConsumesItExactly()
    {
        // Forward compatibility: a field the relay adds later must be skipped cleanly
        // rather than desyncing the rest of the message.
        var success = new List<byte> { 0x08, 0x2A };            // request_id = 42
        success.AddRange([0x22, 0x03, 0xAA, 0xBB, 0xCC]);       // unknown field 4, 3 bytes
        success.AddRange([0x28, 0x01]);                          // unknown field 5, varint

        var frame = new List<byte> { 0x1A, (byte)success.Count }; // ServerMessage.success
        frame.AddRange(success);

        var msg = LoonMessageCodec.DecodeServerMessage(frame.ToArray());

        Assert.Equal(ServerMessageType.Success, msg.Type);
        Assert.Equal(42UL, msg.Success!.RequestId);
    }
}
