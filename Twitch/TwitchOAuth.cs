using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace rans0m;

/// <summary>
/// Runs Twitch's authorization code flow against a throwaway loopback listener, so the user can
/// approve the app in their normal browser and this process picks the code up automatically.
/// </summary>
/// <remarks>
/// Uses a raw <see cref="TcpListener"/> rather than <see cref="HttpListener"/> on purpose:
/// HttpListener refuses to bind prefixes it has no URL ACL for unless the process is elevated, and
/// asking for admin rights to approve a stream overlay is a bad trade. A socket on the loopback
/// adapter has no such requirement.
/// <para>
/// Twitch only supports static redirect URIs and compares them as exact strings, so the URI sent in
/// the authorization request must be character-for-character identical to what is listed under
/// OAuth Redirect URLs in the developer console. That registered value lives in
/// <see cref="TwitchSettings.RedirectUri"/> and is not assumed to be any particular string; the
/// listener simply binds whatever host and port it names.
/// </para>
/// </remarks>
public static class TwitchOAuth
{
    /// <summary>
    /// Suggested redirect URI, and the default in <see cref="TwitchSettings"/>. Copy it verbatim into
    /// the Twitch console's OAuth Redirect URLs list.
    /// </summary>
    public const string RedirectUri = "http://localhost:3000/";

    /// <summary>How long to wait for the user to finish approving before giving up.</summary>
    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(5);

    private const string SuccessPage =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>RANS0M</title></head>"
        + "<body style=\"font-family:Segoe UI,sans-serif;background:#230063;color:#fff;"
        + "display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">"
        + "<div style=\"text-align:center\"><h1>Connected</h1>"
        + "<p>You can close this tab and go back to RANS0M.</p></div></body></html>";

    private const string FailureTemplate =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>RANS0M</title></head>"
        + "<body style=\"font-family:Segoe UI,sans-serif;background:#230063;color:#fff;"
        + "display:flex;align-items:center;justify-content:center;height:100vh;margin:0\">"
        + "<div style=\"text-align:center\"><h1>Failed</h1>"
        + "<p style=\"max-width:32em\">{detail}</p></div></body></html>";

    /// <summary>Fills in the failure page, escaping the reason so it is safe to echo into HTML.</summary>
    private static string BuildFailurePage(string? detail)
    {
        return FailureTemplate.Replace("{detail}", System.Net.WebUtility.HtmlEncode(detail ?? string.Empty));
    }

    /// <summary>
    /// Binds a loopback listener matching the given redirect URI and returns it with the URI itself,
    /// which must be passed to <see cref="WaitForCodeAsync"/>.
    /// </summary>
    /// <remarks>
    /// The host and port come from <paramref name="redirectUri"/> so that whatever the user
    /// registered in the Twitch console is what actually gets bound. Twitch compares the URI as a
    /// literal string, so a mismatch is a hard failure with <c>redirect_mismatch</c>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The URI is unusable or the port is in use.</exception>
    public static (TcpListener Listener, string RedirectUri) CreateListener(string redirectUri)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out Uri? parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"'{redirectUri}' is not a valid http redirect URI. Copy the OAuth Redirect URL from the "
                + "Twitch developer console into this box.");
        }

        // Only loopback hosts can be served by this app; Twitch requires HTTPS for anything else,
        // which a local listener cannot present.
        bool isLoopback = parsed.IsLoopback
            || string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase);

        if (!isLoopback)
        {
            throw new InvalidOperationException(
                $"'{redirectUri}' points at {parsed.Host}, but this app can only receive the callback "
                + "on localhost or 127.0.0.1.");
        }

        int port = parsed.Port;
        TcpListener listener = new(IPAddress.Loopback, port);

        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            // Usually another copy of the app already holding the callback port.
            throw new InvalidOperationException(
                port == 80
                    ? "Port 80 is already in use, so the Twitch login page cannot be served. Close "
                        + "whatever is using it (a second copy of RANS0M is the usual culprit) and try again."
                    : $"Port {port} is already in use, so the Twitch login page cannot be served. Close "
                        + "whatever is using it (a second copy of RANS0M is the usual culprit) and try again.",
                ex);
        }

        return (listener, redirectUri);
    }

    /// <summary>Builds the Twitch authorize URL to open in the browser.</summary>
    public static string BuildAuthorizeUrl(TwitchSettings settings, string redirectUri, string state)
    {
        return "https://id.twitch.tv/oauth2/authorize"
            + $"?client_id={Uri.EscapeDataString(settings.ClientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
            + "&response_type=code"
            + $"&scope={TwitchApi.RedemptionScope}"
            + $"&state={Uri.EscapeDataString(state)}"
            + "&force_verify=true";
    }

    /// <summary>Opens the authorize URL in the user's default browser.</summary>
    public static void OpenBrowser(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>
    /// Waits for Twitch to redirect back with a code, and returns it. Returns null on timeout or
    /// if the user declined, so the caller can report that without treating it as an error.
    /// </summary>
    public static async Task<string?> WaitForCodeAsync(
        TcpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApprovalTimeout);

        try
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(timeout.Token);
            await using NetworkStream stream = client.GetStream();

            string request = await ReadRequestAsync(stream, timeout.Token);

            string? code = ExtractQueryValue(request, "code");
            string? state = ExtractQueryValue(request, "state");
            string? error = ExtractQueryValue(request, "error");
            string? description = ExtractQueryValue(request, "error_description");

            bool approved = error is null
                && code is not null
                && string.Equals(state, expectedState, StringComparison.Ordinal);

            await WriteResponseAsync(stream, approved, DescribeError(error, description, code));

            return approved ? code : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Turns Twitch's callback query into something worth showing a human. The failure page is often
    /// the only place the real reason surfaces, since a mismatch looks identical to a declined
    /// prompt otherwise.
    /// </summary>
    private static string? DescribeError(string? error, string? description, string? code)
    {
        if (error is null)
        {
            return code is null ? "no code returned" : null;
        }

        // redirect_mismatch means the registered URI differs from the one sent, which is a setup
        // problem the user can actually fix rather than a declined prompt.
        if (string.Equals(error, "redirect_mismatch", StringComparison.Ordinal))
        {
            return "Twitch rejected the redirect URI. The value in the Redirect URI box must match "
                + "the OAuth Redirect URL in your app's console exactly, including the trailing slash.";
        }

        return string.IsNullOrWhiteSpace(description) ? error : $"{error}: {description}";
    }

    /// <summary>Reads until the browser has finished sending its headers, ignoring the body.</summary>
    private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        using MemoryStream accumulator = new();
        byte[] terminator = "\r\n\r\n"u8.ToArray();

        while (accumulator.Length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken);
            if (read <= 0)
            {
                break;
            }

            accumulator.Write(buffer, 0, read);
            byte[] current = accumulator.ToArray();

            if (current.AsSpan().IndexOf(terminator) >= 0)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(accumulator.ToArray());
    }

    private static async Task WriteResponseAsync(NetworkStream stream, bool ok, string? detail)
    {
        string body = ok ? SuccessPage : BuildFailurePage(detail);

        byte[] payload = Encoding.UTF8.GetBytes(body);
        string headers = ok
            ? "HTTP/1.1 200 OK\r\n"
            : "HTTP/1.1 400 Bad Request\r\n";

        await stream.WriteAsync(Encoding.UTF8.GetBytes(
            headers
                + "Content-Type: text/html; charset=utf-8\r\n"
                + $"Content-Length: {payload.Length}\r\n"
                + "Connection: close\r\n\r\n"));
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    /// <summary>Pulls a single value out of the request line's query string.</summary>
    private static string? ExtractQueryValue(string request, string key)
    {
        int queryStart = request.IndexOf('?');
        if (queryStart < 0)
        {
            return null;
        }

        int queryEnd = request.IndexOf(' ', queryStart);
        string query = queryEnd < 0
            ? request[(queryStart + 1)..]
            : request[(queryStart + 1)..queryEnd];

        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string name = pair[..equals];
            if (!string.Equals(name, key, StringComparison.Ordinal))
            {
                continue;
            }

            return System.Net.WebUtility.UrlDecode(pair[(equals + 1)..]);
        }

        return null;
    }
}