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

                bool arrived = MoveTruck(truck, dt);
                if (arrived)
                    HandleArrival(truck);
            }
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
                Queue.Enqueue(new SimEvent(State.Time, SimEventType.TruckArrived, a: truck.Id, b: truck.TargetId));
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

        private void HandleArrival(Truck truck)
        {
            var target = truck.CurrentTarget;
            truck.TargetPos = null;
            truck.TargetId = -1;

            if (target == null)
            {
                truck.State = TruckState.Idle;
                return;
            }

            if (target.Value.Type == TargetType.Customer)
            {
                var customer = State.GetCustomerById(target.Value.Id);
                if (customer == null || customer.Status == CustomerStatus.Served)
                {
                    AdvancePlan(truck);
                    return;
                }

                customer.Status = CustomerStatus.InService;
                customer.AssignedTruckId = truck.Id;

                truck.State = TruckState.Servicing;
                truck.ServicingCustomerId = customer.Id;
                truck.ServiceRemaining = customer.ServiceTime;

                if (truck.ServiceRemaining <= 0f)
                    CompleteService(truck);

                return;
            }

            AdvancePlan(truck);
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
            if (customer != null)
            {
                customer.Status = CustomerStatus.Served;
                customer.AssignedTruckId = truck.Id;
                truck.Load += customer.Demand;
                Queue.Enqueue(new SimEvent(State.Time, SimEventType.CustomerServed, a: customer.Id, b: truck.Id));
            }

            truck.State = TruckState.Idle;
            truck.ServiceRemaining = 0f;
            truck.ServicingCustomerId = -1;

            AdvancePlan(truck);
        }

        private void AdvancePlan(Truck truck)
        {
            truck.CurrentTargetIndex += 1;
            if (!truck.HasPlanTarget)
            {
                truck.State = TruckState.Idle;
                return;
            }

            truck.State = TruckState.Idle;
        }

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