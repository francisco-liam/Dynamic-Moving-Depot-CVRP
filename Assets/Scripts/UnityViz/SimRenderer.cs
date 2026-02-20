using System.Collections.Generic;
using UnityEngine;
using CoreSim.Math;
using CoreSim.Model;

public sealed class SimRenderer : MonoBehaviour
{
    [Header("Prefabs")]
    public GameObject customerPrefab;
    public GameObject truckPrefab;
    public GameObject depotPrefab;
    public GameObject stationPrefab;
    public GameObject targetMarkerPrefab;

    [Header("Route Rendering")]
    public LineRenderer routeLinePrefab;
    public LineRenderer lockedRouteLinePrefab;
    public bool showRoutes = true;
    public float routeY = 0.05f;
    public float markerY = 0.2f;

    [Header("Customer Materials")]
    public Material unreleasedMaterial;
    public Material waitingMaterial;
    public Material inServiceMaterial;
    public Material servedMaterial;

    private SimState _state;

    private readonly List<CustomerRender> _customers = new List<CustomerRender>();
    private readonly List<TruckRender> _trucks = new List<TruckRender>();
    private readonly List<GameObject> _stations = new List<GameObject>();
    private GameObject _depot;

    private Transform _root;
    private Transform _customersRoot;
    private Transform _trucksRoot;
    private Transform _stationsRoot;
    private Transform _linesRoot;
    private Transform _markersRoot;
    private Transform _depotRoot;

    public void SetState(SimState state)
    {
        _state = state;
        BuildAll();
    }

    public void SetShowRoutes(bool value)
    {
        showRoutes = value;
        for (int i = 0; i < _trucks.Count; i++)
        {
            if (_trucks[i].Route != null) _trucks[i].Route.gameObject.SetActive(showRoutes);
            if (_trucks[i].LockedRoute != null) _trucks[i].LockedRoute.gameObject.SetActive(showRoutes);
        }
    }

    public void Render(SimState state)
    {
        if (state == null) return;
        if (_state != state)
            SetState(state);
        else
            EnsureDynamicCounts(state);

        UpdateDepot();
        UpdateCustomers();
        UpdateStations();
        UpdateTrucks();
    }

    private void EnsureDynamicCounts(SimState state)
    {
        if (_customers.Count < state.Customers.Count)
            EnsureCustomers();

        if (_stations.Count < state.StationNodeIds.Count)
            EnsureStations();

        if (_trucks.Count < state.Trucks.Count)
            EnsureTrucks();
    }

    private void BuildAll()
    {
        if (_state == null) return;

        EnsureRoots();

        EnsureDepot();
        EnsureCustomers();
        EnsureStations();
        EnsureTrucks();
    }

    private void EnsureRoots()
    {
        if (_root == null)
            _root = GetOrCreateChild("RenderRoot", transform);

        _customersRoot = GetOrCreateChild("Customers", _root);
        _trucksRoot = GetOrCreateChild("Trucks", _root);
        _stationsRoot = GetOrCreateChild("Stations", _root);
        _linesRoot = GetOrCreateChild("Lines", _root);
        _markersRoot = GetOrCreateChild("Markers", _root);
        _depotRoot = GetOrCreateChild("Depot", _root);
    }

    private void EnsureDepot()
    {
        if (_depot == null && depotPrefab != null)
        {
            _depot = Instantiate(depotPrefab, _depotRoot != null ? _depotRoot : transform);
            _depot.name = "Depot";
        }

        UpdateDepot();
    }

    private void UpdateDepot()
    {
        if (_depot == null || _state == null) return;
        _depot.transform.position = UnityVec.ToUnity(_state.Depot.Pos);
    }

    private void EnsureCustomers()
    {
        if (_state == null || customerPrefab == null) return;

        while (_customers.Count < _state.Customers.Count)
        {
            var go = Instantiate(customerPrefab, _customersRoot != null ? _customersRoot : transform);
            var renderer = go.GetComponentInChildren<Renderer>();
            _customers.Add(new CustomerRender(go, renderer));
        }

        for (int i = 0; i < _customers.Count; i++)
            _customers[i].GameObject.SetActive(i < _state.Customers.Count);

        UpdateCustomers();
    }

    private void UpdateCustomers()
    {
        if (_state == null) return;

        for (int i = 0; i < _state.Customers.Count; i++)
        {
            var c = _state.Customers[i];
            var r = _customers[i];

            if (!r.GameObject.activeSelf)
                r.GameObject.SetActive(true);

            r.GameObject.transform.position = UnityVec.ToUnity(c.Pos);
            if (r.GameObject.name != $"Customer {c.Id}")
                r.GameObject.name = $"Customer {c.Id}";

            if (r.LastStatus != c.Status)
            {
                ApplyCustomerMaterial(r.Renderer, c.Status);
                r.LastStatus = c.Status;
            }
        }
    }

    private void EnsureStations()
    {
        if (_state == null || stationPrefab == null) return;

        int required = _state.StationNodeIds.Count;
        while (_stations.Count < required)
        {
            var go = Instantiate(stationPrefab, _stationsRoot != null ? _stationsRoot : transform);
            _stations.Add(go);
        }

        for (int i = 0; i < _stations.Count; i++)
            _stations[i].SetActive(i < required);

        UpdateStations();
    }

    private void UpdateStations()
    {
        if (_state == null) return;

        for (int i = 0; i < _state.StationNodeIds.Count; i++)
        {
            int id = _state.StationNodeIds[i];
            if (_state.StationPositions.TryGetValue(id, out var pos))
                _stations[i].transform.position = UnityVec.ToUnity(pos);
            if (_stations[i].name != $"Station {id}")
                _stations[i].name = $"Station {id}";
        }
    }

    private void EnsureTrucks()
    {
        if (_state == null || truckPrefab == null) return;

        while (_trucks.Count < _state.Trucks.Count)
        {
            var go = Instantiate(truckPrefab, _trucksRoot != null ? _trucksRoot : transform);

            LineRenderer route = null;
            LineRenderer lockedRoute = null;
            if (routeLinePrefab != null)
                route = Instantiate(routeLinePrefab, _linesRoot != null ? _linesRoot : transform);
            if (lockedRouteLinePrefab != null)
                lockedRoute = Instantiate(lockedRouteLinePrefab, _linesRoot != null ? _linesRoot : transform);

            GameObject marker = null;
            if (targetMarkerPrefab != null)
                marker = Instantiate(targetMarkerPrefab, _markersRoot != null ? _markersRoot : transform);

            _trucks.Add(new TruckRender(go, route, lockedRoute, marker));
        }

        for (int i = 0; i < _trucks.Count; i++)
        {
            bool active = i < _state.Trucks.Count;
            _trucks[i].GameObject.SetActive(active);
            if (_trucks[i].Route != null) _trucks[i].Route.gameObject.SetActive(active && showRoutes);
            if (_trucks[i].LockedRoute != null) _trucks[i].LockedRoute.gameObject.SetActive(active && showRoutes);
            if (_trucks[i].TargetMarker != null) _trucks[i].TargetMarker.SetActive(active);
        }

        UpdateTrucks();
    }

    private void UpdateTrucks()
    {
        if (_state == null) return;

        for (int i = 0; i < _state.Trucks.Count; i++)
        {
            var truck = _state.Trucks[i];
            var render = _trucks[i];

            render.GameObject.transform.position = UnityVec.ToUnity(truck.Pos);
            if (render.GameObject.name != $"Truck {truck.Id}")
                render.GameObject.name = $"Truck {truck.Id}";
            if (render.Route != null && render.Route.gameObject.name != $"Route Truck {truck.Id}")
                render.Route.gameObject.name = $"Route Truck {truck.Id}";
            if (render.LockedRoute != null && render.LockedRoute.gameObject.name != $"Locked Route Truck {truck.Id}")
                render.LockedRoute.gameObject.name = $"Locked Route Truck {truck.Id}";
            if (render.TargetMarker != null && render.TargetMarker.name != $"Target Marker Truck {truck.Id}")
                render.TargetMarker.name = $"Target Marker Truck {truck.Id}";

            UpdateTruckRoutes(truck, render);
            UpdateTargetMarker(truck, render);
        }
    }

    private void UpdateTruckRoutes(Truck truck, TruckRender render)
    {
        if (!showRoutes || render.Route == null || render.LockedRoute == null)
            return;

        int remaining = truck.Plan.Count - truck.CurrentTargetIndex;
        if (remaining <= 0)
        {
            render.Route.positionCount = 0;
            render.LockedRoute.positionCount = 0;
            return;
        }

        int lockedCount = Mathf.Clamp(truck.LockedPrefixCount, 0, remaining);
        int unlockedPoints = remaining - lockedCount;

        Vec2 lastPos = truck.Pos;
        if (lockedCount > 0)
        {
            int lockedPoints = lockedCount + 1;
            EnsureBuffer(ref render.LockedBuffer, lockedPoints);
            int lockedIndex = 0;
            render.LockedBuffer[lockedIndex++] = UnityVec.ToUnity(truck.Pos, routeY);

            for (int i = 0; i < lockedCount; i++)
            {
                var target = truck.Plan[truck.CurrentTargetIndex + i];
                if (TryResolveTargetPos(target, out var pos))
                {
                    lastPos = pos;
                    render.LockedBuffer[lockedIndex++] = UnityVec.ToUnity(pos, routeY);
                }
            }

            render.LockedRoute.positionCount = lockedIndex;
            for (int i = 0; i < lockedIndex; i++)
                render.LockedRoute.SetPosition(i, render.LockedBuffer[i]);
        }
        else
        {
            render.LockedRoute.positionCount = 0;
        }

        EnsureBuffer(ref render.RouteBuffer, unlockedPoints + 1);

        int routeIndex = 0;
        render.RouteBuffer[routeIndex++] = UnityVec.ToUnity(lastPos, routeY);
        for (int i = lockedCount; i < remaining; i++)
        {
            var target = truck.Plan[truck.CurrentTargetIndex + i];
            if (TryResolveTargetPos(target, out var pos))
                render.RouteBuffer[routeIndex++] = UnityVec.ToUnity(pos, routeY);
        }

        render.Route.positionCount = routeIndex;
        for (int i = 0; i < routeIndex; i++)
            render.Route.SetPosition(i, render.RouteBuffer[i]);
    }

    private void UpdateTargetMarker(Truck truck, TruckRender render)
    {
        if (render.TargetMarker == null)
            return;

        var target = truck.CurrentTarget;
        if (target == null || !TryResolveTargetPos(target.Value, out var pos))
        {
            render.TargetMarker.SetActive(false);
            return;
        }

        render.TargetMarker.SetActive(true);
        render.TargetMarker.transform.position = UnityVec.ToUnity(pos, markerY);
    }

    private bool TryResolveTargetPos(TargetRef target, out Vec2 pos)
    {
        if (_state == null)
        {
            pos = default;
            return false;
        }

        if (target.Type == TargetType.Depot)
        {
            pos = _state.Depot.Pos;
            return true;
        }

        if (target.Type == TargetType.Customer)
        {
            var customer = _state.GetCustomerById(target.Id);
            if (customer != null)
            {
                pos = customer.Pos;
                return true;
            }
        }

        if (target.Type == TargetType.Station)
        {
            if (_state.StationPositions.TryGetValue(target.Id, out var stationPos))
            {
                pos = stationPos;
                return true;
            }
        }

        pos = default;
        return false;
    }

    private void ApplyCustomerMaterial(Renderer renderer, CustomerStatus status)
    {
        if (renderer == null) return;

        switch (status)
        {
            case CustomerStatus.Unreleased:
                if (unreleasedMaterial != null) renderer.sharedMaterial = unreleasedMaterial;
                break;
            case CustomerStatus.Waiting:
                if (waitingMaterial != null) renderer.sharedMaterial = waitingMaterial;
                break;
            case CustomerStatus.InService:
                if (inServiceMaterial != null) renderer.sharedMaterial = inServiceMaterial;
                break;
            case CustomerStatus.Served:
                if (servedMaterial != null) renderer.sharedMaterial = servedMaterial;
                break;
        }
    }

    private static void EnsureBuffer(ref Vector3[] buffer, int size)
    {
        if (buffer == null || buffer.Length < size)
            buffer = new Vector3[size];
    }

    private static Transform GetOrCreateChild(string name, Transform parent)
    {
        var existing = parent.Find(name);
        if (existing != null)
            return existing;

        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        return go.transform;
    }

    private sealed class CustomerRender
    {
        public GameObject GameObject { get; }
        public Renderer Renderer { get; }
        public CustomerStatus LastStatus { get; set; } = (CustomerStatus)(-1);

        public CustomerRender(GameObject go, Renderer renderer)
        {
            GameObject = go;
            Renderer = renderer;
        }
    }

    private sealed class TruckRender
    {
        public GameObject GameObject { get; }
        public LineRenderer Route { get; }
        public LineRenderer LockedRoute { get; }
        public GameObject TargetMarker { get; }
        public Vector3[] RouteBuffer;
        public Vector3[] LockedBuffer;

        public TruckRender(GameObject go, LineRenderer route, LineRenderer lockedRoute, GameObject targetMarker)
        {
            GameObject = go;
            Route = route;
            LockedRoute = lockedRoute;
            TargetMarker = targetMarker;
        }
    }
}
