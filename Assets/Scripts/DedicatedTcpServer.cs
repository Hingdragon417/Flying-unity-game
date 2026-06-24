using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class DedicatedTcpServer : MonoBehaviour
{
    [Header("Server")]
    [SerializeField] private int port = 25661;
    [SerializeField] private bool startOnAwake = true;

    private readonly List<TcpClient> clients = new();
    private TcpListener listener;
    private CancellationTokenSource cancellation;

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
                TcpClient client = await listener.AcceptTcpClientAsync();
                clients.Add(client);

                Debug.Log($"Client connected: {client.Client.RemoteEndPoint}");
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

        foreach (TcpClient client in clients)
        {
            client.Close();
        }

        clients.Clear();
    }

    public async Task BroadcastAsync(string message)
    {
        byte[] data = Encoding.UTF8.GetBytes(message + "\n");

        for (int i = clients.Count - 1; i >= 0; i--)
        {
            TcpClient client = clients[i];

            if (!client.Connected)
            {
                clients.RemoveAt(i);
                continue;
            }

            try
            {
                await client.GetStream().WriteAsync(data, 0, data.Length);
            }
            catch
            {
                client.Close();
                clients.RemoveAt(i);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        byte[] buffer = new byte[4096];

        try
        {
            using NetworkStream stream = client.GetStream();

            while (!token.IsCancellationRequested && client.Connected)
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

                if (bytesRead == 0)
                {
                    break;
                }

                string message = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
                Debug.Log($"Client says: {message}");

                byte[] reply = Encoding.UTF8.GetBytes($"Server received: {message}\n");
                await stream.WriteAsync(reply, 0, reply.Length);
            }
        }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
            {
                Debug.LogWarning($"Client disconnected with error: {exception.Message}");
            }
        }
        finally
        {
            clients.Remove(client);
            client.Close();
            Debug.Log("Client disconnected.");
        }
    }

    private void ApplyCommandLineOverrides()
    {
        string[] args = Environment.GetCommandLineArgs();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-serverPort" && i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedPort))
            {
                port = parsedPort;
                i++;
            }
        }
    }

    private void OnApplicationQuit()
    {
        StopServer();
    }

    private void OnDestroy()
    {
        StopServer();
    }
}
