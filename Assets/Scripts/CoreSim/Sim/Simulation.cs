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
                    c.Status = CustomerStatus.Available;
                    Queue.Enqueue(new SimEvent(t1, SimEventType.CustomerReleased, a: c.Id));
                }
            }

            // 3) Move depot toward its target (if any)
            if (State.Features.HasFlag(CoreSim.IO.ProblemFeatures.MovingDepot))
                MoveDepot(dt);

            // 4) Move trucks toward their targets (if any)
            for (int i = 0; i < State.Trucks.Count; i++)
            {
                MoveTruck(State.Trucks[i], dt);
            }

            // 5) Fire due events (time-ordered) up to current time
            // In Phase 2 we mostly enqueue "now" events; still good to have the mechanism.
            while (Queue.TryPeek(out var e) && e.Time <= State.Time + 1e-6f)
            {
                Queue.TryDequeue(out e);
                // For now, we don't mutate state here; we just expose events to UnityViz/logger.
                // Later we can route these to handlers.
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

        private void MoveTruck(Truck truck, float dt)
        {
            if (truck.TargetPos == null) return;

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
                truck.TargetPos = null;
                truck.TargetId = -1;
            }
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