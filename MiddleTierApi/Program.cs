using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using Microsoft.Identity.Abstractions;

var builder = WebApplication.CreateBuilder(args);

// ── Azure AD authentication + OBO token acquisition ──
// AddMicrosoftIdentityWebApi  → validates incoming bearer tokens
// EnableTokenAcquisitionToCallDownstreamApi → enables the OBO flow
// AddDownstreamApi            → registers a named HTTP client for the downstream API
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"))
        .EnableTokenAcquisitionToCallDownstreamApi()
        .AddDownstreamApi("DownstreamApi", builder.Configuration.GetSection("DownstreamApi"))
        .AddInMemoryTokenCaches();

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// ── OBO endpoint ──
// 1. The client calls this endpoint with a token scoped to the MiddleTierApi.
// 2. This API uses OBO to exchange that token for a new one scoped to DownstreamApi.
// 3. It then calls the DownstreamApi and returns the result.
app.MapGet("/api/weather-aggregator", async (
    IDownstreamApi downstreamApi,
    HttpContext ctx) =>
{
    var user = ctx.User.Identity?.Name
        ?? ctx.User.FindFirst("preferred_username")?.Value
        ?? "unknown";

    // This call triggers the OBO flow under the hood:
    //   - Takes the incoming user token
    //   - Exchanges it at Azure AD for a new token scoped to DownstreamApi
    //   - Attaches the new token and calls GET /api/weather
    var response = await downstreamApi.CallApiForUserAsync(
        "DownstreamApi",
        options =>
        {
            options.HttpMethod = "GET";
            options.RelativePath = "api/weather";
        });

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
