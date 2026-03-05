using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

var azureAdConfig = builder.Configuration.GetSection("AzureAd");

// ── Validate incoming bearer tokens ──
// This ensures callers present a valid Azure AD token scoped to this API.
// (Separate concern from OBO — this is about authenticating the CALLER.)
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(azureAdConfig);

// ── Build the MSAL ConfidentialClientApplication ──
// This object represents the MiddleTierApi's own identity.
// "Confidential client" means the app has a credential (client secret) it can use to
// prove to Azure AD that it really is the MiddleTierApi — not some impersonator.
// Both the app identity (client_id + client_secret) and the user identity (the incoming
// token) are required before Azure AD will issue an OBO token.
var tenantId = azureAdConfig["TenantId"]!;
var clientId = azureAdConfig["ClientId"]!;
var clientSecret = azureAdConfig["ClientSecret"]!;
var authority = $"{azureAdConfig["Instance"]}{tenantId}/v2.0";

IConfidentialClientApplication confidentialClient = ConfidentialClientApplicationBuilder
    .Create(clientId)                      // MiddleTierApi's application (client) ID
    .WithClientSecret(clientSecret)        // MiddleTierApi's secret — proves the app's identity
    .WithAuthority(new Uri(authority))     // Azure AD tenant + version (e.g. .../v2.0)
    .Build();

builder.Services.AddSingleton(confidentialClient);
builder.Services.AddHttpClient("DownstreamApi");
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// Read downstream API config
var downstreamBaseUrl = builder.Configuration["DownstreamApi:BaseUrl"]!;
var downstreamScopes = builder.Configuration.GetSection("DownstreamApi:Scopes").Get<string[]>()!;

// ── OBO endpoint ──
app.MapGet("/api/weather-aggregator", async (
    IHttpClientFactory httpClientFactory,
    HttpContext ctx) =>
{
    var user = ctx.User.Identity?.Name
        ?? ctx.User.FindFirst("preferred_username")?.Value
        ?? "unknown";

    // ── Step 1: Extract the incoming bearer token (Token A) ──
    // This is the user's token that the ClientApp sent, scoped to MiddleTierApi.
    // It has already been validated by the authentication middleware above.
    var incomingToken = ctx.Request.Headers.Authorization
        .ToString().Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase);

    // ── Step 2: OBO token exchange ──
    // UserAssertion wraps Token A — it represents "the user who called us."
    var userAssertion = new UserAssertion(incomingToken);

    // AcquireTokenOnBehalfOf sends this HTTP request to Azure AD:
    //
    //   POST https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token
    //   Content-Type: application/x-www-form-urlencoded
    //
    //   grant_type          = urn:ietf:params:oauth:grant-type:jwt-bearer
    //   client_id           = <MiddleTierApi client ID>        ← app identity
    //   client_secret       = <MiddleTierApi secret>           ← app credential
    //   assertion           = <Token A from caller>            ← user identity
    //   scope               = api://<DownstreamApi>/.default   ← what we want access to
    //   requested_token_use = on_behalf_of                     ← the OBO grant type
    //
    // Azure AD validates BOTH identities before issuing Token B:
    //   1. App identity  (client_id + client_secret) — is this a registered, legitimate app?
    //   2. User identity (assertion / Token A)       — is this a valid user token?
    //      It also checks: is MiddleTierApi authorized to request tokens for DownstreamApi
    //      on behalf of this user? (configured via API permissions + admin consent)
    //
    // If everything checks out, Azure AD returns Token B — a new access token scoped to
    // DownstreamApi, still carrying the original user's identity (name, oid, etc.).
    // MSAL caches Token B internally so repeated calls reuse it until expiry.
    var result = await confidentialClient
        .AcquireTokenOnBehalfOf(downstreamScopes, userAssertion)
        .ExecuteAsync();

    // result.AccessToken is Token B
    var accessToken = result.AccessToken;

    // ── Step 3: Call the downstream API with Token B ──
    var client = httpClientFactory.CreateClient("DownstreamApi");
    client.BaseAddress = new Uri(downstreamBaseUrl);
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", accessToken);

    var response = await client.GetAsync("/api/weather");
    response.EnsureSuccessStatusCode();
    var content = await response.Content.ReadAsStringAsync();

    return Results.Ok(new
    {
        CalledBy = user,
        Message = "MiddleTierApi received this from DownstreamApi via OBO:",
        DownstreamResponse = System.Text.Json.JsonSerializer.Deserialize<object>(content)
    });
})
.RequireAuthorization()
.WithName("WeatherAggregator");

app.Run();
