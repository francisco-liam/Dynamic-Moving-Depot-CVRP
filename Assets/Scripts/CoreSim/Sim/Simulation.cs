#nullable enable
using System;
using CoreSim.Events;
using CoreSim.Math;
using CoreSim.Model;

namespace CoreSim.Sim
{
    public sealed class Simulation
    {
        public SimState State { get; }
        public EventQueue Queue { get; } = new EventQueue();

        private readonly float _arriveEpsilon;

        public Simulation(SimState state, float arriveEpsilon = 0.1f)
        {
            State = state;
            _arriveEpsilon = arriveEpsilon;
        }

        public void Step(float dt)
        {
            if (dt <= 0f) return;

            float t0 = State.Time;
            float t1 = t0 + dt;

            // 1) Advance time
            State.Time = t1;

            // 2) Release customers (immediate fire when they become available)
            for (int i = 0; i < State.Customers.Count; i++)
            {
                var c = State.Customers[i];
                if (c.Status == CustomerStatus.Unreleased && c.ReleaseTime <= t1)
                {
                    c.Status = CustomerStatus.Waiting;
                    Queue.Enqueue(new SimEvent(t1, SimEventType.CustomerReleased, a: c.Id));
                }
            }

            // 3) Move depot toward its target (if any)
            if (State.Features.HasFlag(CoreSim.IO.ProblemFeatures.MovingDepot))
                MoveDepot(dt);

            // 4) Move trucks toward their targets (if any)
            for (int i = 0; i < State.Trucks.Count; i++)
            {
                var truck = State.Trucks[i];

                if (truck.State == TruckState.Servicing)
                {
                    UpdateService(truck, dt);
                    continue;
                }

                if (!EnsureTruckTarget(truck))
                {
                    truck.State = TruckState.Idle;
                    continue;
                }

                var target = truck.CurrentTarget;
                if (target == null)
                {
                    truck.State = TruckState.Idle;
                    continue;
                }

                if (TryStartServiceAtTarget(truck, target.Value))
                    continue;

                bool arrived = MoveTruck(truck, dt);
                if (arrived)
                    ProcessArrival(truck, target.Value, arrivedNow: true);
            }

#if DEBUG
            RunDebugChecks();
#endif
        }

        private void MoveDepot(float dt)
        {
            var depot = State.Depot;
            if (depot.TargetPos == null) return;

            Vec2 target = depot.TargetPos.Value;
            float maxDist = depot.Speed * dt;
            Vec2 newPos = MoveToward(depot.Pos, target, maxDist);

            depot.Pos = newPos;

            if (Vec2.Distance(newPos, target) <= _arriveEpsilon)
            {
                // Snap + emit arrival
                depot.Pos = target;
                Queue.Enqueue(new SimEvent(State.Time, SimEventType.DepotArrived, a: depot.TargetStopId));
                depot.TargetPos = null;
                depot.TargetStopId = -1;
            }
        }

        private bool MoveTruck(Truck truck, float dt)
        {
            if (truck.TargetPos == null) return false;

            Vec2 target = truck.TargetPos.Value;
            float maxDist = truck.Speed * dt;
            Vec2 newPos = MoveToward(truck.Pos, target, maxDist);

            float moveDist = Vec2.Distance(truck.Pos, newPos);

            truck.Pos = newPos;

            if (State.Features.HasFlag(CoreSim.IO.ProblemFeatures.Electric) && truck.EnergyConsumption > 0f)
            {
                float energyUsed = moveDist * truck.EnergyConsumption;
                truck.Battery = System.Math.Max(0f, truck.Battery - energyUsed);
                Queue.Enqueue(new SimEvent(State.Time, SimEventType.TruckEnergyChanged, a: truck.Id, b: (int)System.Math.Round(truck.Battery)));
            }

            if (Vec2.Distance(newPos, target) <= _arriveEpsilon)
            {
                truck.Pos = target;
                return true;
            }

            return false;
        }

        private bool EnsureTruckTarget(Truck truck)
        {
            if (truck.TargetPos != null)
                return true;

            if (!truck.HasPlanTarget)
                return false;

            var target = truck.CurrentTarget;
            if (target == null)
                return false;

            if (!TryResolveTargetPos(target.Value, out var pos))
                return false;

            if (truck.ActiveTarget == null || !truck.ActiveTarget.Value.Equals(target.Value))
            {
                truck.ActiveTarget = target.Value;
                truck.ArrivedOnActiveTarget = false;
            }

            truck.TargetPos = pos;
            truck.TargetId = target.Value.Id;
            truck.State = TruckState.Traveling;
            return true;
        }

        private bool TryResolveTargetPos(TargetRef target, out Vec2 pos)
        {
            if (target.Type == TargetType.Depot)
            {
                pos = State.Depot.Pos;
                return true;
            }

            if (target.Type == TargetType.Customer)
            {
                var customer = State.GetCustomerById(target.Id);
                if (customer != null)
                {
                    pos = customer.Pos;
                    return true;
                }
            }

            if (target.Type == TargetType.Station)
            {
                if (State.StationPositions.TryGetValue(target.Id, out var stationPos))
                {
                    pos = stationPos;
                    return true;
                }
            }

            pos = default;
            return false;
        }

        private bool TryStartServiceAtTarget(Truck truck, TargetRef target)
        {
            if (truck.TargetPos == null) return false;
            if (target.Type != TargetType.Customer) return false;

            float dist = Vec2.Distance(truck.Pos, truck.TargetPos.Value);
            if (dist > _arriveEpsilon) return false;

            var customer = State.GetCustomerById(target.Id);
            if (customer == null) return false;

            if (customer.Status == CustomerStatus.Unreleased)
            {
                if (!truck.ArrivedOnActiveTarget)
                    Queue.Enqueue(new SimEvent(State.Time, SimEventType.TruckArrived, a: truck.Id, b: target.Id));

                truck.ArrivedOnActiveTarget = true;
                truck.State = TruckState.Idle;
                Warn($"Truck {truck.Id} at customer {customer.Id} before release time.");
                return true;
            }

            if (customer.Status == CustomerStatus.Served)
            {
                if (!truck.ArrivedOnActiveTarget)
                    Queue.Enqueue(new SimEvent(State.Time, SimEventType.TruckArrived, a: truck.Id, b: target.Id));

                truck.ArrivedOnActiveTarget = true;
                Warn($"Truck {truck.Id} attempted to serve already served customer {customer.Id}.");
                AdvancePlan(truck);
                return true;
            }

            if (customer.Status != CustomerStatus.Waiting)
            {
                Warn($"Invalid customer state transition to InService. Customer {customer.Id} status={customer.Status}.");
                return true;
            }

            if (!truck.ArrivedOnActiveTarget)
                Queue.Enqueue(new SimEvent(State.Time, SimEventType.TruckArrived, a: truck.Id, b: target.Id));

            truck.ArrivedOnActiveTarget = true;
            truck.TargetPos = null;
            truck.TargetId = -1;

            StartService(truck, customer);
            return true;
        }

        private void ProcessArrival(Truck truck, TargetRef target, bool arrivedNow)
        {
            bool firstArrival = arrivedNow && !truck.ArrivedOnActiveTarget;
            if (firstArrival)
            {
                truck.ArrivedOnActiveTarget = true;
                Queue.Enqueue(new SimEvent(State.Time, SimEventType.TruckArrived, a: truck.Id, b: target.Id));
            }

            truck.TargetPos = null;
            truck.TargetId = -1;

            if (!firstArrival && target.Type != TargetType.Customer)
            {
                truck.State = TruckState.Idle;
                return;
            }

            if (target.Type == TargetType.Customer)
            {
                var customer = State.GetCustomerById(target.Id);
                if (customer == null)
                {
                    Warn($"Truck {truck.Id} arrived at missing customer {target.Id}.");
                    return;
                }

                if (customer.Status == CustomerStatus.Unreleased)
                {
                    Warn($"Truck {truck.Id} arrived at unreleased customer {customer.Id}.");
                    truck.State = TruckState.Idle;
                    return;
                }

                if (customer.Status == CustomerStatus.Served)
                {
                    Warn($"Truck {truck.Id} arrived at served customer {customer.Id}.");
                    truck.State = TruckState.Idle;
                    AdvancePlan(truck);
                    return;
                }

                if (customer.Status != CustomerStatus.Waiting)
                {
                    Warn($"Invalid customer state on arrival. Customer {customer.Id} status={customer.Status}.");
                    truck.State = TruckState.Idle;
                    return;
                }

                StartService(truck, customer);
                return;
            }

            if (arrivedNow)
                AdvancePlan(truck);
            else
                truck.State = TruckState.Idle;
        }

        private void StartService(Truck truck, Customer customer)
        {
            customer.Status = CustomerStatus.InService;
            customer.AssignedTruckId = truck.Id;

            truck.State = TruckState.Servicing;
            truck.ServicingCustomerId = customer.Id;
            truck.ServiceRemaining = customer.ServiceTime;

            if (truck.ServiceRemaining <= 0f)
                CompleteService(truck);
        }

        private void UpdateService(Truck truck, float dt)
        {
            if (truck.ServicingCustomerId < 0)
                return;

            truck.ServiceRemaining -= dt;
            if (truck.ServiceRemaining <= 0f)
                CompleteService(truck);
        }

        private void CompleteService(Truck truck)
        {
            var customer = State.GetCustomerById(truck.ServicingCustomerId);
            if (customer != null && customer.Status == CustomerStatus.InService)
            {
                customer.Status = CustomerStatus.Served;
                customer.AssignedTruckId = truck.Id;
                truck.Load += customer.Demand;
                if (truck.Capacity > 0 && truck.Load > truck.Capacity)
                    Warn($"Truck {truck.Id} capacity exceeded. Load={truck.Load}, Capacity={truck.Capacity}.");

                Queue.Enqueue(new SimEvent(State.Time, SimEventType.CustomerServed, a: truck.Id, b: customer.Id));
            }
            else
            {
                Warn($"Invalid service completion. Truck {truck.Id} customer={truck.ServicingCustomerId}.");
            }

            truck.State = TruckState.Idle;
            truck.ServiceRemaining = 0f;
            truck.ServicingCustomerId = -1;

            AdvancePlan(truck);
        }

        private void AdvancePlan(Truck truck)
        {
            truck.CurrentTargetIndex += 1;
            truck.ActiveTarget = null;
            truck.ArrivedOnActiveTarget = false;
            if (!truck.HasPlanTarget)
            {
                truck.State = TruckState.Idle;
                return;
            }

            truck.State = TruckState.Idle;
        }

        private void Warn(string message)
        {
            System.Diagnostics.Debug.WriteLine(message);
            try { Console.WriteLine(message); } catch { }
        }

#if DEBUG
        private void RunDebugChecks()
        {
            var servicingCounts = new System.Collections.Generic.Dictionary<int, int>();

            for (int i = 0; i < State.Trucks.Count; i++)
            {
                var t = State.Trucks[i];
                if (t.Load < 0)
                    throw new InvalidOperationException($"Truck {t.Id} load out of bounds: {t.Load}/{t.Capacity}.");

                if (t.Capacity > 0 && t.Load > t.Capacity)
                    throw new InvalidOperationException($"Truck {t.Id} load out of bounds: {t.Load}/{t.Capacity}.");

                if (t.State == TruckState.Servicing)
                {
                    if (t.ServicingCustomerId < 0)
                        throw new InvalidOperationException($"Truck {t.Id} servicing with invalid customer id.");

                    if (!servicingCounts.ContainsKey(t.ServicingCustomerId))
                        servicingCounts[t.ServicingCustomerId] = 0;
                    servicingCounts[t.ServicingCustomerId] += 1;
                }

                if (t.CurrentTargetIndex < 0 || t.CurrentTargetIndex > t.Plan.Count)
                    throw new InvalidOperationException($"Truck {t.Id} CurrentTargetIndex out of range.");
            }

            for (int i = 0; i < State.Customers.Count; i++)
            {
                var c = State.Customers[i];
                if (c.Status == CustomerStatus.InService)
                {
                    if (c.AssignedTruckId == null || c.AssignedTruckId.Value < 0)
                        throw new InvalidOperationException($"Customer {c.Id} InService without assigned truck.");

                    if (!servicingCounts.TryGetValue(c.Id, out var count) || count != 1)
                        throw new InvalidOperationException($"Customer {c.Id} InService but servicing count={count}.");
                }
            }
        }
#endif

        private static Vec2 MoveToward(Vec2 from, Vec2 to, float maxDist)
        {
            Vec2 delta = to - from;
            float dist = delta.Magnitude;
            if (dist <= 1e-6f) return to;
            if (dist <= maxDist) return to;

            float s = maxDist / dist;
            return new Vec2(from.X + delta.X * s, from.Y + delta.Y * s);
        }
    }
}