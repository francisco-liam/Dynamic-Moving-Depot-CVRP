#nullable enable

namespace CoreSim.Model
{
    public enum TargetType
    {
        Depot = 0,
        Customer = 1,
        Station = 2
    }

    public readonly struct TargetRef
    {
        public readonly TargetType Type;
        public readonly int Id;

        public TargetRef(TargetType type, int id)
        {
            Type = type;
            Id = id;
        }

        public static TargetRef Depot(int id = 1) => new TargetRef(TargetType.Depot, id);
        public static TargetRef Customer(int id) => new TargetRef(TargetType.Customer, id);
        public static TargetRef Station(int id) => new TargetRef(TargetType.Station, id);

        public override string ToString() => $"{Type}:{Id}";
    }
}
