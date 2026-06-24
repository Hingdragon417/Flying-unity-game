using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class DedicatedTcpServer : MonoBehaviour
{
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

                Debug.Log($"Client {client.Id} connected: {tcpClient.Client.RemoteEndPoint}");
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

        if (parts.Length == 8 && parts[0] == "state")
        {
            _ = BroadcastAsync($"state|{client.Id}|{parts[1]}|{parts[2]}|{parts[3]}|{parts[4]}|{parts[5]}|{parts[6]}|{parts[7]}", client);
        }
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
            Debug.Log($"Client {client.Id} disconnected.");
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
            Reader = new StreamReader(stream, Encoding.UTF8);
            writer = new StreamWriter(stream, Encoding.UTF8)
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
}
