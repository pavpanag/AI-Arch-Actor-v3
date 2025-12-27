using UnityEngine;
using UnityEngine.InputSystem;

public class FlyCamera : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 10f;
    public float boostMultiplier = 4f;
    public float lookSensitivity = 2f;

    private float yaw;   // rotation around Y (left/right)
    private float pitch; // rotation around X (up/down)

    private bool cameraActive = true;

    void Start()
    {
        SyncRotationFromTransform();
        ActivateCamera(true);
    }

    void Update()
    {
        // Toggle camera control
        if (Keyboard.current.tabKey.wasPressedThisFrame)
        {
            cameraActive = !cameraActive;

            if (cameraActive)
                SyncRotationFromTransform();

            ActivateCamera(cameraActive);
        }

        if (!cameraActive)
            return;

        // Mouse look
        Vector2 look = Mouse.current.delta.ReadValue();
        yaw   += look.x * lookSensitivity * Time.deltaTime;
        pitch -= look.y * lookSensitivity * Time.deltaTime;

        pitch = Mathf.Clamp(pitch, -90f, 90f);

        transform.rotation = Quaternion.Euler(pitch, yaw, 0f);

        // Movement
        Vector3 move = Vector3.zero;

        if (Keyboard.current.wKey.isPressed) move += transform.forward;
        if (Keyboard.current.sKey.isPressed) move -= transform.forward;
        if (Keyboard.current.aKey.isPressed) move -= transform.right;
        if (Keyboard.current.dKey.isPressed) move += transform.right;
        if (Keyboard.current.eKey.isPressed) move += transform.up;
        if (Keyboard.current.qKey.isPressed) move -= transform.up;

        float speed = moveSpeed * (Keyboard.current.leftShiftKey.isPressed ? boostMultiplier : 1f);
        transform.position += move * speed * Time.deltaTime;
    }

    private void SyncRotationFromTransform()
    {
        Vector3 e = transform.eulerAngles;

        yaw = e.y;
        pitch = e.x;

        if (pitch > 180f)
            pitch -= 360f;

        pitch = Mathf.Clamp(pitch, -90f, 90f);
    }

    private void ActivateCamera(bool active)
    {
        if (active)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        else
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
