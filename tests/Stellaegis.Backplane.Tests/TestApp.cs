using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stellaegis.Backplane.Tests;

/// <summary>
/// An ASP.NET Core application on a <see cref="TestServer"/>, with an authentication scheme reading the request
/// headers: each <c>X-Claim-{type}</c> header becomes a claim of that type, and the user is authenticated as soon
/// as one is present. The authentication of the host, which the hub runs under.
/// </summary>
internal static class TestApp
{
    public const string ClaimHeaderPrefix = "X-Claim-";
    private const string SchemeName = "headers";

    public static async Task<WebApplication> StartAsync(Action<IServiceCollection> services, Action<WebApplication> map)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(SchemeName).AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(SchemeName, null);
        builder.Services.AddAuthorization();
        services(builder.Services);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = Request.Headers
                .Where(header => header.Key.StartsWith(ClaimHeaderPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(header => new Claim(header.Key[ClaimHeaderPrefix.Length..], header.Value.ToString()))
                .ToList();
            return Task.FromResult(claims.Count == 0
                ? AuthenticateResult.NoResult()
                : AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
        }
    }
}
