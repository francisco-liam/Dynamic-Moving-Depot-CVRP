namespace CoreSim.Events
{
    public enum SimEventType
    {
        CustomerReleased = 0,
        TruckArrived = 1,
        DepotArrived = 2,
        TruckEnergyChanged = 3,

        // reserved for later:
        CustomerInserted = 10,
        ReplanRequested = 11
    }
}