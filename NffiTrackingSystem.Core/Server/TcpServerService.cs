using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NffiTrackingSystem.Shared.Models;
using NffiTrackingSystem.Shared.Protocol;
using NffiTrackingSystem.Shared.Security;

namespace NffiTrackingSystem.Core.Server;

// TCP server that accepts NFFI messages from vehicles.
// Supports multiple simultaneous clients and optional TLS mutual authentication.
public sealed class TcpServerService
{
    // Listening port
    private readonly int _port;

    // TLS configuration (null = plain TCP)
    private readonly TlsOptions? _tls;

    // Underlying TCP listener
    private TcpListener? _listener;

    // Cancellation token source for the accept loop
    private CancellationTokenSource? _cts;

    // Connected clients, keyed by remote endpoint string
    private readonly ConcurrentDictionary<string, TcpClient> _clients = new();

    // Raised for status messages (client connect/disconnect, errors, TLS state)
    public event Action<string>? Log;

    // Raised for every fully-received NFFI message:
    // (remote endpoint, message, JREAP sequence number, NFFI XML)
    public event Action<string, NffiMessage, int, string>? MessageReceived;

    public TcpServerService(int port, TlsOptions? tls = null)
    {
        _port = port;
        _tls = tls;
    }

    // Starts the TCP listener and begins accepting clients in the background.
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        Log?.Invoke(_tls?.Enabled == true
            ? $"TLS server started on {_port}."
            : $"Server started on {_port}.");

        // Fire-and-forget the accept loop
        _ = AcceptLoopAsync(_cts.Token);
    }

    // Stops the server: cancels the accept loop and closes all client connections.
    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener?.Stop();

        // Force-close every currently connected client
        foreach (var c in _clients.Values) { try { c.Close(); } catch { } }
        _clients.Clear();

        Log?.Invoke("Server stopped.");
        await Task.CompletedTask;
    }

    // Runs until cancellation. Accepts new TCP clients and hands each one off
    // to a background task for its own read loop.
    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Wait for a new client
                var client = await _listener!.AcceptTcpClientAsync(token);
                var ep = client.Client.RemoteEndPoint?.ToString() ?? "unknown";

                // Remember the client in the dictionary
                _clients[ep] = client;
                Log?.Invoke($"Client connected: {ep}");

                // Handle the client on a background task (fire-and-forget)
                _ = HandleClientAsync(client, ep, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log?.Invoke($"Accept error: {ex.Message}"); }
        }
    }

    // Handles one client: optionally performs the TLS handshake,
    // then runs the read loop until disconnect.
    private async Task HandleClientAsync(TcpClient client, string ep, CancellationToken token)
    {
        using (client)
        {
            Stream stream = client.GetStream();

            try
            {
                // Optional TLS handshake
                if (_tls?.Enabled == true)
                {
                    // Load our server certificate
                    var serverCert = CertificateHelper.LoadPfx(
                        _tls.ServerCertificatePath!, _tls.ServerCertificatePassword);

                    // Optionally load the root CA for validating the client certificate
                    X509Certificate2? rootCa = null;
                    if (!string.IsNullOrWhiteSpace(_tls.RootCaCertificatePath))
                        rootCa = CertificateHelper.LoadCert(_tls.RootCaCertificatePath);

                    // Create the TLS stream with a custom validation callback
                    var ssl = new SslStream(stream, false, (s, cert, chain, errors) =>
                    {
                        if (cert is null) return false;
                        if (rootCa is null) return errors == SslPolicyErrors.None;
                        try
                        {
                            return CertificateHelper.ValidateAgainstRoot(
                                new X509Certificate2(cert), rootCa);
                        }
                        catch { return false; }
                    });

                    // Perform the TLS handshake as the server side
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = serverCert,
                        ClientCertificateRequired = _tls.RequireClientCertificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, token);

                    stream = ssl;
                    Log?.Invoke($"TLS established with {ep}.");
                }

                // Read NFFI messages until the connection closes
                await ReadLoopAsync(stream, ep, token);
            }
            catch (Exception ex) { Log?.Invoke($"Client error {ep}: {ex.Message}"); }
            finally
            {
                // Clean up the dictionary entry
                _clients.TryRemove(ep, out _);
                Log?.Invoke($"Client disconnected: {ep}");
            }
        }
    }

    // Reads NFFI messages in a loop:
    //   [JREAP-C header 16B][payload length 4B][payload JSON]
    private async Task ReadLoopAsync(Stream stream, string ep, CancellationToken token)
    {
        // Reusable buffer for the fixed-size JREAP-C header
        var headerBuffer = new byte[JreapConstants.HeaderSize];

        while (!token.IsCancellationRequested)
        {
            // Read the 16-byte JREAP-C header
            if (!await ReadExactAsync(stream, headerBuffer, JreapConstants.HeaderSize, token))
                break;

            var header = JreapHeader.FromBytes(headerBuffer);

            // Sanity check: verify the magic number
            if (header.Magic != JreapConstants.Magic)
            {
                Log?.Invoke($"Invalid magic from {ep}.");
                break;
            }

            // Read the 4-byte little-endian payload length
            var lengthBuffer = new byte[4];
            if (!await ReadExactAsync(stream, lengthBuffer, 4, token)) break;

            int payloadLen = BitConverter.ToInt32(lengthBuffer, 0);
            if (payloadLen <= 0 || payloadLen > 1024 * 1024)
            {
                Log?.Invoke($"Bad payload length {payloadLen}.");
                break;
            }

            // Read the JSON payload
            var payloadBuffer = new byte[payloadLen];
            if (!await ReadExactAsync(stream, payloadBuffer, payloadLen, token)) break;

            // Deserialize the NFFI message and also produce an XML view for storage
            var message = NffiSerializer.Deserialize(payloadBuffer);
            var xml = NffiSerializer.ToXml(message);

            // Notify subscribers (e.g. the WPF server UI)
            MessageReceived?.Invoke(ep, message, header.SequenceNumber, xml);
        }
    }

    // Reads exactly `count` bytes from the stream into `buffer`.
    // Returns false if the connection closed before the buffer was filled.
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), token);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}