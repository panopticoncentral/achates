using System.Net.Http.Headers;
using System.Text.Json;
using Achates.Configuration;
using Microsoft.Identity.Client;

namespace Achates.Server.Graph;

/// <summary>
/// Microsoft Graph API client supporting both client credentials (work/school)
/// and device code (personal/work) authentication flows.
/// </summary>
public sealed class GraphClient
{
    private static readonly string[] ClientCredentialScopes = ["https://graph.microsoft.com/.default"];
    private static readonly string[] DelegatedScopes = ["Mail.Read", "Calendars.Read"];

    /// <summary>
    /// AsyncLocal callback for surfacing device code messages to the current caller
    /// (e.g. sending through the transport to the user's chat). Set per async flow
    /// so that shared GraphClient instances route to the correct peer.
    /// </summary>
    private static readonly AsyncLocal<Func<string, Task>?> DeviceCodeNotifier = new();

    /// <summary>
    /// Set a callback to receive device code sign-in messages in the current async flow.
    /// </summary>
    public static void SetDeviceCodeNotifier(Func<string, Task>? notifier) =>
        DeviceCodeNotifier.Value = notifier;

    private readonly IConfidentialClientApplication? _confidentialApp;
    private readonly IPublicClientApplication? _publicApp;
    private readonly HttpClient _httpClient;
    private readonly string _basePath;
    private readonly string? _userEmail;
    private readonly ILogger _logger;
    private string? _resolvedEmail;

    public GraphClient(GraphConfig config, HttpClient httpClient, ILogger logger, string? tokenCachePath = null)
    {
        _httpClient = httpClient;
        _logger = logger;

        var clientId = config.ClientId
            ?? throw new InvalidOperationException("Graph config requires client_id.");

        var clientSecret = config.ClientSecret
            ?? Environment.GetEnvironmentVariable("GRAPH_CLIENT_SECRET");

        if (clientSecret is not null)
        {
            // Client credentials flow (work/school)
            var tenantId = config.TenantId
                ?? throw new InvalidOperationException("Graph config requires tenant_id for client credentials flow.");
            _userEmail = config.UserEmail
                ?? throw new InvalidOperationException("Graph config requires user_email for client credentials flow.");

            _confidentialApp = ConfidentialClientApplicationBuilder
                .Create(clientId)
                .WithClientSecret(clientSecret)
                .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
                .Build();

            _basePath = $"https://graph.microsoft.com/v1.0/users/{_userEmail}";
        }
        else
        {
            // Device code flow (personal or work/school)
            var tenantId = config.TenantId ?? "consumers";

            _publicApp = PublicClientApplicationBuilder
                .Create(clientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
                .Build();

            // Persist token cache to disk so user doesn't re-auth on every restart
            var cachePath = tokenCachePath
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".achates", "graph-token-cache.bin");
            EnableTokenCache(_publicApp.UserTokenCache, cachePath);

            _basePath = "https://graph.microsoft.com/v1.0/me";
        }
    }

    /// <summary>
    /// GET a Graph API resource, deserializing the JSON response.
    /// The path is appended to the base path (/users/{email} or /me).
    /// </summary>
    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        var token = await AcquireTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_basePath}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await JsonSerializer.DeserializeAsync<T>(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            JsonOptions,
            cancellationToken) ?? throw new InvalidOperationException("Empty Graph API response.");
    }

    /// <summary>
    /// GET a Graph API resource with custom headers (e.g. ConsistencyLevel for $search).
    /// </summary>
    public async Task<T> GetAsync<T>(string path, Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        var token = await AcquireTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_basePath}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Prefer", "outlook.body-content-type=\"text\"");

        foreach (var (key, value) in headers)
            request.Headers.TryAddWithoutValidation(key, value);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await JsonSerializer.DeserializeAsync<T>(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            JsonOptions,
            cancellationToken) ?? throw new InvalidOperationException("Empty Graph API response.");
    }

    /// <summary>
    /// POST to a Graph API resource with a JSON body.
    /// </summary>
    public async Task<T> PostAsync<T>(string path, object body,
        CancellationToken cancellationToken = default)
    {
        var token = await AcquireTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_basePath}/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body, options: JsonOptions);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await JsonSerializer.DeserializeAsync<T>(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            JsonOptions,
            cancellationToken) ?? throw new InvalidOperationException("Empty Graph API response.");
    }

    /// <summary>
    /// Proactively acquire a token, triggering device code flow if needed.
    /// Call at startup so the sign-in prompt appears immediately.
    /// </summary>
    public async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default) =>
        await AcquireTokenAsync(cancellationToken);

    private async Task<string> AcquireTokenAsync(CancellationToken cancellationToken)
    {
        if (_confidentialApp is not null)
        {
            var result = await _confidentialApp.AcquireTokenForClient(ClientCredentialScopes)
                .ExecuteAsync(cancellationToken);
            return result.AccessToken;
        }

        if (_publicApp is not null)
        {
            // Try silent first (cached token)
            var accounts = await _publicApp.GetAccountsAsync();
            var account = accounts.FirstOrDefault();

            if (account is not null)
            {
                try
                {
                    var result = await _publicApp.AcquireTokenSilent(DelegatedScopes, account)
                        .ExecuteAsync(cancellationToken);
                    return result.AccessToken;
                }
                catch (MsalUiRequiredException)
                {
                    // Token expired and refresh failed — fall through to device code
                }
            }

            // Device code flow — notify the user via transport and log
            var dcResult = await _publicApp.AcquireTokenWithDeviceCode(DelegatedScopes, async callback =>
            {
                _logger.LogWarning("Graph sign-in required: {Message}", callback.Message);

                if (DeviceCodeNotifier.Value is { } notify)
                    await notify(callback.Message);
            }).ExecuteAsync(cancellationToken);

            return dcResult.AccessToken;
        }

        throw new InvalidOperationException("No MSAL application configured.");
    }

    /// <summary>
    /// Get the authenticated user's email address.
    /// For client credentials, returns the configured user_email.
    /// For delegated, fetches from /me and caches the result.
    /// </summary>
    public async Task<string> GetUserEmailAsync(CancellationToken cancellationToken = default)
    {
        if (_userEmail is not null)
            return _userEmail;

        if (_resolvedEmail is not null)
            return _resolvedEmail;

        var profile = await GetAsync<JsonElement>("?$select=mail,userPrincipalName", cancellationToken);
        _resolvedEmail = profile.TryGetProperty("mail", out var mail) && mail.ValueKind == JsonValueKind.String
            ? mail.GetString()
            : profile.TryGetProperty("userPrincipalName", out var upn) && upn.ValueKind == JsonValueKind.String
                ? upn.GetString()
                : null;

        return _resolvedEmail
            ?? throw new InvalidOperationException("Could not resolve user email from Graph /me profile.");
    }

    private static void EnableTokenCache(ITokenCache cache, string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        cache.SetBeforeAccess(args =>
        {
            if (File.Exists(filePath))
            {
                var data = File.ReadAllBytes(filePath);
                args.TokenCache.DeserializeMsalV3(data);
            }
        });

        cache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged)
            {
                var data = args.TokenCache.SerializeMsalV3();
                File.WriteAllBytes(filePath, data);
            }
        });
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Graph API error {(int)response.StatusCode}: {body}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };
}
