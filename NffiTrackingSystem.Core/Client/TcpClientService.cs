using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using NffiTrackingSystem.Shared.Models;
using NffiTrackingSystem.Shared.Protocol;
using NffiTrackingSystem.Shared.Security;

namespace NffiTrackingSystem.Core.Client;

// TCP client for sending NFFI messages to the server.
// Optionally wraps the connection in TLS with mutual certificate authentication.
public sealed class TcpClientService
{
    // Underlying TCP connection and network stream (raw or TLS-wrapped)
    private TcpClient? _client;
    private Stream? _stream;

    // Sequence number incremented for every outgoing JREAP-C message
    private ushort _sequence;

    // Raised for status messages (connected, TLS established, etc.)
    public event Action<string>? Log;

    // True when the underlying TCP connection is open
    public bool IsConnected => _client?.Connected == true;

    // Connects to the server. If tls is non-null, performs a TLS handshake
    // with mutual certificate authentication.
    public async Task ConnectAsync(string host, int port, TlsOptions? tls = null)
    {
        // Open the TCP connection
        _client = new TcpClient();
        await _client.ConnectAsync(host, port);

        // Start with the raw network stream
        Stream stream = _client.GetStream();

        // Optionally wrap the stream in TLS
        if (tls?.Enabled == true)
        {
            // Load the client certificate (PFX with private key)
            var clientCert = CertificateHelper.LoadPfx(
                tls.ClientCertificatePath!, tls.ClientCertificatePassword);

            // Optionally load the root CA for server certificate validation
            X509Certificate2? rootCa = null;
            if (!string.IsNullOrWhiteSpace(tls.RootCaCertificatePath))
                rootCa = CertificateHelper.LoadCert(tls.RootCaCertificatePath);

            // Create the TLS stream with a custom validation callback.
            // If rootCa is present, the server's certificate is validated
            // against our custom root CA; otherwise system trust is used.
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

            // Perform the TLS handshake, presenting our client certificate
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ClientCertificates = new X509CertificateCollection { clientCert },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            });

            // From now on, write to the TLS stream instead of the raw one
            stream = ssl;
            Log?.Invoke($"TLS established with {host}:{port}.");
        }

        _stream = stream;
        Log?.Invoke($"Connected to {host}:{port}.");
    }

    // Closes the stream and TCP connection.
    public async Task DisconnectAsync()
    {
        try { _stream?.Close(); _client?.Close(); } catch { }
        await Task.CompletedTask;
    }

    // Serializes and sends a single NFFI message over the connection.
    // Wire format: [JREAP-C header: 16B][payload length: 4B LE][payload: JSON]
    public async Task SendNffiAsync(NffiMessage message)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected.");

        // Serialize the NFFI message to JSON bytes
        var payload = NffiSerializer.Serialize(message);

        // Build the JREAP-C header with an incrementing sequence number
        var header = new JreapHeader
        {
            MessageType = JreapConstants.MessageTypeNffiPpli,
            SequenceNumber = ++_sequence,
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        // Write header, then length prefix, then payload
        await _stream.WriteAsync(header.ToBytes());
        await _stream.WriteAsync(BitConverter.GetBytes(payload.Length));
        await _stream.WriteAsync(payload);

        // Flush so the bytes reach the server immediately
        await _stream.FlushAsync();
    }
}