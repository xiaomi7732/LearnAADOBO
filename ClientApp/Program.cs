using System.Net.Http.Headers;
using Microsoft.Identity.Client;

// ═══════════════════════════════════════════════════════════════
//  ClientApp — Interactive login, then call MiddleTierApi
// ═══════════════════════════════════════════════════════════════

// ── Configuration (replace with your Azure AD app registration values) ──
const string tenantId = "YOUR_TENANT_ID";
const string clientAppClientId = "YOUR_CLIENT_APP_CLIENT_ID";
const string middleTierApiScope = "api://YOUR_MIDDLETIER_API_CLIENT_ID/access_as_user";
const string middleTierApiBaseUrl = "https://localhost:7213";

// ── Build the MSAL public client application ──
var app = PublicClientApplicationBuilder
    .Create(clientAppClientId)
    .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
    .WithRedirectUri("http://localhost") // for interactive browser login
    .Build();

// ── Acquire token interactively ──
Console.WriteLine("Signing in... A browser window will open for authentication.\n");

AuthenticationResult result;
try
{
    // Try to get a cached token first
    var accounts = await app.GetAccountsAsync().ConfigureAwait(false);
    result = await app
        .AcquireTokenSilent(new[] { middleTierApiScope }, accounts.FirstOrDefault())
        .ExecuteAsync()
        .ConfigureAwait(false);
}
catch (MsalUiRequiredException)
{
    // No cached token — launch interactive browser login
    result = await app
        .AcquireTokenInteractive(new[] { middleTierApiScope })
        .WithPrompt(Prompt.SelectAccount)
        .ExecuteAsync()
        .ConfigureAwait(false);
}

Console.WriteLine($"✓ Signed in as: {result.Account.Username}");
Console.WriteLine($"✓ Token scoped to: {string.Join(", ", result.Scopes)}");
Console.WriteLine($"✓ Token expires at: {result.ExpiresOn:HH:mm:ss}\n");

// ── Call the MiddleTierApi with the acquired token ──
Console.WriteLine("Calling MiddleTierApi /api/weather-aggregator ...\n");

using var httpClient = new HttpClient(new HttpClientHandler
{
    // Allow self-signed dev certs for localhost
    ServerCertificateCustomValidationCallback = (_, _, _, _) => true
});

httpClient.DefaultRequestHeaders.Authorization =
    new AuthenticationHeaderValue("Bearer", result.AccessToken);

var response = await httpClient.GetAsync($"{middleTierApiBaseUrl}/api/weather-aggregator");

if (response.IsSuccessStatusCode)
{
    var body = await response.Content.ReadAsStringAsync();
    Console.WriteLine("✓ Response from MiddleTierApi (which called DownstreamApi via OBO):\n");
    // Pretty-print JSON
    var json = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(json, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}
else
{
    Console.WriteLine($"✗ Request failed: {response.StatusCode}");
    var error = await response.Content.ReadAsStringAsync();
    Console.WriteLine(error);
}

Console.WriteLine("\nDone. Press any key to exit.");
Console.ReadKey();
