using System.Text;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using PSWSMan.Authentication.Native;
using PSWSMan.Connection;
using PSWSMan.Lib;

namespace cntremotepwshell;

/// <summary>
/// Sends WSMan envelopes over HTTP(S).
/// Authentication is Devolutions SSPI Negotiate, so Linux and Windows share the same path.
/// </summary>
internal sealed class WMRemoteClient : IDisposable
{
    public const string ContentType = "application/soap+xml;charset=UTF-8";

    private readonly HttpClient _http;
    private readonly PSWSMan.Authentication.WSManCredential _credential;
    private X509Certificate2? _serverCertificate;
    private bool _disposed;

    public Uri ConnectionUri { get; }

    public WMRemoteClient(
        Uri connectionUri,
        string userName,
        string password,
        bool skipCertificateCheck = false,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(connectionUri);
        ArgumentException.ThrowIfNullOrEmpty(password);

        ConnectionUri = connectionUri;
        _http = CreateHttpClient(connectionUri, skipCertificateCheck, timeout, CaptureServerCertificate);

        if (!ProviderLibs.TryGetDevolutionsSspi(out var provider, out var error))
            throw new InvalidOperationException("Devolutions SSPI could not be loaded.", error);

        var options = new PSWSMan.Authentication.NegotiateOptions
        {
            SPNService = "WSMAN",
            SPNHostName = connectionUri.Host,
        };
        _credential = new PSWSMan.Authentication.SspiCredential(
            provider,
            string.IsNullOrEmpty(userName) ? null : userName,
            password,
            PSWSMan.Authentication.NegotiateMethod.Negotiate,
            options);
    }

    public async Task<byte[]> SendAsync(WSManRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var auth = _credential.CreateAuthContext(_serverCertificate);

        byte[]? input = [];
        for (var round = 0; round < 4; round++)
        {
            using var message = CreateMessage(request, auth, auth.Step(input));
            using HttpResponseMessage response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
                return await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

            input = ReadChallenge(response, auth.HttpAuthLabel);
            if (input is null)
                throw new WSManException("WinRM HTTP 401 did not include a " + auth.HttpAuthLabel + " challenge.");
        }

        throw new WSManException("WinRM Negotiate did not complete.");
    }

    public async Task<T> SendAsync<T>(WSManRequest request, CancellationToken cancellationToken = default)
        where T : IWSManPayload<T>
    {
        byte[] body = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return T.Parse(body, request.MessageId);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _credential.Dispose();
        _serverCertificate?.Dispose();
        _http.Dispose();
    }

    private HttpRequestMessage CreateMessage(WSManRequest request, IWSManAuthenticationContext auth, byte[]? token)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, ConnectionUri);
        message.Content = new ByteArrayContent(request.Content);
        message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType);
        if (token is not null)
            message.Headers.Authorization = new AuthenticationHeaderValue(auth.HttpAuthLabel, Convert.ToBase64String(token));
        return message;
    }

    private static byte[]? ReadChallenge(HttpResponseMessage response, string scheme)
    {
        foreach (var header in response.Headers.WwwAuthenticate)
        {
            if (!header.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase) || header.Parameter is null)
                continue;

            try
            {
                return Convert.FromBase64String(header.Parameter);
            }
            catch (FormatException)
            {
                continue;
            }
        }

        return null;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var headers = string.Join("; ", response.Headers.Select(h => $"{h.Key}={string.Join(",", h.Value)}"));
            var textBody = Encoding.UTF8.GetString(body);
            throw new WSManException(
                $"WinRM HTTP {(int)response.StatusCode} {response.ReasonPhrase} bodyLen={body.Length} {headers} body={textBody}");
        }

        return body;
    }

    private static HttpClient CreateHttpClient(
        Uri connectionUri,
        bool skipCertificateCheck,
        TimeSpan? timeout,
        Action<X509Certificate> captureCertificate)
    {
        HttpClientHandler handler = new()
        {
            PreAuthenticate = false,
            UseDefaultCredentials = false,
            UseProxy = false,
        };

        handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
        {
            if (certificate is not null)
                captureCertificate(certificate);
            return skipCertificateCheck || errors == SslPolicyErrors.None;
        };

        HttpClient http = new(handler, disposeHandler: true);
        if (timeout is not null)
            http.Timeout = timeout.Value;

        return http;
    }

    private void CaptureServerCertificate(X509Certificate certificate)
    {
        var captured = new X509Certificate2(certificate);
        var previous = Interlocked.Exchange(ref _serverCertificate, captured);
        previous?.Dispose();
    }
}
