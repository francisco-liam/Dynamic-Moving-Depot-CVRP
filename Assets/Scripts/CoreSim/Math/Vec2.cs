namespace CoreSim.Math
{
    public readonly struct Vec2
    {
        public readonly float X;
        public readonly float Y; // this maps to Unity's Z

        public Vec2(float x, float y) { X = x; Y = y; }

        public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
        public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);

        public float SqrMagnitude => X * X + Y * Y;
        public float Magnitude => (float)System.Math.Sqrt(SqrMagnitude);
        public static float Distance(Vec2 a, Vec2 b) => (a - b).Magnitude;
    }
}