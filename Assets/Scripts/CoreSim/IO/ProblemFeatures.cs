using System;

namespace CoreSim.IO
{
    [Flags]
    public enum ProblemFeatures
    {
        None = 0,
        Capacitated = 1 << 0,   // CAPACITY + DEMAND_SECTION (classic VRP)
        Electric = 1 << 1,      // ENERGY_CAPACITY / STATIONS / ENERGY_CONSUMPTION / station section present
        Dynamic = 1 << 2,       // RELEASE_TIME_SECTION present (or any release time > 0)
        MovingDepot = 1 << 3    // DEPOT_SPEED + depot stop section present (or any depot stop section)
    }
}