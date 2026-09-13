using System.Net;

namespace WebsitePreview;

public sealed class FixtureProxyHandler(Uri origin) : DelegatingHandler(new HttpClientHandler {
    AllowAutoRedirect = false,
    UseProxy = false,
    UseCookies = false,
}) {
    private readonly CookieContainer cookies = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        if (request.RequestUri is not Uri uri || uri.Scheme != Uri.UriSchemeHttp ||
            uri.Host != "127.0.0.1" || uri.Host != origin.Host || uri.Port != origin.Port) {
            throw new InvalidOperationException("The fixture client may only contact its own loopback host.");
        }

        // Model the existing proxy leg, including forwarding Secure cookies only for HTTPS requests.
        bool isHttps = request.Headers.TryGetValues("X-Forwarded-Proto", out IEnumerable<string>? schemes) && schemes.Single() == "https";
        Uri externalUri = new UriBuilder(uri) { Scheme = isHttps ? Uri.UriSchemeHttps : Uri.UriSchemeHttp }.Uri;
        string cookieHeader = cookies.GetCookieHeader(externalUri);
        if (cookieHeader.Length > 0) {
            request.Headers.Add("Cookie", cookieHeader);
        }
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)) {
            foreach (string value in values) {
                cookies.SetCookies(externalUri, value);
            }
        }
        return response;
    }
}
