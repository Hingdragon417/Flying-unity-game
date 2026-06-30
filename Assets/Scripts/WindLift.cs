using System;
using UnityEngine;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
public class WindLift : MonoBehaviour
{
    [SerializeField] private float liftHeight = 40f;
    [SerializeField] private float liftAcceleration = 160f;
    [SerializeField] private float instantUpwardSpeed = 35f;
    [SerializeField] private float maxUpwardSpeed = 95f;
    [SerializeField] private float fastPlayerPredictionTime = 0.35f;
    [SerializeField] private float extraHorizontalPadding = 4f;
    [SerializeField] private LayerMask affectedLayers = ~0;
    [SerializeField] private bool drawGizmos = true;

    private Collider sourceCollider;
    private Renderer sourceRenderer;

    private void Awake()
    {
        ResolveBoundsSources();
    }

    private void FixedUpdate()
    {
        Bounds bounds = GetSourceBounds();
        Vector3 halfExtents = new(bounds.extents.x + extraHorizontalPadding, liftHeight * 0.5f, bounds.extents.z + extraHorizontalPadding);
        Vector3 center = bounds.center + Vector3.up * (bounds.extents.y + halfExtents.y);

        Collider[] hits = Physics.OverlapBox(center, halfExtents, Quaternion.identity, affectedLayers, QueryTriggerInteraction.Collide);

        foreach (Collider hit in hits)
        {
            PlayerMovement player = hit.GetComponentInParent<PlayerMovement>();
            if (player != null && player.TryGetComponent(out Rigidbody playerBody))
            {
                ApplyLift(playerBody);
            }
        }

        // Fast gliding can tunnel past a narrow overlap volume between physics ticks.
        // Also test the player's near-future position so the lift catches high speed passes.
        foreach (PlayerMovement player in FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None))
        {
            if (!player.TryGetComponent(out Rigidbody playerBody))
            {
                continue;
            }

            Vector3 predictedPosition = playerBody.position + playerBody.linearVelocity * fastPlayerPredictionTime;

            bool predictedInside =
                predictedPosition.x >= center.x - halfExtents.x &&
                predictedPosition.x <= center.x + halfExtents.x &&
                predictedPosition.y >= center.y - halfExtents.y &&
                predictedPosition.y <= center.y + halfExtents.y &&
                predictedPosition.z >= center.z - halfExtents.z &&
                predictedPosition.z <= center.z + halfExtents.z;

            if (predictedInside)
            {
                ApplyLift(playerBody);
            }
        }
    }

    private void ApplyLift(Rigidbody playerBody)
    {
        if (playerBody.linearVelocity.y < instantUpwardSpeed)
        {
            Vector3 velocity = playerBody.linearVelocity;
            velocity.y = instantUpwardSpeed;
            playerBody.linearVelocity = velocity;
        }

        playerBody.AddForce(Vector3.up * liftAcceleration, ForceMode.Acceleration);

        if (playerBody.linearVelocity.y > maxUpwardSpeed)
        {
            Vector3 velocity = playerBody.linearVelocity;
            velocity.y = maxUpwardSpeed;
            playerBody.linearVelocity = velocity;
        }
    }

    private void ResolveBoundsSources()
    {
        sourceCollider = GetComponent<Collider>();
        sourceRenderer = GetComponent<Renderer>();
    }

    private Bounds GetSourceBounds()
    {
        if (sourceCollider != null)
        {
            return sourceCollider.bounds;
        }

        if (sourceRenderer != null)
        {
            return sourceRenderer.bounds;
        }

        Vector3 size = Vector3.Max(transform.lossyScale, Vector3.one * 0.25f);
        return new Bounds(transform.position, size);
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos)
        {
            return;
        }

        ResolveBoundsSources();
        Bounds bounds = GetSourceBounds();
        Vector3 size = new(bounds.size.x + extraHorizontalPadding * 2f, liftHeight, bounds.size.z + extraHorizontalPadding * 2f);
        Vector3 center = bounds.center + Vector3.up * (bounds.extents.y + liftHeight * 0.5f);

        Gizmos.color = new Color(0.25f, 0.7f, 1f, 0.25f);
        Gizmos.DrawCube(center, size);
        Gizmos.color = new Color(0.25f, 0.7f, 1f, 1f);
        Gizmos.DrawWireCube(center, size);
    }
}

public static class WindLiftAutoBinder
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Initialize()
    {
        AttachToNamedWindObjects();
        SceneManager.sceneLoaded += (_, _) => AttachToNamedWindObjects();
    }

    private static void AttachToNamedWindObjects()
    {
        GameObject[] objects = UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (GameObject obj in objects)
        {
            if (!obj.scene.IsValid() ||
                !string.Equals(obj.name, "wind", StringComparison.OrdinalIgnoreCase) ||
                obj.GetComponent<WindLift>() != null)
            {
                continue;
            }

            obj.AddComponent<WindLift>();
        }
    }
}
