using CoreSim.Math;
using UnityEngine;

public static class UnityVec
{
    public static Vector3 ToUnity(Vec2 v, float y = 0f)
    {
        return new Vector3(v.X, y, v.Y);
    }
}
