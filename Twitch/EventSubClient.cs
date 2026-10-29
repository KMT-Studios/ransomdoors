using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace rans0m;

/// <summary>
/// Minimal EventSub WebSocket client, scoped to what this app needs: a single subscription's
/// notifications, keepalives, and surviving Twitch's own reconnects.
/// </summary>
/// <remarks>
/// The WebSocket is deliberately dumb. EventSub requires the app to answer a notification with a
/// 2xx within ten seconds or it retries, and it closes the socket if no keepalive is read for
/// around thirty seconds, so this class reads continuously and hands every message upward. All
/// policy about what a message means lives in <see cref="TwitchBridge"/>.
/// </remarks>
public sealed class EventSubClient
{
    private const string DefaultUrl = "wss://eventsub.wss.twitch.tv/ws";

    /// <summary>How long to wait between reconnection attempts, growing to <see cref="MaxBackoff"/>.</summary>
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Raised for every inbound message as (message_type, payload). Runs on a background task, so
    /// handlers must marshal to the UI thread themselves.
    /// </summary>
    public event Action<string, JsonElement>? MessageReceived;

    /// <summary>Raised with human-readable connection state for logging to the console.</summary>
    public event Action<string>? StatusChanged;

    private string? _reconnectUrl;

    /// <summary>The socket currently being read, so it can be torn down from another thread.</summary>
    private volatile ClientWebSocket? _activeSocket;

    /// <summary>Whether the client is currently connected and reading.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>
    /// Connects and keeps reconnecting until cancelled. Never throws for an ordinary network or
    /// protocol problem; those are reported through <see cref="StatusChanged"/> and retried, since
    /// a stream should not stop working just because Twitch briefly dropped the socket.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        string url = DefaultUrl;
        TimeSpan backoff = MinBackoff;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                StatusChanged?.Invoke($"connecting to {url}");
                await ReceiveSessionAsync(url, cancellationToken);

                // Twitch asked us to move sockets rather than because the connection failed.
                if (_reconnectUrl is not null)
                {
                    url = _reconnectUrl;
                    _reconnectUrl = null;
                    StatusChanged?.Invoke("reconnecting on Twitch's request");
                    continue;
                }

                backoff = MinBackoff;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"connection lost: {ex.Message}");
            }
            finally
            {
                IsConnected = false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            await Task.Delay(backoff, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, MaxBackoff.TotalSeconds));
        }

        StatusChanged?.Invoke("stopped");
    }

    /// <summary>
    /// Forces the current socket closed, so a <c>session_reconnect</c> or a wedged connection is
    /// followed by a fresh one instead of waiting on a read that may never return.
    /// </summary>
    public void Abort() => _activeSocket?.Abort();

    private async Task ReceiveSessionAsync(string url, CancellationToken cancellationToken)
    {
        using ClientWebSocket socket = new();
        _activeSocket = socket;
        try
        {
            await socket.ConnectAsync(new Uri(url), cancellationToken);
            IsConnected = true;
            StatusChanged?.Invoke("connected");

        byte[] buffer = new byte[8192];
            using MemoryStream accumulator = new();

            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    StatusChanged?.Invoke(
                        $"peer closed: {result.CloseStatus} '{result.CloseStatusDescription ?? string.Empty}'");

                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                    break;
                }

                accumulator.Write(buffer, 0, result.Count);

                // Twitch splits large notification payloads, so only act once a message is complete.
                if (!result.EndOfMessage)
                {
                    continue;
                }

                string text = Encoding.UTF8.GetString(accumulator.ToArray());
                accumulator.SetLength(0);

                Dispatch(text);
            }
        }
        finally
        {
            _activeSocket = null;
        }
    }

    /// <summary>Parses one message and raises it, ignoring anything malformed.</summary>
    private void Dispatch(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;

            string type = root.TryGetProperty("metadata", out JsonElement metadata)
                && metadata.TryGetProperty("message_type", out JsonElement messageType)
                    ? messageType.GetString() ?? string.Empty
                    : string.Empty;

            switch (type)
            {
                case "session_welcome":
                    StatusChanged?.Invoke("session established");

                    // The session id is what the subscription is bound to, so the bridge has to
                    // receive this frame exactly like a redemption. Clone is required: the
                    // JsonDocument is disposed below and the handler continues asynchronously.
                    if (root.TryGetProperty("payload", out JsonElement welcomePayload))
                    {
                        MessageReceived?.Invoke(type, welcomePayload.Clone());
                    }

                    break;

                case "session_reconnect":
                    // The subscription carries over, so we must not create a second one.
                    if (root.TryGetProperty("payload", out JsonElement reconnectPayload)
                        && reconnectPayload.TryGetProperty("session", out JsonElement session)
                        && session.TryGetProperty("reconnect_url", out JsonElement reconnectUrl))
                    {
                        _reconnectUrl = reconnectUrl.GetString();
                    }

                    StatusChanged?.Invoke("server requested reconnect");
                    break;

                case "revocation":
                    string reason = root.TryGetProperty("payload", out JsonElement revokePayload)
                        && revokePayload.TryGetProperty("subscription", out JsonElement subscription)
                        && subscription.TryGetProperty("status", out JsonElement status)
                            ? status.GetString() ?? "revoked"
                            : "revoked";
                    StatusChanged?.Invoke($"subscription lost: {reason}");
                    break;

                case "notification":
                    if (root.TryGetProperty("payload", out JsonElement payload))
                    {
                        // Cloned for the same reason as the welcome payload: handlers hand the
                        // element to the UI thread and may outlive this JsonDocument.
                        MessageReceived?.Invoke(type, payload.Clone());
                    }

                    break;
            }
        }
        catch (JsonException)
        {
            // A frame we cannot parse is not worth tearing the connection down for.
        }
    }
}