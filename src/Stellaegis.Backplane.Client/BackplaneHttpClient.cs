using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Stellaegis.Primitives;

namespace Stellaegis.Backplane.Client;

/// <summary>The HTTP routes of the hub's host, as the client reaches them when its connection is down, and for replay.</summary>
public interface IBackplaneEndpoints
{
    /// <summary>Publishes <paramref name="envelope"/> through <c>POST events</c>.</summary>
    Task PublishAsync(Envelope envelope, CancellationToken cancellationToken = default);

    /// <summary>Reads, through <c>GET events</c>, what was published since <paramref name="since"/> to <paramref name="recipients"/> and to everyone.</summary>
    Task<IReadOnlyList<Envelope>> ReplayAsync(DateTimeOffset since, IReadOnlyCollection<Recipient> recipients, CancellationToken cancellationToken = default);
}

/// <summary>The <see cref="IBackplaneEndpoints"/> over an <see cref="HttpClient"/> at the hub's host, presenting the token of the host.</summary>
public sealed class BackplaneHttpClient(HttpClient http, IOptions<BackplaneClientOptions> options, IServiceProvider services) : IBackplaneEndpoints
{
    private readonly HttpClient _http = Guard.NotNull(http);
    private readonly string _path = Guard.NotNull(options).Value.EndpointsPath.Trim('/');
    private readonly IBackplaneTokenProvider? _tokens = Guard.NotNull(services).GetService(typeof(IBackplaneTokenProvider)) as IBackplaneTokenProvider;

    /// <inheritdoc />
    public async Task PublishAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(envelope);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_path}/events") { Content = JsonContent.Create(envelope) };
        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Envelope>> ReplayAsync(DateTimeOffset since, IReadOnlyCollection<Recipient> recipients, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(recipients);
        var query = $"since={Uri.EscapeDataString(since.ToString("O"))}&recipients={Uri.EscapeDataString(string.Join(',', recipients.Select(recipient => recipient.Segment)))}";
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_path}/events?{query}");
        await AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<Envelope>>(cancellationToken).ConfigureAwait(false) ?? [];
    }

    private async Task AuthorizeAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_tokens is not null && await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false) is { } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
