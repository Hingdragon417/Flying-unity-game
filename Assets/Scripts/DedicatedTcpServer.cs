using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

// ATOMIC DEPLOY CHECK: lobby server bundle 2026-06-29 protocol v2.
public class DedicatedTcpServer : MonoBehaviour
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly Dictionary<int, ServerListing> listingTable = new();
    private static readonly object listingTableLock = new();
    private static int nextListingId = 1;

    [Header("Server")]
    [SerializeField] private int port = 25661;
    [SerializeField] private bool startOnAwake = true;
    [SerializeField] private bool enableConsoleCommands = true;

    private const float ServerSpeedGraceMultiplier = 1.15f;
    private const float MaxServerFallSpeed = 140f;

    private readonly List<ClientConnection> clients = new();
    private readonly object clientsLock = new();
    private TcpListener listener;
    private CancellationTokenSource cancellation;
    private int nextClientId = 1;
    private int shutdownRequested;

    public int Port => port;
    public bool IsRunning => listener != null;

    private async void Awake()
    {
        ApplyCommandLineOverrides();
        StartConsoleCommandLoop();

        if (startOnAwake)
        {
            await StartServerAsync();
        }
    }

    private void Update()
    {
        if (Interlocked.Exchange(ref shutdownRequested, 0) == 1)
        {
            ShutdownFromConsole();
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
            BroadcastStateToLobby(client, parts);
            return;
        }

        if (parts[0] == "create_listing")
        {
            CreateListing(client, parts);
            return;
        }

        if (parts[0] == "join_listing")
        {
            JoinListing(client, parts);
            return;
        }

        if (parts[0] == "listings_request")
        {
            _ = SendListingsAsync(client);
        }
    }

    private void BroadcastStateToLobby(ClientConnection client, string[] parts)
    {
        int listingId = client.ListingId;

        if (listingId <= 0)
        {
            return;
        }

        if (!TryParseState(parts, out Vector3 requestedPosition, out Quaternion requestedRotation))
        {
            return;
        }

        AuthoritativePlayerState state = SanitizeClientState(client, requestedPosition, requestedRotation);

        _ = BroadcastToListingAsync(
            listingId,
            FormatStateMessage(client.Id, state.Position, state.Rotation),
            client);
    }

    private bool TryParseState(string[] parts, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        if (!TryParseFloat(parts[1], out float x) ||
            !TryParseFloat(parts[2], out float y) ||
            !TryParseFloat(parts[3], out float z) ||
            !TryParseFloat(parts[4], out float qx) ||
            !TryParseFloat(parts[5], out float qy) ||
            !TryParseFloat(parts[6], out float qz) ||
            !TryParseFloat(parts[7], out float qw))
        {
            return false;
        }

        position = new Vector3(x, y, z);
        rotation = new Quaternion(qx, qy, qz, qw);
        return IsFinite(position) && IsFinite(rotation);
    }

    private AuthoritativePlayerState SanitizeClientState(ClientConnection client, Vector3 requestedPosition, Quaternion requestedRotation)
    {
        double now = NowSeconds();
        Quaternion safeRotation = NormalizeRotation(requestedRotation);

        if (!client.HasAuthoritativeState)
        {
            client.HasAuthoritativeState = true;
            client.AuthoritativePosition = requestedPosition;
            client.AuthoritativeRotation = safeRotation;
            client.LastStateTime = now;
            return new AuthoritativePlayerState(requestedPosition, safeRotation);
        }

        double deltaTime = Math.Max(0.001d, now - client.LastStateTime);
        Vector3 previousPosition = client.AuthoritativePosition;
        Vector3 delta = requestedPosition - previousPosition;

        Vector3 horizontalDelta = new(delta.x, 0f, delta.z);
        float maxHorizontalSpeed = GameplayRules.MaxOfficialHorizontalSpeed * ServerSpeedGraceMultiplier;
        float maxHorizontalDistance = maxHorizontalSpeed * (float)deltaTime + GameplayRules.ServerPositionPadding;
        if (horizontalDelta.magnitude > maxHorizontalDistance)
        {
            horizontalDelta = horizontalDelta.normalized * maxHorizontalDistance;
        }

        float maxUpwardSpeed = GameplayRules.MaxOfficialUpwardSpeed * ServerSpeedGraceMultiplier;
        float maxVerticalSpeed = delta.y >= 0f ? maxUpwardSpeed : MaxServerFallSpeed;
        float maxVerticalDistance = maxVerticalSpeed * (float)deltaTime + GameplayRules.ServerPositionPadding;
        float verticalDelta = Mathf.Clamp(delta.y, -maxVerticalDistance, maxVerticalDistance);

        Vector3 sanitizedPosition = previousPosition + new Vector3(horizontalDelta.x, verticalDelta, horizontalDelta.z);
        Vector3 updateDelta = sanitizedPosition - previousPosition;

        if (updateDelta.magnitude > GameplayRules.ServerMaxSingleUpdateDistance)
        {
            sanitizedPosition = previousPosition + updateDelta.normalized * GameplayRules.ServerMaxSingleUpdateDistance;
        }

        client.AuthoritativePosition = sanitizedPosition;
        client.AuthoritativeRotation = safeRotation;
        client.LastStateTime = now;
        return new AuthoritativePlayerState(sanitizedPosition, safeRotation);
    }

    private static string FormatStateMessage(int clientId, Vector3 position, Quaternion rotation)
    {
        return string.Join("|",
            "state",
            clientId.ToString(CultureInfo.InvariantCulture),
            Format(position.x),
            Format(position.y),
            Format(position.z),
            Format(rotation.x),
            Format(rotation.y),
            Format(rotation.z),
            Format(rotation.w));
    }

    private static Quaternion NormalizeRotation(Quaternion rotation)
    {
        float magnitude = Mathf.Sqrt(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w);
        if (magnitude <= 0.0001f)
        {
            return Quaternion.identity;
        }

        return new Quaternion(rotation.x / magnitude, rotation.y / magnitude, rotation.z / magnitude, rotation.w / magnitude);
    }

    private static bool TryParseFloat(string value, out float parsedValue)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedValue);
    }

    private static string Format(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(Quaternion value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static double NowSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d;
    }

    private void StartConsoleCommandLoop()
    {
        if (!enableConsoleCommands || !Application.isBatchMode)
        {
            return;
        }

        _ = Task.Run(ReadConsoleCommands);
    }

    private void ReadConsoleCommands()
    {
        try
        {
            while (true)
            {
                string command = Console.ReadLine();

                if (command == null)
                {
                    return;
                }

                if (string.Equals(command.Trim(), "stop", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Exchange(ref shutdownRequested, 1);
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"Console command listener stopped: {exception.Message}");
        }
    }

    private void ShutdownFromConsole()
    {
        Debug.Log("Stop command received. Shutting down dedicated server.");
        StopServer();
        Application.Quit(0);
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
        ServerListing previousListing;
        List<int> removedListingIds = new();

        lock (listingTableLock)
        {
            previousListing = RemoveClientFromCurrentListingLocked(client);

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
            listing.AddMember(client.Id);
            listingTable[listing.Id] = listing;
            client.ListingId = listing.Id;
        }

        if (previousListing != null && !removedListingIds.Contains(previousListing.Id))
        {
            _ = BroadcastAsync(FormatListing("listing_added", previousListing), null);
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

    private void JoinListing(ClientConnection client, string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out int listingId))
        {
            _ = client.SendAsync("join_failed|0|invalid_listing");
            return;
        }

        ServerListing listing = null;
        ServerListing previousListing = null;
        bool joined;
        string failureReason = string.Empty;

        lock (listingTableLock)
        {
            if (!listingTable.TryGetValue(listingId, out listing))
            {
                joined = false;
                failureReason = "not_found";
            }
            else if (listing.CurrentPlayers >= listing.MaxPlayers && !listing.HasMember(client.Id))
            {
                joined = false;
                failureReason = "full";
            }
            else
            {
                previousListing = RemoveClientFromCurrentListingLocked(client);
                listing.AddMember(client.Id);
                client.ListingId = listing.Id;
                joined = true;
            }
        }

        if (!joined)
        {
            _ = client.SendAsync($"join_failed|{listingId}|{failureReason}");
            return;
        }

        string listingMessage = FormatListing("listing_added", listing);
        if (previousListing != null && previousListing.Id != listing.Id)
        {
            _ = BroadcastAsync(FormatListing("listing_added", previousListing), null);
            _ = BroadcastToListingAsync(previousListing.Id, $"leave|{client.Id}", null);
        }

        _ = client.SendAsync(FormatListing("listing_joined", listing));
        _ = BroadcastAsync(listingMessage, null);
        _ = BroadcastToListingAsync(listing.Id, $"player_joined|{client.Id}|{listing.Id}", client);

        Debug.Log($"Client {client.Id} joined listing {listing.Id}: {listing.Name} ({listing.CurrentPlayers}/{listing.MaxPlayers}).");
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

    private async Task BroadcastToListingAsync(int listingId, string message, ClientConnection excludedClient)
    {
        List<ClientConnection> snapshot;

        lock (clientsLock)
        {
            snapshot = new List<ClientConnection>(clients);
        }

        foreach (ClientConnection client in snapshot)
        {
            if (client == excludedClient || client.ListingId != listingId)
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

    private void RemoveClient(ClientConnection client)
    {
        bool removed;
        ServerListing updatedListing = null;
        int previousListingId = client.ListingId;

        lock (clientsLock)
        {
            removed = clients.Remove(client);
        }

        lock (listingTableLock)
        {
            updatedListing = RemoveClientFromCurrentListingLocked(client);
        }

        client.Close();

        if (removed)
        {
            _ = BroadcastToListingAsync(previousListingId, $"leave|{client.Id}", null);

            if (updatedListing != null)
            {
                _ = BroadcastAsync(FormatListing("listing_added", updatedListing), null);
            }
        }
    }

    private ServerListing RemoveClientFromCurrentListingLocked(ClientConnection client)
    {
        if (client.ListingId <= 0 || !listingTable.TryGetValue(client.ListingId, out ServerListing listing))
        {
            client.ListingId = 0;
            return null;
        }

        listing.RemoveMember(client.Id);
        client.ListingId = 0;
        return listing;
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
        private readonly SemaphoreSlim sendLock = new(1, 1);

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
        public int ListingId { get; set; }
        public StreamReader Reader { get; }
        public bool IsConnected => tcpClient.Connected;
        public bool HasAuthoritativeState { get; set; }
        public Vector3 AuthoritativePosition { get; set; }
        public Quaternion AuthoritativeRotation { get; set; } = Quaternion.identity;
        public double LastStateTime { get; set; }

        public async Task SendAsync(string message)
        {
            await sendLock.WaitAsync();

            try
            {
                await writer.WriteLineAsync(message);
            }
            finally
            {
                sendLock.Release();
            }
        }

        public void Close()
        {
            Reader.Dispose();
            writer.Dispose();
            sendLock.Dispose();
            tcpClient.Close();
        }
    }

    private sealed class ServerListing
    {
        private readonly HashSet<int> memberClientIds = new();

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
        public int CurrentPlayers => memberClientIds.Count;

        public bool HasMember(int clientId)
        {
            return memberClientIds.Contains(clientId);
        }

        public void AddMember(int clientId)
        {
            memberClientIds.Add(clientId);
        }

        public void RemoveMember(int clientId)
        {
            memberClientIds.Remove(clientId);
        }
    }

    private readonly struct AuthoritativePlayerState
    {
        public AuthoritativePlayerState(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }

        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
    }

}
