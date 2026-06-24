using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class DynamicTcpClient : MonoBehaviour
{
    [Header("Default Server")]
    [SerializeField] private string host = "play.atomichost.xyz";
    [SerializeField] private int port = 25661;

    [Header("Connection")]
    [SerializeField] private bool connectOnStart = true;
    [SerializeField] private string helloMessage = "Hello from Unity";

    private TcpClient client;
    private NetworkStream stream;
    private CancellationTokenSource cancellation;

    public bool IsConnected => client != null && client.Connected;
    public string Host => host;
    public int Port => port;

    private async void Start()
    {
        ApplyCommandLineOverrides();

        if (connectOnStart)
        {
            await ConnectAsync();
        }
    }

    public async Task ConnectAsync()
    {
        await ConnectAsync(host, port);
    }

    public async Task ConnectAsync(string serverAddress)
    {
        if (!TrySplitAddress(serverAddress, out string parsedHost, out int parsedPort))
        {
            Debug.LogError($"Invalid TCP server address: {serverAddress}");
            return;
        }

        await ConnectAsync(parsedHost, parsedPort);
    }

    public async Task ConnectAsync(string serverHost, int serverPort)
    {
        Disconnect();

        host = serverHost;
        port = serverPort;
        cancellation = new CancellationTokenSource();

        try
        {
            client = new TcpClient();
            await client.ConnectAsync(host, port);
            stream = client.GetStream();

            Debug.Log($"Connected to TCP server {host}:{port}");

            if (!string.IsNullOrWhiteSpace(helloMessage))
            {
                await SendAsync(helloMessage);
            }

            _ = ReceiveLoopAsync(cancellation.Token);
        }
        catch (Exception exception)
        {
            Debug.LogError($"Failed to connect to TCP server {host}:{port}: {exception.Message}");
            Disconnect();
        }
    }

    public void SetServer(string serverHost, int serverPort)
    {
        host = serverHost;
        port = serverPort;
    }

    public void SetPort(int serverPort)
    {
        port = serverPort;
    }

    public async Task SendAsync(string message)
    {
        if (stream == null || !IsConnected)
        {
            Debug.LogWarning("Cannot send TCP message because the client is not connected.");
            return;
        }

        byte[] data = Encoding.UTF8.GetBytes(message + "\n");
        await stream.WriteAsync(data, 0, data.Length);
    }

    public void Disconnect()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;

        stream?.Close();
        stream = null;

        client?.Close();
        client = null;
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        byte[] buffer = new byte[4096];

        while (!token.IsCancellationRequested && stream != null && IsConnected)
        {
            try
            {
                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

                if (bytesRead == 0)
                {
                    Debug.Log("TCP server closed the connection.");
                    Disconnect();
                    return;
                }

                string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                Debug.Log($"TCP received: {message}");
            }
            catch (Exception exception)
            {
                if (!token.IsCancellationRequested)
                {
                    Debug.LogError($"TCP receive error: {exception.Message}");
                    Disconnect();
                }

                return;
            }
        }
    }

    private void ApplyCommandLineOverrides()
    {
        string[] args = Environment.GetCommandLineArgs();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-server" && i + 1 < args.Length)
            {
                ConnectAddressFromString(args[i + 1]);
                i++;
            }
            else if (args[i] == "-serverHost" && i + 1 < args.Length)
            {
                host = args[i + 1];
                i++;
            }
            else if (args[i] == "-serverPort" && i + 1 < args.Length && int.TryParse(args[i + 1], out int parsedPort))
            {
                port = parsedPort;
                i++;
            }
        }
    }

    private void ConnectAddressFromString(string serverAddress)
    {
        if (TrySplitAddress(serverAddress, out string parsedHost, out int parsedPort))
        {
            host = parsedHost;
            port = parsedPort;
        }
        else
        {
            Debug.LogWarning($"Ignoring invalid -server value: {serverAddress}");
        }
    }

    private static bool TrySplitAddress(string serverAddress, out string parsedHost, out int parsedPort)
    {
        parsedHost = string.Empty;
        parsedPort = 0;

        if (string.IsNullOrWhiteSpace(serverAddress))
        {
            return false;
        }

        string[] parts = serverAddress.Trim().Split(':');

        if (parts.Length != 2 || !int.TryParse(parts[1], out parsedPort))
        {
            return false;
        }

        parsedHost = parts[0];
        return !string.IsNullOrWhiteSpace(parsedHost) && parsedPort > 0 && parsedPort <= 65535;
    }

    private void OnApplicationQuit()
    {
        Disconnect();
    }

    private void OnDestroy()
    {
        Disconnect();
    }
}
