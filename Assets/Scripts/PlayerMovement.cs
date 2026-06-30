using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : MonoBehaviour
{
    [Header("Movement")]
    public float mouseSensitivity = 0.5f;
    public Transform playerCamera;

    [Header("Glide Animation")]
    public Transform playerModel;
    public float glideBodyPitch = 90f;
    public float glideBankAngle = 25f;
    public float glideAnimSmooth = 8f;

    [Header("Runtime (read-only-ish)")]
    public Vector3 lastCheckpoint;
    public float cameraRotation;
    public float currentGlideSpeed;
    public float currentSpeedMultiplier = 1f;

    private Rigidbody rb;
    private bool isGrounded;
    private bool isGliding;
    private Coroutine speedBoostCoroutine;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();

        if (rb != null)
        {
            rb.freezeRotation = true;
        }

        ResolveSetupReferences();
    }

    void Start()
    {
        lastCheckpoint = transform.position;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        if (playerCamera == null)
        {
            ResolveSetupReferences();
        }

        if (Mouse.current != null)
        {
            Vector2 mouse = Mouse.current.delta.ReadValue();

            transform.Rotate(0, mouse.x * mouseSensitivity, 0);

            cameraRotation -= mouse.y * mouseSensitivity;
            cameraRotation = Mathf.Clamp(cameraRotation, -90f, 90f);

            if (playerCamera != null)
            {
                playerCamera.localRotation = Quaternion.Euler(cameraRotation, 0, 0);
            }
        }

        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame && isGrounded)
        {
            rb.AddForce(Vector3.up * GameplayRules.JumpForce, ForceMode.Impulse);
        }

        bool holdingGlide = !isGrounded && Keyboard.current != null && Keyboard.current.spaceKey.isPressed;

        // Glide only ENGAGES once you're no longer rising (top of the jump/arc).
        // That moment's height becomes your ceiling — you can never climb above it.
        if (holdingGlide && !isGliding && rb.linearVelocity.y <= 0.1f)
        {
            currentGlideSpeed = Mathf.Max(
                new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z).magnitude,
                GameplayRules.GlideStartSpeed
            );
            isGliding = true;
        }
        else if (!holdingGlide)
        {
            isGliding = false;
        }
    }

    private void ResolveSetupReferences()
    {
        if (playerCamera == null)
        {
            Camera childCamera = GetComponentInChildren<Camera>(true);
            if (childCamera != null)
            {
                playerCamera = childCamera.transform;
            }
        }

        if (playerModel != null)
        {
            return;
        }

        Transform existingModelRoot = transform.Find("PlayerModel");
        if (existingModelRoot != null)
        {
            playerModel = existingModelRoot;
            return;
        }

        List<Transform> visualChildren = new();

        foreach (Transform child in transform)
        {
            if (child == playerCamera || child.GetComponent<Camera>() != null)
            {
                continue;
            }

            if (child.GetComponentInChildren<Renderer>(true) != null ||
                child.GetComponentInChildren<SkinnedMeshRenderer>(true) != null)
            {
                visualChildren.Add(child);
            }
        }

        if (visualChildren.Count == 0)
        {
            return;
        }

        GameObject modelRoot = new("PlayerModel");
        Transform modelTransform = modelRoot.transform;
        modelTransform.SetParent(transform, false);
        modelTransform.localPosition = Vector3.zero;
        modelTransform.localRotation = Quaternion.identity;
        modelTransform.localScale = Vector3.one;

        foreach (Transform visualChild in visualChildren)
        {
            visualChild.SetParent(modelTransform, true);
        }

        playerModel = modelTransform;
    }

    void FixedUpdate()
    {
        if (isGliding)
        {
            Glide();
            return;
        }

        Vector2 moveInput = ReadMoveInput();
        float x = moveInput.x;
        float z = moveInput.y;

        float forwardSpeed = z > 0f ? z * GameplayRules.ForwardSpeedMultiplier : z;
        Vector3 movement = transform.right * x + transform.forward * forwardSpeed;
        movement = Vector3.ClampMagnitude(movement, GameplayRules.ForwardSpeedMultiplier);

        Vector3 velocity = movement * (GameplayRules.WalkSpeed * currentSpeedMultiplier);
        float verticalVelocity = rb.linearVelocity.y;

        if (isGrounded)
            currentGlideSpeed = 0f;

        rb.linearVelocity = new Vector3(velocity.x, verticalVelocity, velocity.z);
    }

    void Glide()
    {
        Vector3 lookDir = (playerCamera != null ? playerCamera.forward : transform.forward).normalized;
        float pitch = lookDir.y; // +up, -down

        float naturalMaxSpeed = GameplayRules.LevelGlideSpeed + GameplayRules.DiveSpeedBonus;
        float maxSpeed = Mathf.Max(GameplayRules.GlideMaxSpeed, naturalMaxSpeed) * currentSpeedMultiplier;
        float levelSpeed = GameplayRules.LevelGlideSpeed * currentSpeedMultiplier;
        float diveBonus = GameplayRules.DiveSpeedBonus * currentSpeedMultiplier;

        // Looking up bleeds speed. Looking back down gradually builds it again.
        if (pitch < -0.05f)
        {
            float diveAmount = Mathf.InverseLerp(0.05f, 0.75f, -pitch);
            float diveTargetSpeed = levelSpeed + diveBonus * diveAmount;
            currentGlideSpeed = Mathf.MoveTowards(
                currentGlideSpeed,
                diveTargetSpeed,
                GameplayRules.GlideDiveAcceleration * diveAmount * Time.fixedDeltaTime
            );
        }
        else if (pitch > 0.05f)
        {
            float climbAmount = Mathf.InverseLerp(0.05f, 0.75f, pitch);
            currentGlideSpeed = Mathf.MoveTowards(
                currentGlideSpeed,
                GameplayRules.MinGlideSpeed,
                GameplayRules.GlideClimbSlowdown * climbAmount * Time.fixedDeltaTime
            );
        }

        currentGlideSpeed -= currentGlideSpeed * GameplayRules.GlideDrag * Time.fixedDeltaTime;

        if (Mathf.Abs(pitch) < 0.15f && currentGlideSpeed < levelSpeed)
        {
            currentGlideSpeed = Mathf.MoveTowards(
                currentGlideSpeed,
                levelSpeed,
                GameplayRules.LevelGlideAcceleration * Time.fixedDeltaTime
            );
        }

        currentGlideSpeed = Mathf.Clamp(currentGlideSpeed, GameplayRules.MinGlideSpeed, maxSpeed);

        Vector3 targetVelocity = lookDir * currentGlideSpeed;
        targetVelocity.y -= GameplayRules.GlideSink;
        targetVelocity.y = Mathf.Min(targetVelocity.y, -GameplayRules.GlideMinDescentSpeed);

        Vector3 newVelocity = Vector3.Lerp(rb.linearVelocity, targetVelocity, GameplayRules.GlideTurnSpeed * Time.fixedDeltaTime);
        newVelocity.y = Mathf.Min(newVelocity.y, -GameplayRules.GlideMinDescentSpeed);

        rb.linearVelocity = newVelocity;
    }

    private Vector2 ReadMoveInput()
    {
        if (Keyboard.current != null)
        {
            float x = 0f;
            float z = 0f;

            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) x -= 1f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) x += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) z -= 1f;
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) z += 1f;

            return Vector2.ClampMagnitude(new Vector2(x, z), 1f);
        }

        return new Vector2(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"));
    }

    void LateUpdate()
    {
        if (playerModel == null) return;

        Quaternion target;

        if (isGliding)
        {
            Vector3 lookDir = (playerCamera != null ? playerCamera.forward : transform.forward).normalized;
            float lookPitchDeg = Mathf.Asin(Mathf.Clamp(lookDir.y, -1f, 1f)) * Mathf.Rad2Deg;

            float bodyPitch = glideBodyPitch - lookPitchDeg;
            float roll = -Input.GetAxis("Horizontal") * glideBankAngle;

            target = Quaternion.Euler(bodyPitch, 0f, roll);
        }
        else
        {
            target = Quaternion.identity;
        }

        playerModel.localRotation = Quaternion.Slerp(
            playerModel.localRotation,
            target,
            glideAnimSmooth * Time.deltaTime
        );
    }

    void OnCollisionStay(Collision collision)
    {
        foreach (ContactPoint contact in collision.contacts)
        {
            if (contact.normal.y > 0.5f)
            {
                isGrounded = true;
                isGliding = false;
                return;
            }
        }
    }

    void OnCollisionExit(Collision collision)
    {
        isGrounded = false;
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Checkpoint"))
        {
            ActivateCheckpoint(other.transform.position);
            Destroy(other.gameObject);
        }
    }

    public void ActivateCheckpoint(Vector3 checkpointPosition)
    {
        lastCheckpoint = checkpointPosition;

        if (speedBoostCoroutine != null)
            StopCoroutine(speedBoostCoroutine);

        speedBoostCoroutine = StartCoroutine(SpeedBoost());
    }

    IEnumerator SpeedBoost()
    {
        Debug.Log("SPEED BOOST");
        currentSpeedMultiplier = GameplayRules.CheckpointBoostMultiplier;

        currentGlideSpeed = Mathf.Max(currentGlideSpeed, GameplayRules.GlideStartSpeed) + GameplayRules.CheckpointGlideBoostBurst;

        yield return new WaitForSeconds(GameplayRules.CheckpointBoostDuration);

        currentSpeedMultiplier = 1f;
        speedBoostCoroutine = null;
    }
}
