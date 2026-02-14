using UnityEngine;
using UnityEngine.EventSystems;
using CoreSim.Math;
using CoreSim.Model;

public sealed class SimInputController : MonoBehaviour
{
    [Header("References")]
    public SimViewController controller;
    public Camera mainCamera;

    [Header("Insert Customer")]
    public bool insertMode = false;
    public int defaultDemand = 1;
    public float defaultServiceTime = 1f;

    private readonly Plane _groundPlane = new Plane(Vector3.up, Vector3.zero);

    private void Awake()
    {
        if (controller == null)
            controller = FindAnyObjectByType<SimViewController>();
        if (mainCamera == null)
            mainCamera = Camera.main;
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.I))
            ToggleInsertMode();

        if (!insertMode)
            return;

        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            return;

        if (Input.GetMouseButtonDown(0))
            TryInsertAtMouse();
    }

    public void SetInsertMode(bool value)
    {
        insertMode = value;
    }

    public void ToggleInsertMode()
    {
        insertMode = !insertMode;
    }

    public void SetDefaultDemand(int demand)
    {
        defaultDemand = Mathf.Max(1, demand);
    }

    public void SetDefaultServiceTime(float serviceTime)
    {
        defaultServiceTime = Mathf.Max(0f, serviceTime);
    }

    private void TryInsertAtMouse()
    {
        if (controller == null || controller.State == null || mainCamera == null)
            return;

        Ray ray = mainCamera.ScreenPointToRay(Input.mousePosition);
        if (_groundPlane.Raycast(ray, out float enter))
        {
            Vector3 hit = ray.GetPoint(enter);
            var spec = new CustomerSpec(new Vec2(hit.x, hit.z))
            {
                Demand = defaultDemand,
                ReleaseTime = controller.State.Time,
                ServiceTime = defaultServiceTime
            };

            controller.InsertCustomer(spec);
        }
    }
}
