using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

// ── Azure AD authentication + OBO token acquisition ──
// AddMicrosoftIdentityWebApi               → validates incoming bearer tokens
// EnableTokenAcquisitionToCallDownstreamApi → registers ITokenAcquisition, which performs the OBO
//                                             token exchange against Azure AD's token endpoint
// AddInMemoryTokenCaches                   → caches OBO tokens so repeated calls don't hit Azure AD
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"))
        .EnableTokenAcquisitionToCallDownstreamApi()
        .AddInMemoryTokenCaches();

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
// 1. The client calls this endpoint with Token A scoped to MiddleTierApi.
// 2. ITokenAcquisition.GetAccessTokenForUserAsync performs the OBO exchange:
//    - Sends Token A to Azure AD's /oauth2/v2.0/token endpoint
//    - grant_type = urn:ietf:params:oauth:grant-type:jwt-bearer
//    - assertion  = Token A (the incoming user token)
//    - scope      = the downstream API scopes (e.g. api://<downstream-client-id>/.default)
//    - Azure AD validates Token A, checks that MiddleTierApi is allowed to act on behalf of the user,
//      and returns Token B scoped to DownstreamApi.
// 3. We attach Token B to an HttpClient and call the DownstreamApi ourselves.
app.MapGet("/api/weather-aggregator", async (
    ITokenAcquisition tokenAcquisition,
    IHttpClientFactory httpClientFactory,
    HttpContext ctx) =>
{
    var user = ctx.User.Identity?.Name
        ?? ctx.User.FindFirst("preferred_username")?.Value
        ?? "unknown";

    // ── Step 1: OBO token exchange ──
    // Under the hood this sends a POST to:
    //   https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token
    // with:
    //   grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer
    //   client_id=<MiddleTierApi client ID>
    //   client_secret=<MiddleTierApi secret>
    //   assertion=<the incoming bearer token from the caller>
    //   scope=api://<DownstreamApi client ID>/.default
    //   requested_token_use=on_behalf_of
    //
    // Azure AD returns a NEW token (Token B) issued for DownstreamApi, carrying the original
    // user's identity. The token cache stores it so subsequent calls skip the round-trip.
    var accessToken = await tokenAcquisition.GetAccessTokenForUserAsync(
        downstreamScopes,
        user: ctx.User);

    // ── Step 2: Call the downstream API with the OBO token ──
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
