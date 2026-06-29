using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// ATOMIC DEPLOY CHECK: lobby server script updated 2026-06-29 protocol v2.
public class DedicatedTcpServer : MonoBehaviour
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly Dictionary<int, ServerListing> listingTable = new();
    private static readonly object listingTableLock = new();
    private static int nextListingId = 1;

    [Header("Server")]
    [SerializeField] private int port = 25661;
    [SerializeField] private bool startOnAwake = true;

    private readonly List<ClientConnection> clients = new();
    private readonly object clientsLock = new();
    private TcpListener listener;
    private CancellationTokenSource cancellation;
    private int nextClientId = 1;

    public int Port => port;
    public bool IsRunning => listener != null;

    private async void Awake()
    {
        ApplyCommandLineOverrides();

        if (startOnAwake)
        {
            await StartServerAsync();
        }
    }

    public async Task StartServerAsync()
    {
        if (IsRunning)
        {
            return;
        }

        cancellation = new CancellationTokenSource();
        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        Debug.Log($"Dedicated TCP server listening on port {port}");

        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                TcpClient tcpClient = await listener.AcceptTcpClientAsync();
                ClientConnection client = new(nextClientId++, tcpClient);

                lock (clientsLock)
                {
                    clients.Add(client);
                }

                _ = HandleClientAsync(client, cancellation.Token);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            if (!cancellation.IsCancellationRequested)
            {
                Debug.LogError($"TCP server error: {exception.Message}");
            }
        }
    }

    public void StopServer()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;

        listener?.Stop();
        listener = null;

        List<ClientConnection> snapshot;

        lock (clientsLock)
        {
            snapshot = new List<ClientConnection>(clients);
            clients.Clear();
        }

        foreach (ClientConnection client in snapshot)
        {
            client.Close();
        }
    }

    public async Task BroadcastAsync(string message)
    {
        await BroadcastAsync(message, null);
    }

    private async Task BroadcastAsync(string message, ClientConnection excludedClient)
    {
        List<ClientConnection> snapshot;

        lock (clientsLock)
        {
            snapshot = new List<ClientConnection>(clients);
        }

        foreach (ClientConnection client in snapshot)
        {
            if (client == excludedClient)
            {
                continue;
            }

            try
            {
                await client.SendAsync(message);
            }
            catch
            {
                RemoveClient(client);
            }
        }
    }

    private async Task HandleClientAsync(ClientConnection client, CancellationToken token)
    {
        try
        {
            await client.SendAsync($"welcome|{client.Id}");
            await client.SendAsync("server_protocol|2");
            await SendListingsAsync(client);
            await BroadcastAsync($"join|{client.Id}", client);

            while (!token.IsCancellationRequested && client.IsConnected)
            {
                string message = await client.Reader.ReadLineAsync();

                if (message == null)
                {
                    break;
                }

                HandleClientMessage(client, message);
            }
        }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
            {
                Debug.LogWarning($"Client {client.Id} disconnected with error: {exception.Message}");
            }
        }
        finally
        {
            RemoveClient(client);
        }
    }

    private void HandleClientMessage(ClientConnection client, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string[] parts = message.Split('|');
        parts[0] = parts[0].Trim().Trim('\uFEFF');

        if (parts.Length == 8 && parts[0] == "state")
        {
            _ = BroadcastAsync($"state|{client.Id}|{parts[1]}|{parts[2]}|{parts[3]}|{parts[4]}|{parts[5]}|{parts[6]}|{parts[7]}", client);
            return;
        }

        if (parts[0] == "create_listing")
        {
            CreateListing(client, parts);
            return;
        }

        if (parts[0] == "listings_request")
        {
            _ = SendListingsAsync(client);
        }
    }

    private void CreateListing(ClientConnection client, string[] parts)
    {
        int maxPlayers = 8;
        string lobbyName = $"Server {client.Id}";

        if (parts.Length > 1 && int.TryParse(parts[1], out int requestedMaxPlayers))
        {
            maxPlayers = Mathf.Clamp(requestedMaxPlayers, 1, 12);
        }

        if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]))
        {
            lobbyName = parts[2].Trim();
        }

        ServerListing listing;
        List<int> removedListingIds = new();

        lock (listingTableLock)
        {
            foreach (KeyValuePair<int, ServerListing> existing in listingTable)
            {
                if (existing.Value.HostClientId == client.Id ||
                    string.Equals(existing.Value.Name, lobbyName, StringComparison.OrdinalIgnoreCase))
                {
                    removedListingIds.Add(existing.Key);
                }
            }

            foreach (int listingId in removedListingIds)
            {
                listingTable.Remove(listingId);
            }

            listing = new ServerListing(nextListingId++, client.Id, maxPlayers, lobbyName);
            listingTable[listing.Id] = listing;
        }

        string message = FormatListing("listing_added", listing);
        foreach (int removedListingId in removedListingIds)
        {
            _ = BroadcastAsync($"listing_removed|{removedListingId}", null);
        }

        _ = client.SendAsync(FormatListing("listing_created", listing));
        _ = BroadcastAsync(message, client);
        _ = SendListingsAsync(client);

        Debug.Log($"Created server listing {listing.Id}: {listing.Name} ({maxPlayers} players).");
    }

    private async Task SendListingsAsync(ClientConnection client)
    {
        List<ServerListing> snapshot;

        lock (listingTableLock)
        {
            snapshot = new List<ServerListing>(listingTable.Values);
        }

        snapshot.Sort((left, right) => left.Id.CompareTo(right.Id));

        await client.SendAsync($"listings_begin|{snapshot.Count}");
        Debug.Log($"Sent server listing snapshot: {snapshot.Count} active.");

        foreach (ServerListing listing in snapshot)
        {
            string message = FormatListing("listing", listing);
            await client.SendAsync(message);
        }

        await client.SendAsync("listings_end");
    }

    private static string FormatListing(string messageType, ServerListing listing)
    {
        return $"{messageType}|{listing.Id}|{listing.HostClientId}|{listing.MaxPlayers}|{listing.CurrentPlayers}|{listing.Name}";
    }

    private void RemoveClient(ClientConnection client)
    {
        bool removed;

        lock (clientsLock)
        {
            removed = clients.Remove(client);
        }

        client.Close();

        if (removed)
        {
            _ = BroadcastAsync($"leave|{client.Id}", null);
        }
    }

    private void ApplyCommandLineOverrides()
    {
        string[] args = Environment.GetCommandLineArgs();

        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "-port" || args[i] == "-serverPort") && i + 1 < args.Length)
            {
                TryApplyPortArgument(args[i], args[i + 1]);
                i++;
            }
        }
    }

    private void TryApplyPortArgument(string argumentName, string argumentValue)
    {
        if (!ushort.TryParse(argumentValue, out ushort parsedPort) || parsedPort == 0)
        {
            Debug.LogWarning($"Ignoring invalid {argumentName} value: {argumentValue}");
            return;
        }

        port = parsedPort;
    }

    private void OnApplicationQuit()
    {
        StopServer();
    }

    private void OnDestroy()
    {
        StopServer();
    }

    private sealed class ClientConnection
    {
        private readonly TcpClient tcpClient;
        private readonly StreamWriter writer;

        public ClientConnection(int id, TcpClient tcpClient)
        {
            Id = id;
            this.tcpClient = tcpClient;

            NetworkStream stream = tcpClient.GetStream();
            Reader = new StreamReader(stream, Utf8NoBom);
            writer = new StreamWriter(stream, Utf8NoBom)
            {
                AutoFlush = true
            };
        }

        public int Id { get; }
        public StreamReader Reader { get; }
        public bool IsConnected => tcpClient.Connected;

        public Task SendAsync(string message)
        {
            return writer.WriteLineAsync(message);
        }

        public void Close()
        {
            Reader.Dispose();
            writer.Dispose();
            tcpClient.Close();
        }
    }

    private sealed class ServerListing
    {
        public ServerListing(int id, int hostClientId, int maxPlayers, string name)
        {
            Id = id;
            HostClientId = hostClientId;
            MaxPlayers = maxPlayers;
            Name = name;
        }

        public int Id { get; }
        public int HostClientId { get; }
        public int MaxPlayers { get; }
        public string Name { get; }
        public int CurrentPlayers => 1;
    }

}
