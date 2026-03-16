using System.Text.Json;
using Achates.Server.Mobile;

namespace Achates.Tests;

public sealed class FrameSerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void RequestFrame_Serializes()
    {
        var frame = new RequestFrame
        {
            Id = "abc-123",
            Method = "chat.send",
            Params = JsonSerializer.SerializeToElement(new { text = "Hello" }),
        };
        var json = JsonSerializer.Serialize(frame, Options);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("req", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("abc-123", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("chat.send", doc.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public void ResponseFrame_Success_Serializes()
    {
        var frame = ResponseFrame.Success("abc-123", JsonSerializer.SerializeToElement(new { agents = new[] { "paul" } }));
        var json = JsonSerializer.Serialize(frame, Options);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("res", doc.RootElement.GetProperty("type").GetString());
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void ResponseFrame_Error_Serializes()
    {
        var frame = ResponseFrame.Failure("abc-123", "AGENT_NOT_FOUND", "No agent named 'bob'");
        var json = JsonSerializer.Serialize(frame, Options);
        var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("AGENT_NOT_FOUND", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void EventFrame_Serializes()
    {
        var frame = new EventFrame
        {
            Event = "text.delta",
            Payload = JsonSerializer.SerializeToElement(new { delta = "Hello " }),
            Seq = 42,
        };
        var json = JsonSerializer.Serialize(frame, Options);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("evt", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal(42, doc.RootElement.GetProperty("seq").GetInt32());
    }

    [Fact]
    public void ParseFrame_DetectsRequestType()
    {
        var json = """{"type":"req","id":"x","method":"ping","params":{}}""";
        var frame = FrameParser.Parse(json);
        Assert.IsType<RequestFrame>(frame);
        Assert.Equal("ping", ((RequestFrame)frame).Method);
    }

    [Fact]
    public void ParseFrame_DetectsResponseType()
    {
        var json = """{"type":"res","id":"x","ok":true,"payload":{}}""";
        var frame = FrameParser.Parse(json);
        Assert.IsType<ResponseFrame>(frame);
    }
}
