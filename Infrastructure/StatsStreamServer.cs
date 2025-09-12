using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace AnakimOrchestrator.Infrastructure
{
    public interface IStatsStreamServer
    {
        void Start(int port, CancellationToken ct = default);
        ValueTask PublishAsync(object payload, CancellationToken ct = default); // payload já agregado (PI/AHs ou TM/PIs)
    }

    public sealed class StatsStreamServer : IStatsStreamServer
    {
        private readonly Channel<byte[]> _bus = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });

        private readonly ConcurrentDictionary<Guid, StreamWriter> _clients = new();

        public void Start(int port, CancellationToken ct = default)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();

            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(ct);
                    _ = HandleClientAsync(client, ct);
                }
            }, ct);

            // Broadcast loop
            _ = Task.Run(async () =>
            {
                while (await _bus.Reader.WaitToReadAsync(ct))
                {
                    while (_bus.Reader.TryRead(out var bytes))
                    {
                        foreach (var kvp in _clients.ToArray())
                        {
                            try { await kvp.Value.BaseStream.WriteAsync(bytes, ct); await kvp.Value.WriteLineAsync(); }
                            catch { _clients.TryRemove(kvp.Key, out _); }
                        }
                    }
                }
            }, ct);
        }

        public async ValueTask PublishAsync(object payload, CancellationToken ct = default)
        {
            // NDJSON (uma linha por mensagem)
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            await _bus.Writer.WriteAsync(Encoding.UTF8.GetBytes(json), ct);
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using var s = client.GetStream();
            using var r = new StreamReader(s, Encoding.UTF8, leaveOpen: true);
            var w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true };

            // HELLO simples (opcional: validar token)
            var line = await r.ReadLineAsync();
            // if (!Validate(line)) { client.Close(); return; }

            await w.WriteLineAsync(JsonSerializer.Serialize(new { type = "hello_ack", server = "Anakim", ts = DateTime.UtcNow }));

            var id = Guid.NewGuid();
            _clients[id] = w;

            // Heartbeats (caso fique ocioso)
            _ = Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    try { await w.WriteLineAsync(JsonSerializer.Serialize(new { type = "heartbeat", ts = DateTime.UtcNow })); }
                    catch { _clients.TryRemove(id, out _); break; }
                }
            }, ct);

            // Mantém aberto até o cliente fechar (leitura passiva)
            while (!ct.IsCancellationRequested && client.Connected)
            {
                var ping = await r.ReadLineAsync();
                if (ping is null) break;
            }

            _clients.TryRemove(id, out _);
            client.Close();
        }
    }

}
