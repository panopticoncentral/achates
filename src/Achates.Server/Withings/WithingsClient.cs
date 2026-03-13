using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Achates.Configuration;

namespace Achates.Server.Withings;

/// <summary>
/// Withings Health API client with OAuth 2.0 authorization code flow.
/// Withings layers a nonce/HMAC-SHA256 signature mechanism on top of standard OAuth.
/// Tokens are persisted to disk so the user only authorizes once.
/// </summary>
public sealed class WithingsClient
{
    private const string AuthUrl = "https://account.withings.com/oauth2_user/authorize2";
    private const string TokenUrl = "https://wbsapi.withings.net/v2/oauth2";
    private const string SignatureUrl = "https://wbsapi.withings.net/v2/signature";
    private const string BaseUrl = "https://wbsapi.withings.net";

    // All health-related scopes
    private const string Scopes = "user.info,user.metrics,user.activity,user.sleepevents";

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _redirectUri;
    private readonly string _tokenPath;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _tokenExpiry;

    public bool IsAuthorized => _refreshToken is not null;

    public WithingsClient(WithingsConfig config, HttpClient httpClient, ILogger logger, string? tokenPath = null)
    {
        _clientId = config.ClientId
            ?? throw new InvalidOperationException("Withings config requires client_id.");
        _clientSecret = config.ClientSecret
            ?? Environment.GetEnvironmentVariable("WITHINGS_CLIENT_SECRET")
            ?? throw new InvalidOperationException("Withings config requires client_secret.");
        _redirectUri = config.RedirectUri ?? "http://localhost:5000/withings/callback";
        _httpClient = httpClient;
        _logger = logger;
        _tokenPath = tokenPath
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".achates", "withings-tokens.json");

        LoadTokens();
    }

    /// <summary>
    /// Generate the authorization URL for the user to visit.
    /// </summary>
    public string GetAuthorizationUrl(string state = "achates")
    {
        return $"{AuthUrl}?response_type=code&client_id={_clientId}&redirect_uri={Uri.EscapeDataString(_redirectUri)}&scope={Uri.EscapeDataString(Scopes)}&state={state}";
    }

    /// <summary>
    /// Exchange an authorization code for access and refresh tokens.
    /// Called from the OAuth callback endpoint.
    /// </summary>
    public async Task ExchangeCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var nonce = await GetNonceAsync(cancellationToken);

        // Signature is computed over action, client_id, nonce only (sorted by key)
        var signature = ComputeSignature("requesttoken", _clientId, nonce);

        var parameters = new SortedDictionary<string, string>
        {
            ["action"] = "requesttoken",
            ["client_id"] = _clientId,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["nonce"] = nonce,
            ["redirect_uri"] = _redirectUri,
            ["signature"] = signature,
        };

        var response = await PostFormAsync(TokenUrl, parameters, cancellationToken);
        ParseTokenResponse(response);
        SaveTokens();

        _logger.LogInformation("Withings authorization successful");
    }

    /// <summary>
    /// POST to a Withings API endpoint with form-encoded parameters.
    /// Automatically handles token refresh. Data endpoints use Bearer auth, not signatures.
    /// </summary>
    public async Task<JsonElement> ApiAsync(string path, Dictionary<string, string> parameters,
        CancellationToken cancellationToken = default)
    {
        await EnsureTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        request.Content = new FormUrlEncodedContent(parameters);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var status = root.GetProperty("status").GetInt32();
        if (status != 0)
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            throw new HttpRequestException($"Withings API error (status {status}): {error ?? json}");
        }

        return root.GetProperty("body").Clone();
    }

    private async Task EnsureTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _tokenExpiry.AddMinutes(-5))
            return;

        if (_refreshToken is null)
            throw new InvalidOperationException("Not authorized. Visit the authorization URL first.");

        await RefreshTokenAsync(cancellationToken);
    }

    private async Task RefreshTokenAsync(CancellationToken cancellationToken)
    {
        var nonce = await GetNonceAsync(cancellationToken);

        var signature = ComputeSignature("requesttoken", _clientId, nonce);

        var parameters = new SortedDictionary<string, string>
        {
            ["action"] = "requesttoken",
            ["client_id"] = _clientId,
            ["grant_type"] = "refresh_token",
            ["nonce"] = nonce,
            ["refresh_token"] = _refreshToken!,
            ["signature"] = signature,
        };

        try
        {
            var response = await PostFormAsync(TokenUrl, parameters, cancellationToken);
            ParseTokenResponse(response);
            SaveTokens();
            _logger.LogDebug("Withings token refreshed");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Withings token refresh failed — re-authorization may be needed");
            _accessToken = null;
            _refreshToken = null;
            SaveTokens();
            throw new InvalidOperationException("Withings token refresh failed. Re-authorization needed.", ex);
        }
    }

    /// <summary>
    /// Get a single-use nonce from the Withings signature endpoint.
    /// Required for all signed requests (token exchange, refresh).
    /// </summary>
    private async Task<string> GetNonceAsync(CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        // Signature over action, client_id, timestamp (sorted by key)
        var signature = ComputeSignature("getnonce", _clientId, timestamp);

        var parameters = new SortedDictionary<string, string>
        {
            ["action"] = "getnonce",
            ["client_id"] = _clientId,
            ["signature"] = signature,
            ["timestamp"] = timestamp,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, SignatureUrl);
        request.Content = new FormUrlEncodedContent(parameters);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var status = root.GetProperty("status").GetInt32();
        if (status != 0)
            throw new HttpRequestException($"Withings nonce error (status {status}): {json}");

        return root.GetProperty("body").GetProperty("nonce").GetString()
            ?? throw new InvalidOperationException("Nonce response missing nonce value.");
    }

    /// <summary>
    /// Compute HMAC-SHA256 signature over the signing fields (action, client_id, and
    /// nonce or timestamp), sorted by key, values joined with commas.
    /// </summary>
    private string ComputeSignature(params string[] values)
    {
        var message = string.Join(",", values);
        var keyBytes = Encoding.UTF8.GetBytes(_clientSecret);
        var messageBytes = Encoding.UTF8.GetBytes(message);
        var hash = HMACSHA256.HashData(keyBytes, messageBytes);
        return Convert.ToHexStringLower(hash);
    }

    private async Task<JsonElement> PostFormAsync(string url, SortedDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new FormUrlEncodedContent(parameters);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var status = root.GetProperty("status").GetInt32();
        if (status != 0)
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            throw new HttpRequestException($"Withings OAuth error (status {status}): {error ?? json}");
        }

        return root.GetProperty("body").Clone();
    }

    private void ParseTokenResponse(JsonElement body)
    {
        _accessToken = body.GetProperty("access_token").GetString();
        _refreshToken = body.GetProperty("refresh_token").GetString();
        var expiresIn = body.GetProperty("expires_in").GetInt32();
        _tokenExpiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
    }

    private void LoadTokens()
    {
        if (!File.Exists(_tokenPath))
            return;

        try
        {
            var json = File.ReadAllText(_tokenPath);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            _accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            _refreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            _tokenExpiry = root.TryGetProperty("token_expiry", out var te)
                ? DateTimeOffset.Parse(te.GetString()!)
                : DateTimeOffset.MinValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Withings tokens from {Path}", _tokenPath);
        }
    }

    private void SaveTokens()
    {
        var dir = Path.GetDirectoryName(_tokenPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        var data = new
        {
            access_token = _accessToken,
            refresh_token = _refreshToken,
            token_expiry = _tokenExpiry.ToString("o"),
        };

        File.WriteAllText(_tokenPath, JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
    }
}
