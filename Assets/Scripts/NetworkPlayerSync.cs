using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using UnityEngine;

public class NetworkPlayerSync : MonoBehaviour
{
    [SerializeField] private DynamicTcpClient tcpClient;
    [SerializeField] private float sendRate = 15f;
    [SerializeField] private Color remotePlayerColor = new(0.1f, 0.7f, 1f);

    private readonly Dictionary<int, Transform> remotePlayers = new();
    private readonly Dictionary<int, Vector3> targetPositions = new();
    private readonly Dictionary<int, Quaternion> targetRotations = new();

    private int localPlayerId = -1;
    private float nextSendTime;

    private void Awake()
    {
        if (tcpClient == null)
        {
            tcpClient = FindAnyObjectByType<DynamicTcpClient>();
        }
    }

    private void OnEnable()
    {
        if (tcpClient != null)
        {
            tcpClient.MessageReceived += HandleServerMessage;
        }
    }

    private void OnDisable()
    {
        if (tcpClient != null)
        {
            tcpClient.MessageReceived -= HandleServerMessage;
        }
    }

    private void Update()
    {
        SendLocalState();
        SmoothRemotePlayers();
    }

    private void SendLocalState()
    {
        if (tcpClient == null || !tcpClient.IsConnected || Time.time < nextSendTime)
        {
            return;
        }

        nextSendTime = Time.time + 1f / Mathf.Max(1f, sendRate);

        Vector3 position = transform.position;
        Quaternion rotation = transform.rotation;

        string message = string.Join("|",
            "state",
            Format(position.x),
            Format(position.y),
            Format(position.z),
            Format(rotation.x),
            Format(rotation.y),
            Format(rotation.z),
            Format(rotation.w));

        _ = SendAsync(message);
    }

    private async Task SendAsync(string message)
    {
        await tcpClient.SendAsync(message);
    }

    private void HandleServerMessage(string message)
    {
        string[] parts = message.Split('|');

        if (parts.Length == 2 && parts[0] == "welcome" && int.TryParse(parts[1], out int welcomedId))
        {
            localPlayerId = welcomedId;
            Debug.Log($"Joined multiplayer server as player {localPlayerId}");
            return;
        }

        if (parts.Length == 2 && parts[0] == "leave" && int.TryParse(parts[1], out int leavingId))
        {
            RemoveRemotePlayer(leavingId);
            return;
        }

        if (parts.Length == 9 && parts[0] == "state" && int.TryParse(parts[1], out int playerId))
        {
            if (playerId == localPlayerId)
            {
                return;
            }

            if (!TryParseState(parts, out Vector3 position, out Quaternion rotation))
            {
                return;
            }

            EnsureRemotePlayer(playerId);
            targetPositions[playerId] = position;
            targetRotations[playerId] = rotation;
        }
    }

    private bool TryParseState(string[] parts, out Vector3 position, out Quaternion rotation)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;

        if (!TryParseFloat(parts[2], out float x) ||
            !TryParseFloat(parts[3], out float y) ||
            !TryParseFloat(parts[4], out float z) ||
            !TryParseFloat(parts[5], out float qx) ||
            !TryParseFloat(parts[6], out float qy) ||
            !TryParseFloat(parts[7], out float qz) ||
            !TryParseFloat(parts[8], out float qw))
        {
            return false;
        }

        position = new Vector3(x, y, z);
        rotation = new Quaternion(qx, qy, qz, qw);
        return true;
    }

    private void EnsureRemotePlayer(int playerId)
    {
        if (remotePlayers.ContainsKey(playerId))
        {
            return;
        }

        GameObject remotePlayer = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        remotePlayer.name = $"Remote Player {playerId}";
        remotePlayer.transform.localScale = new Vector3(1f, 1f, 1f);

        Collider remoteCollider = remotePlayer.GetComponent<Collider>();

        if (remoteCollider != null)
        {
            remoteCollider.enabled = false;
        }

        Renderer renderer = remotePlayer.GetComponent<Renderer>();

        if (renderer != null)
        {
            renderer.material.color = remotePlayerColor;
        }

        remotePlayers[playerId] = remotePlayer.transform;
        targetPositions[playerId] = remotePlayer.transform.position;
        targetRotations[playerId] = remotePlayer.transform.rotation;
    }

    private void RemoveRemotePlayer(int playerId)
    {
        if (!remotePlayers.TryGetValue(playerId, out Transform remotePlayer))
        {
            return;
        }

        Destroy(remotePlayer.gameObject);
        remotePlayers.Remove(playerId);
        targetPositions.Remove(playerId);
        targetRotations.Remove(playerId);
    }

    private void SmoothRemotePlayers()
    {
        foreach (KeyValuePair<int, Transform> entry in remotePlayers)
        {
            int playerId = entry.Key;
            Transform remotePlayer = entry.Value;

            if (targetPositions.TryGetValue(playerId, out Vector3 targetPosition))
            {
                remotePlayer.position = Vector3.Lerp(remotePlayer.position, targetPosition, Time.deltaTime * 12f);
            }

            if (targetRotations.TryGetValue(playerId, out Quaternion targetRotation))
            {
                remotePlayer.rotation = Quaternion.Slerp(remotePlayer.rotation, targetRotation, Time.deltaTime * 12f);
            }
        }
    }

    private static string Format(float value)
    {
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static bool TryParseFloat(string value, out float parsedValue)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsedValue);
    }
}
