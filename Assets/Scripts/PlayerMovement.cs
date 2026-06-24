using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : MonoBehaviour
{

    public float speed = 5f;
    public float jumpForce = 5f;
    public float glideFallSpeed = 2f;
    public float glideDiveFallSpeed = 18f;
    public float glideStartSpeed = 7f;
    public float glideMaxSpeed = 24f;
    public float glideAcceleration = 8f;
    public float glideDiveAcceleration = 18f;
    public float glideStallDeceleration = 20f;
    public float glideStallFallSpeed = 12f;
    public float glideStallPitch = 0.35f;
    public float glideTurnSpeed = 12f;
    public float glideRotationSpeed = 8f;
    public float mouseSensitivity = 0.01f;
    public Transform playerCamera; 

    public float cameraRotation;
    public float currentGlideSpeed;

    private Rigidbody rb; 
    private bool isGrounded;
    private bool isGliding;


    void Start()
    {
        rb = GetComponent<Rigidbody>();
    
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false; 
        
    }

    void Update()
    {
        Vector2 mouse = Mouse.current.delta.ReadValue();

        transform.Rotate(0, mouse.x * mouseSensitivity, 0);

        cameraRotation -= mouse.y * mouseSensitivity;
        cameraRotation = Mathf.Clamp(cameraRotation, -90f, 90f);
        playerCamera.localRotation = Quaternion.Euler(cameraRotation, 0, 0);

        if (Keyboard.current.spaceKey.wasPressedThisFrame && isGrounded)
        {
            rb.AddForce(Vector3.up * jumpForce, ForceMode.Impulse);
        }

        bool wantsToGlide = !isGrounded && Keyboard.current.spaceKey.isPressed;

        if (wantsToGlide && !isGliding)
        {
            currentGlideSpeed = Mathf.Max(
                new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z).magnitude,
                glideStartSpeed
            );
        }

        isGliding = wantsToGlide;
    }

    void FixedUpdate()
    {
        float x = Input.GetAxis("Horizontal");
        float z = Input.GetAxis("Vertical");

        Vector3 movement = transform.right * x + transform.forward * z;
        Vector3 velocity = movement * speed;
        float verticalVelocity = rb.linearVelocity.y;

        if (isGliding)
        {
            Vector3 aimDirection = playerCamera != null ? playerCamera.forward : transform.forward;
            Vector3 flatAimDirection = Vector3.ProjectOnPlane(aimDirection, Vector3.up).normalized;

            if (flatAimDirection.sqrMagnitude < 0.01f)
            {
                flatAimDirection = transform.forward;
            }

            float diveAmount = Mathf.Clamp01(-aimDirection.y);
            float stallAmount = Mathf.InverseLerp(glideStallPitch, 1f, aimDirection.y);

            if (stallAmount > 0f)
            {
                currentGlideSpeed = Mathf.MoveTowards(
                    currentGlideSpeed,
                    0f,
                    glideStallDeceleration * stallAmount * Time.fixedDeltaTime
                );
            }
            else
            {
                float acceleration = Mathf.Lerp(glideAcceleration, glideDiveAcceleration, diveAmount);
                currentGlideSpeed = Mathf.MoveTowards(
                    currentGlideSpeed,
                    glideMaxSpeed,
                    acceleration * Time.fixedDeltaTime
                );
            }

            Vector3 glideVelocity = flatAimDirection * currentGlideSpeed;
            RotateTowardGlideDirection(glideVelocity);

            velocity = Vector3.MoveTowards(
                new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z),
                glideVelocity,
                glideTurnSpeed * Time.fixedDeltaTime
            );

            float targetFallSpeed = Mathf.Lerp(glideFallSpeed, glideDiveFallSpeed, diveAmount);
            targetFallSpeed = Mathf.Lerp(targetFallSpeed, glideStallFallSpeed, stallAmount);
            verticalVelocity = Mathf.Max(verticalVelocity, -targetFallSpeed);
        }
        else if (isGrounded)
        {
            currentGlideSpeed = 0f;
        }

        rb.linearVelocity = new Vector3(velocity.x, verticalVelocity, velocity.z);
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

    void RotateTowardGlideDirection(Vector3 glideVelocity)
    {
        Vector3 horizontalDirection = new Vector3(glideVelocity.x, 0f, glideVelocity.z);

        if (horizontalDirection.sqrMagnitude < 0.01f)
        {
            return;
        }

        Quaternion targetRotation = Quaternion.LookRotation(horizontalDirection.normalized, Vector3.up);
        transform.rotation = Quaternion.Slerp(
            transform.rotation,
            targetRotation,
            glideRotationSpeed * Time.fixedDeltaTime
        );
    }
}
