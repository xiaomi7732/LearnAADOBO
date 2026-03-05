using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);

var azureAdConfig = builder.Configuration.GetSection("AzureAd");
var clientId = azureAdConfig["ClientId"]!;

// ── Validate incoming bearer tokens ──
// This ensures callers present a valid Azure AD token scoped to this API.
// (Separate concern from OBO — this is about authenticating the CALLER.)
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(azureAdConfig);

// ── Build the MSAL ConfidentialClientApplication ──
// This object represents the MiddleTierApi's own identity.
// "Confidential client" means the app has a credential it can use to prove its identity
// to Azure AD. Instead of a client secret, we use a certificate:
//   - The PRIVATE key (in the .pfx file) is used to sign a JWT client assertion
//   - Azure AD verifies the signature using the PUBLIC key (.cer) uploaded to the app registration
// This is more secure than a client secret because the private key never leaves this machine.
//
// With SNI (Subject Name and Issuer) authentication, you don't upload the certificate
// directly to the app registration (which would trigger credential lifetime policies).
// Instead, you configure the app registration to trust certificates by their subject name
// and issuer. MSAL sends the full certificate chain (x5c) in the JWT client assertion,
// and Azure AD validates it against the configured trust — no credential upload needed.
var tenantId = azureAdConfig["TenantId"]!;
var certPath = azureAdConfig["CertificatePath"]!;
var certPassword = azureAdConfig["CertificatePassword"]!;
var authority = $"{azureAdConfig["Instance"]}{tenantId}/v2.0";

// Load the certificate from a .pfx file (contains both public and private key).
var certificate = X509CertificateLoader.LoadPkcs12FromFile(certPath, certPassword);

IConfidentialClientApplication confidentialClient = ConfidentialClientApplicationBuilder
    .Create(clientId)                              // MiddleTierApi's application (client) ID
    .WithCertificate(certificate, sendX5C: true)   // sendX5C: true → sends the certificate chain
                                                   // in the x5c header of the JWT client assertion,
                                                   // enabling SNI-based authentication
    .WithAuthority(new Uri(authority))             // Azure AD tenant + version (e.g. .../v2.0)
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
    //   grant_type            = urn:ietf:params:oauth:grant-type:jwt-bearer
    //   client_id             = <MiddleTierApi client ID>        ← app identity
    //   client_assertion_type = urn:ietf:params:oauth:client-assertion-type:jwt-bearer
    //   client_assertion      = <JWT signed with the certificate's private key>  ← app credential
    //   assertion             = <Token A from caller>            ← user identity
    //   scope                 = api://<DownstreamApi>/.default   ← what we want access to
    //   requested_token_use   = on_behalf_of                     ← the OBO grant type
    //
    // With a certificate, MSAL builds a short-lived JWT (the "client assertion"), signs it
    // with the private key from the .pfx, and sends that instead of a plain client_secret.
    // Azure AD verifies the signature using the public key (.cer) uploaded to the app registration.
    //
    // Azure AD validates BOTH identities before issuing Token B:
    //   1. App identity  (client_id + client_assertion) — is this a registered, legitimate app?
    //   2. User identity (assertion / Token A)          — is this a valid user token?
    //      It also checks: is MiddleTierApi authorized to request tokens for DownstreamApi
    //      on behalf of this user? (configured via API permissions + admin consent)
    //
    // If everything checks out, Azure AD returns Token B — a new access token scoped to
    // DownstreamApi, still carrying the original user's identity (name, oid, etc.).
    // MSAL caches Token B internally so repeated calls reuse it until expiry.
    string accessToken;
    try
    {
        var result = await confidentialClient
            .AcquireTokenOnBehalfOf(downstreamScopes, userAssertion)
            .ExecuteAsync();

        accessToken = result.AccessToken;
    }
    catch (MsalServiceException ex) when (ex.ErrorCode.Contains("AADSTS7000215"))
    {
        // Invalid credential — the certificate's public key doesn't match what's uploaded in Azure AD.
        return Results.Json(new
        {
            Error = "APP_CREDENTIAL_INVALID",
            Detail = "The MiddleTierApi's certificate is not recognized by Azure AD. "
                + "Go to Azure Portal → App registrations → MiddleTierApi → "
                + "Certificates & secrets → Certificates, and upload the .cer file.",
            AzureAdCode = ex.ErrorCode
        }, statusCode: 502);
    }
    catch (MsalServiceException ex) when (ex.ErrorCode.Contains("AADSTS7000222"))
    {
        // Expired client secret.
        return Results.Json(new
        {
            Error = "APP_CREDENTIAL_EXPIRED",
            Detail = "The MiddleTierApi's certificate has expired. "
                + "Generate a new self-signed certificate, update the .pfx file, "
                + "and upload the new .cer to Azure Portal → App registrations → MiddleTierApi → "
                + "Certificates & secrets.",
            AzureAdCode = ex.ErrorCode
        }, statusCode: 502);
    }
    catch (MsalServiceException ex) when (ex.ErrorCode.Contains("AADSTS700027"))
    {
        // Client assertion signature failure — certificate mismatch or corruption.
        return Results.Json(new
        {
            Error = "APP_CREDENTIAL_SIGNATURE_MISMATCH",
            Detail = "The certificate used to sign the client assertion doesn't match "
                + "the public key uploaded to Azure AD. Re-upload the .cer file or regenerate the certificate.",
            AzureAdCode = ex.ErrorCode
        }, statusCode: 502);
    }
    catch (MsalServiceException ex) when (ex.ErrorCode.Contains("AADSTS50013"))
    {
        // Assertion (the user's token) failed validation — expired, bad signature, wrong issuer.
        return Results.Json(new
        {
            Error = "USER_TOKEN_INVALID",
            Detail = "The incoming user token (Token A) failed validation at Azure AD. "
                + "This usually means the token is expired or was issued by an untrusted authority. "
                + "Have the client sign in again to get a fresh token.",
            AzureAdCode = ex.ErrorCode
        }, statusCode: 401);
    }
    catch (MsalServiceException ex) when (ex.ErrorCode.Contains("AADSTS65001"))
    {
        // Admin consent not granted for the requested scopes.
        return Results.Json(new
        {
            Error = "CONSENT_REQUIRED",
            Detail = "Admin consent has not been granted for MiddleTierApi to call DownstreamApi "
                + "on behalf of this user. Go to Azure Portal → App registrations → MiddleTierApi → "
                + "API permissions → Grant admin consent.",
            AzureAdCode = ex.ErrorCode
        }, statusCode: 403);
    }
    catch (MsalServiceException ex) when (
        ex.ErrorCode.Contains("AADSTS500011") || ex.ErrorCode.Contains("AADSTS70011"))
    {
        // Resource not found or invalid scope — downstream API not registered or scopes misconfigured.
        return Results.Json(new
        {
            Error = "SCOPE_OR_RESOURCE_INVALID",
            Detail = "The requested scope or resource doesn't exist in Azure AD. "
                + "Check that DownstreamApi:Scopes in appsettings.json matches the "
                + "Expose an API → Application ID URI of the DownstreamApi app registration.",
            RequestedScopes = downstreamScopes,
            AzureAdCode = ex.ErrorCode
        }, statusCode: 502);
    }
    catch (MsalServiceException ex)
    {
        // Catch-all for other Azure AD errors.
        return Results.Json(new
        {
            Error = "OBO_TOKEN_EXCHANGE_FAILED",
            Detail = "Azure AD rejected the OBO token exchange. See AzureAdCode and Message for details.",
            AzureAdCode = ex.ErrorCode,
            Message = ex.Message
        }, statusCode: 502);
    }

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
