using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Achates.Server.Speech;

namespace Achates.Tests.Speech;

public sealed class KokoroSpeechSynthesizerTests
{
    [Fact]
    public async Task SynthesizeAsync_posts_openai_compatible_payload_and_returns_bytes()
    {
        byte[]? capturedBody = null;
        string? capturedUrl = null;

        var handler = new StubHandler(async req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            capturedBody = await req.Content!.ReadAsByteArrayAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0xff, 0xfb, 0x90, 0x44]) // fake MP3 header bytes
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("audio/mpeg") },
                },
            };
        });

        var client = new HttpClient(handler);
        var synth = new KokoroSpeechSynthesizer(client, new Uri("http://127.0.0.1:8880"));

        var result = await synth.SynthesizeAsync("hello", "af_nicole", speed: null, CancellationToken.None);

        Assert.Equal("mp3", result.Format);
        Assert.Equal(new byte[] { 0xff, 0xfb, 0x90, 0x44 }, result.Audio);
        Assert.Equal("http://127.0.0.1:8880/v1/audio/speech", capturedUrl);

        var doc = JsonDocument.Parse(Encoding.UTF8.GetString(capturedBody!));
        Assert.Equal("kokoro", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("af_nicole", doc.RootElement.GetProperty("voice").GetString());
        Assert.Equal("hello", doc.RootElement.GetProperty("input").GetString());
        Assert.Equal("mp3", doc.RootElement.GetProperty("response_format").GetString());
        // speed must be omitted entirely when null so Kokoro applies its default.
        Assert.False(doc.RootElement.TryGetProperty("speed", out _));
    }

    [Fact]
    public async Task SynthesizeAsync_includes_speed_in_payload_when_set()
    {
        byte[]? capturedBody = null;
        var handler = new StubHandler(async req =>
        {
            capturedBody = await req.Content!.ReadAsByteArrayAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0xff]),
            };
        });

        var synth = new KokoroSpeechSynthesizer(new HttpClient(handler), new Uri("http://127.0.0.1:8880"));
        await synth.SynthesizeAsync("hello", "af_nicole", speed: 1.15, CancellationToken.None);

        var doc = JsonDocument.Parse(Encoding.UTF8.GetString(capturedBody!));
        Assert.Equal(1.15, doc.RootElement.GetProperty("speed").GetDouble());
    }

    [Fact]
    public async Task SynthesizeAsync_throws_on_non_2xx()
    {
        var handler = new StubHandler(req => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("unknown voice"),
        }));

        var synth = new KokoroSpeechSynthesizer(new HttpClient(handler), new Uri("http://127.0.0.1:8880"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => synth.SynthesizeAsync("hi", "bad_voice", speed: null, CancellationToken.None));
        Assert.Contains("400", ex.Message);
    }

    [Fact]
    public async Task ListVoicesAsync_returns_empty_when_unreachable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
        var synth = new KokoroSpeechSynthesizer(new HttpClient(handler), new Uri("http://127.0.0.1:8880"));
        var voices = await synth.ListVoicesAsync(CancellationToken.None);
        Assert.Empty(voices);
    }

    [Fact]
    public async Task ListVoicesAsync_parses_wrapped_string_array()
    {
        // Tolerance shape: { "voices": ["af_nicole", ...] }
        var json = """{"voices":["af_nicole","af_bella","am_michael"]}""";
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

        var synth = new KokoroSpeechSynthesizer(new HttpClient(handler), new Uri("http://127.0.0.1:8880"));
        var voices = await synth.ListVoicesAsync(CancellationToken.None);
        Assert.Equal(new[] { "af_nicole", "af_bella", "am_michael" }, voices);
    }

    [Fact]
    public async Task ListVoicesAsync_parses_kokoro_object_array()
    {
        // Actual kokoro-fastapi 1.x shape: { "voices": [{"id": "...", "name": "..."}] }
        var json = """{"voices":[{"id":"af_alloy","name":"af_alloy"},{"id":"af_nicole","name":"af_nicole"}]}""";
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

        var synth = new KokoroSpeechSynthesizer(new HttpClient(handler), new Uri("http://127.0.0.1:8880"));
        var voices = await synth.ListVoicesAsync(CancellationToken.None);
        Assert.Equal(new[] { "af_alloy", "af_nicole" }, voices);
    }

    [Fact]
    public async Task ListVoicesAsync_parses_bare_string_array()
    {
        // Older / minimal shape: top-level array of strings.
        var json = """["af_one","af_two"]""";
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

        var synth = new KokoroSpeechSynthesizer(new HttpClient(handler), new Uri("http://127.0.0.1:8880"));
        var voices = await synth.ListVoicesAsync(CancellationToken.None);
        Assert.Equal(new[] { "af_one", "af_two" }, voices);
    }

    [Fact]
    public async Task ListVoicesAsync_returns_empty_on_unknown_shape()
    {
        var json = """{"unexpected":"shape"}""";
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

        var synth = new KokoroSpeechSynthesizer(new HttpClient(handler), new Uri("http://127.0.0.1:8880"));
        var voices = await synth.ListVoicesAsync(CancellationToken.None);
        Assert.Empty(voices);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => handler(request);
    }
}
