using CoreSim.Model;
using UnityEngine;
using UnityEngine.EventSystems;

public sealed class SimCameraController : MonoBehaviour
{
    [Header("Framing")]
    public bool autoFrameOnReset = true;
    public float minFrameDistance = 30f;
    public float framePadding = 1.35f;
    public float initialPitch = 40f;
    public float initialYaw = 45f;

    [Header("Movement")]
    public float moveSpeed = 20f;
    public float verticalMoveSpeed = 20f;
    public float fastMoveMultiplier = 3f;

    [Header("Zoom")]
    public float zoomSpeed = 120f;
    public float minHeight = 2f;
    public float maxHeight = 500f;

    [Header("Look")]
    public float lookSensitivity = 3f;
    public float minPitch = -85f;
    public float maxPitch = 85f;

    private float _yaw;
    private float _pitch;

    private void Start()
    {
        Vector3 euler = transform.rotation.eulerAngles;
        _yaw = euler.y;
        _pitch = NormalizePitch(euler.x);
    }

    private void Update()
    {
        HandleLook();
        HandleZoom();
        HandleMove();
    }

    public void FrameState(SimState state)
    {
        if (state == null)
            return;

        Vector3 center = new Vector3(state.Depot.Pos.X, 0f, state.Depot.Pos.Y);
        float minX = center.x;
        float maxX = center.x;
        float minZ = center.z;
        float maxZ = center.z;

        for (int i = 0; i < state.Customers.Count; i++)
        {
            var p = state.Customers[i].Pos;
            minX = Mathf.Min(minX, p.X);
            maxX = Mathf.Max(maxX, p.X);
            minZ = Mathf.Min(minZ, p.Y);
            maxZ = Mathf.Max(maxZ, p.Y);
        }

        center = new Vector3((minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f);
        float span = Mathf.Max(maxX - minX, maxZ - minZ);
        float distance = Mathf.Max(minFrameDistance, span * framePadding);

        _yaw = initialYaw;
        _pitch = Mathf.Clamp(initialPitch, minPitch, maxPitch);

        Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
        transform.rotation = rot;
        transform.position = center - (rot * Vector3.forward * distance);
    }

    private void HandleMove()
    {
        if (IsTypingInInputField())
            return;

        float x = 0f;
        float y = 0f;
        float z = 0f;

        if (Input.GetKey(KeyCode.A)) x -= 1f;
        if (Input.GetKey(KeyCode.D)) x += 1f;
        if (Input.GetKey(KeyCode.Q)) y -= 1f;
        if (Input.GetKey(KeyCode.E)) y += 1f;
        if (Input.GetKey(KeyCode.S)) z -= 1f;
        if (Input.GetKey(KeyCode.W)) z += 1f;

        if (x == 0f && y == 0f && z == 0f)
            return;

        float planarSpeed = moveSpeed;
        float verticalSpeed = verticalMoveSpeed;
        if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
        {
            planarSpeed *= fastMoveMultiplier;
            verticalSpeed *= fastMoveMultiplier;
        }

        Vector3 forward = transform.forward;
        Vector3 right = transform.right;
        forward.y = 0f;
        right.y = 0f;
        if (forward.sqrMagnitude > 1e-6f) forward.Normalize();
        if (right.sqrMagnitude > 1e-6f) right.Normalize();

        Vector3 planar = (forward * z) + (right * x);
        if (planar.sqrMagnitude > 1e-6f) planar.Normalize();

        float dt = Time.unscaledDeltaTime;
        Vector3 delta = (planar * planarSpeed * dt) + (Vector3.up * y * verticalSpeed * dt);
        transform.position += delta;
        ClampHeight();
    }

    private void HandleZoom()
    {
        if (IsTypingInInputField())
            return;

        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            return;

        float scroll = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(scroll) <= 1e-5f)
            return;

        float dt = Time.unscaledDeltaTime;
        transform.position += transform.forward * (scroll * zoomSpeed * dt);
        ClampHeight();
    }

    private void HandleLook()
    {
        if (!Input.GetMouseButton(2))
            return;

        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            return;

        float mouseX = Input.GetAxis("Mouse X");
        float mouseY = Input.GetAxis("Mouse Y");

        _yaw += mouseX * lookSensitivity;
        _pitch -= mouseY * lookSensitivity;
        _pitch = Mathf.Clamp(_pitch, minPitch, maxPitch);

        transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
    }

    private static float NormalizePitch(float x)
    {
        if (x > 180f)
            x -= 360f;
        return x;
    }

    private void ClampHeight()
    {
        Vector3 p = transform.position;
        p.y = Mathf.Clamp(p.y, minHeight, maxHeight);
        transform.position = p;
    }

    private static bool IsTypingInInputField()
    {
        if (EventSystem.current == null || EventSystem.current.currentSelectedGameObject == null)
            return false;

        var selected = EventSystem.current.currentSelectedGameObject;
        return selected.GetComponent<TMPro.TMP_InputField>() != null;
    }
}
