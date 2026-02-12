#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CoreSim.Events;
using CoreSim.Math;
using CoreSim.Model;

namespace CoreSim.IO
{
    public sealed class SnapshotData
    {
        public float Time { get; set; }
        public int Seed { get; set; }
        public ProblemFeatures Features { get; set; } = ProblemFeatures.None;
        public Vec2 DepotPos { get; set; }
        public float DepotSpeed { get; set; }

        public List<CustomerSnapshot> Customers { get; } = new List<CustomerSnapshot>();
        public List<TruckSnapshot> Trucks { get; } = new List<TruckSnapshot>();
        public List<SimEvent> Events { get; } = new List<SimEvent>();
    }

    public sealed class CustomerSnapshot
    {
        public int Id { get; set; }
        public Vec2 Pos { get; set; }
        public int Demand { get; set; }
        public float ReleaseTime { get; set; }
        public float ServiceTime { get; set; }
        public CustomerStatus Status { get; set; }
        public int AssignedTruckId { get; set; }
    }

    public sealed class TruckSnapshot
    {
        public int Id { get; set; }
        public Vec2 Pos { get; set; }
        public float Speed { get; set; }
        public int Capacity { get; set; }
        public int Load { get; set; }
        public TruckState State { get; set; }
        public float Battery { get; set; }
        public float BatteryCapacity { get; set; }
        public float EnergyConsumption { get; set; }
        public int LockedPrefixCount { get; set; }
        public int CurrentTargetIndex { get; set; }
        public List<TargetRef> Plan { get; } = new List<TargetRef>();
    }

    public static class SnapshotSerializer
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public static SnapshotData CreateFromState(SimState state, EventQueue queue, int seed)
        {
            var data = new SnapshotData
            {
                Time = state.Time,
                Seed = seed,
                Features = state.Features,
                DepotPos = state.Depot.Pos,
                DepotSpeed = state.Depot.Speed
            };

            foreach (var c in state.Customers)
            {
                data.Customers.Add(new CustomerSnapshot
                {
                    Id = c.Id,
                    Pos = c.Pos,
                    Demand = c.Demand,
                    ReleaseTime = c.ReleaseTime,
                    ServiceTime = c.ServiceTime,
                    Status = c.Status,
                    AssignedTruckId = c.AssignedTruckId ?? -1
                });
            }

            foreach (var t in state.Trucks)
            {
                var snap = new TruckSnapshot
                {
                    Id = t.Id,
                    Pos = t.Pos,
                    Speed = t.Speed,
                    Capacity = t.Capacity,
                    Load = t.Load,
                    State = t.State,
                    Battery = t.Battery,
                    BatteryCapacity = t.BatteryCapacity,
                    EnergyConsumption = t.EnergyConsumption,
                    LockedPrefixCount = t.LockedPrefixCount,
                    CurrentTargetIndex = t.CurrentTargetIndex
                };

                snap.Plan.AddRange(t.Plan);
                data.Trucks.Add(snap);
            }

            data.Events.AddRange(queue.ToList());
            return data;
        }

        public static void WriteToFile(string path, SnapshotData data)
        {
            using var writer = new StreamWriter(path, false);
            Write(writer, data);
        }

        public static SnapshotData ReadFromFile(string path)
        {
            using var reader = new StreamReader(path);
            return Read(reader);
        }

        public static void Write(TextWriter writer, SnapshotData data)
        {
            writer.WriteLine("# SNAPSHOT v1");
            writer.WriteLine($"time={F(data.Time)}");
            writer.WriteLine($"seed={data.Seed}");
            writer.WriteLine($"features={(int)data.Features}");
            writer.WriteLine($"depot={F(data.DepotPos.X)},{F(data.DepotPos.Y)},{F(data.DepotSpeed)}");

            writer.WriteLine($"customers={data.Customers.Count}");
            foreach (var c in data.Customers)
            {
                writer.WriteLine($"customer {c.Id} {F(c.Pos.X)} {F(c.Pos.Y)} {c.Demand} {F(c.ReleaseTime)} {F(c.ServiceTime)} {(int)c.Status} {c.AssignedTruckId}");
            }

            writer.WriteLine($"trucks={data.Trucks.Count}");
            foreach (var t in data.Trucks)
            {
                string plan = EncodePlan(t.Plan);
                writer.WriteLine($"truck {t.Id} {F(t.Pos.X)} {F(t.Pos.Y)} {F(t.Speed)} {t.Capacity} {t.Load} {(int)t.State} {F(t.Battery)} {F(t.BatteryCapacity)} {F(t.EnergyConsumption)} {t.LockedPrefixCount} {t.CurrentTargetIndex} {t.Plan.Count} {plan}");
            }

            writer.WriteLine($"events={data.Events.Count}");
            foreach (var e in data.Events)
            {
                writer.WriteLine($"event {F(e.Time)} {(int)e.Type} {e.A} {e.B}");
            }
        }

        public static SnapshotData Read(TextReader reader)
        {
            var data = new SnapshotData();
            string? line;

            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;

                if (line.StartsWith("time=", StringComparison.Ordinal))
                {
                    data.Time = ParseFloat(line.Substring(5));
                    continue;
                }
                if (line.StartsWith("seed=", StringComparison.Ordinal))
                {
                    data.Seed = ParseInt(line.Substring(5));
                    continue;
                }
                if (line.StartsWith("features=", StringComparison.Ordinal))
                {
                    data.Features = (ProblemFeatures)ParseInt(line.Substring(9));
                    continue;
                }
                if (line.StartsWith("depot=", StringComparison.Ordinal))
                {
                    var parts = line.Substring(6).Split(',');
                    if (parts.Length >= 3)
                    {
                        float x = ParseFloat(parts[0]);
                        float y = ParseFloat(parts[1]);
                        float spd = ParseFloat(parts[2]);
                        data.DepotPos = new Vec2(x, y);
                        data.DepotSpeed = spd;
                    }
                    continue;
                }

                if (line.StartsWith("customer ", StringComparison.Ordinal))
                {
                    var t = Split(line);
                    if (t.Length >= 9)
                    {
                        data.Customers.Add(new CustomerSnapshot
                        {
                            Id = ParseInt(t[1]),
                            Pos = new Vec2(ParseFloat(t[2]), ParseFloat(t[3])),
                            Demand = ParseInt(t[4]),
                            ReleaseTime = ParseFloat(t[5]),
                            ServiceTime = ParseFloat(t[6]),
                            Status = (CustomerStatus)ParseInt(t[7]),
                            AssignedTruckId = ParseInt(t[8])
                        });
                    }
                    continue;
                }

                if (line.StartsWith("truck ", StringComparison.Ordinal))
                {
                    var t = Split(line);
                    if (t.Length >= 14)
                    {
                        var snap = new TruckSnapshot
                        {
                            Id = ParseInt(t[1]),
                            Pos = new Vec2(ParseFloat(t[2]), ParseFloat(t[3])),
                            Speed = ParseFloat(t[4]),
                            Capacity = ParseInt(t[5]),
                            Load = ParseInt(t[6]),
                            State = (TruckState)ParseInt(t[7]),
                            Battery = ParseFloat(t[8]),
                            BatteryCapacity = ParseFloat(t[9]),
                            EnergyConsumption = ParseFloat(t[10]),
                            LockedPrefixCount = ParseInt(t[11]),
                            CurrentTargetIndex = ParseInt(t[12])
                        };

                        int planCount = ParseInt(t[13]);
                        string planEncoded = t.Length > 14 ? t[14] : "";
                        if (planCount > 0 && planEncoded.Length > 0)
                            snap.Plan.AddRange(DecodePlan(planEncoded));

                        data.Trucks.Add(snap);
                    }
                    continue;
                }

                if (line.StartsWith("event ", StringComparison.Ordinal))
                {
                    var t = Split(line);
                    if (t.Length >= 5)
                    {
                        data.Events.Add(new SimEvent(ParseFloat(t[1]), (SimEventType)ParseInt(t[2]), ParseInt(t[3]), ParseInt(t[4])));
                    }
                    continue;
                }
            }

            return data;
        }

        private static string[] Split(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        private static float ParseFloat(string s) => float.Parse(s, NumberStyles.Float, Invariant);

        private static int ParseInt(string s) => int.Parse(s, NumberStyles.Integer, Invariant);

        private static string F(float v) => v.ToString("0.###", Invariant);

        private static string EncodePlan(List<TargetRef> plan)
        {
            if (plan.Count == 0) return "";
            var parts = new string[plan.Count];
            for (int i = 0; i < plan.Count; i++)
            {
                var p = plan[i];
                char code = p.Type == TargetType.Depot ? 'D' : p.Type == TargetType.Station ? 'S' : 'C';
                parts[i] = $"{code}:{p.Id}";
            }
            return string.Join("|", parts);
        }

        private static List<TargetRef> DecodePlan(string encoded)
        {
            var list = new List<TargetRef>();
            var parts = encoded.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                var item = parts[i];
                if (string.IsNullOrWhiteSpace(item)) continue;
                var sub = item.Split(':');
                if (sub.Length != 2) continue;

                char code = sub[0].Length > 0 ? sub[0][0] : 'C';
                int id = ParseInt(sub[1]);
                if (code == 'D') list.Add(TargetRef.Depot(id));
                else if (code == 'S') list.Add(TargetRef.Station(id));
                else list.Add(TargetRef.Customer(id));
            }
            return list;
        }
    }
}
