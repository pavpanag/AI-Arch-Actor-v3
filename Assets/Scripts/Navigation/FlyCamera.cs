using UnityEngine;
using UnityEngine.InputSystem;

public class FlyCamera : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 10f;
    public float boostMultiplier = 4f;
    public float lookSensitivity = 2f;

    private float rotationX;
    private float rotationY;

    private bool cameraActive = true; // starts active

    void Start()
    {
        ActivateCamera(true);
    }

    void Update()
    {
        // --- Toggle with Tab ---
        if (Keyboard.current.tabKey.wasPressedThisFrame)
        {
            cameraActive = !cameraActive;
            ActivateCamera(cameraActive);
        }

        if (!cameraActive)
            return; // skip input while paused

        // --- Look around ---
        Vector2 look = Mouse.current.delta.ReadValue();
        rotationX += look.x * lookSensitivity * Time.deltaTime;
        rotationY -= look.y * lookSensitivity * Time.deltaTime;
        rotationY = Mathf.Clamp(rotationY, -90f, 90f);
        transform.rotation = Quaternion.Euler(rotationY, rotationX, 0);

        // --- Movement ---
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

    void ActivateCamera(bool active)
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
