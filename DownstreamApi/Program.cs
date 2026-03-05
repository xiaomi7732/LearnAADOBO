using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

// ── Azure AD authentication ──
// This API trusts tokens issued by Azure AD for the "DownstreamApi" app registration.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// ── Protected endpoint ──
// Only callers with a valid Azure AD token scoped to this API can reach here.
var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/api/weather", (HttpContext ctx) =>
{
    var user = ctx.User.Identity?.Name ?? ctx.User.FindFirst("preferred_username")?.Value ?? "unknown";

    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast(
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        )).ToArray();

    return Results.Ok(new
    {
        CalledBy = user,
        Message = "Hello from the Downstream API! This data was fetched ON BEHALF OF the user.",
        Forecast = forecast
    });
})
.RequireAuthorization()
.WithName("GetWeather");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
