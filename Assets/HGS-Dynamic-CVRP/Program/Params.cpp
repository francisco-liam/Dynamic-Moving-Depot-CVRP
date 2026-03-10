#include "Params.h"

// The universal constructor for both executable and shared library
// When the executable is run from the commandline,
// it will first generate an CVRPLIB instance from .vrp file, then supply necessary information.
Params::Params(
	const std::vector<double>& x_coords,
	const std::vector<double>& y_coords,
	const std::vector<std::vector<double>>& dist_mtx,
	const std::vector<double>& service_time,
	const std::vector<double>& demands,
	double vehicleCapacity,
	double durationLimit,
	int nbVeh,
	bool isDurationConstraint,
	bool verbose,
	const AlgorithmParameters& ap
)
	: ap(ap), isDurationConstraint(isDurationConstraint), nbVehicles(nbVeh), durationLimit(durationLimit),
	  vehicleCapacity(vehicleCapacity), timeCost(dist_mtx), verbose(verbose)
{
	// This marks the starting time of the algorithm
	startTime = clock();

	nbClients = (int)demands.size() - 1; // Need to substract the depot from the number of nodes
	totalDemand = 0.;
	maxDemand = 0.;

	// Initialize RNG
	ran.seed(ap.seed);

	// check if valid coordinates are provided
	areCoordinatesProvided = (demands.size() == x_coords.size()) && (demands.size() == y_coords.size());

	cli = std::vector<Client>(nbClients + 1);
	for (int i = 0; i <= nbClients; i++)
	{
		// If useSwapStar==false, x_coords and y_coords may be empty.
		if (ap.useSwapStar == 1 && areCoordinatesProvided)
		{
			cli[i].coordX = x_coords[i];
			cli[i].coordY = y_coords[i];
			cli[i].polarAngle = CircleSector::positive_mod(
				32768. * atan2(cli[i].coordY - cli[0].coordY, cli[i].coordX - cli[0].coordX) / PI);
		}
		else
		{
			cli[i].coordX = 0.0;
			cli[i].coordY = 0.0;
			cli[i].polarAngle = 0.0;
		}

		cli[i].serviceDuration = service_time[i];
		cli[i].demand = demands[i];
		if (cli[i].demand > maxDemand) maxDemand = cli[i].demand;
		totalDemand += cli[i].demand;
	}

	if (verbose && ap.useSwapStar == 1 && !areCoordinatesProvided)
		std::cout << "----- NO COORDINATES HAVE BEEN PROVIDED, SWAP* NEIGHBORHOOD WILL BE DEACTIVATED BY DEFAULT" << std::endl;

	// Default initialization if the number of vehicles has not been provided by the user
	if (nbVehicles == INT_MAX)
	{
		nbVehicles = (int)std::ceil(1.3*totalDemand/vehicleCapacity) + 3;  // Safety margin: 30% + 3 more vehicles than the trivial bin packing LB
		if (verbose) 
			std::cout << "----- FLEET SIZE WAS NOT SPECIFIED: DEFAULT INITIALIZATION TO " << nbVehicles << " VEHICLES" << std::endl;
	}
	else
	{
		if (verbose)
			std::cout << "----- FLEET SIZE SPECIFIED: SET TO " << nbVehicles << " VEHICLES" << std::endl;
	}

	// Calculation of the maximum distance
	maxDist = 0.;
	for (int i = 0; i <= nbClients; i++)
		for (int j = 0; j <= nbClients; j++)
			if (timeCost[i][j] > maxDist) maxDist = timeCost[i][j];

	// Calculation of the correlated vertices for each customer (for the granular restriction)
	correlatedVertices = std::vector<std::vector<int> >(nbClients + 1);
	std::vector<std::set<int> > setCorrelatedVertices = std::vector<std::set<int> >(nbClients + 1);
	std::vector<std::pair<double, int> > orderProximity;
	for (int i = 1; i <= nbClients; i++)
	{
		orderProximity.clear();
		for (int j = 1; j <= nbClients; j++)
			if (i != j) orderProximity.emplace_back(timeCost[i][j], j);
		std::sort(orderProximity.begin(), orderProximity.end());

		for (int j = 0; j < std::min<int>(ap.nbGranular, nbClients - 1); j++)
		{
			// If i is correlated with j, then j should be correlated with i
			setCorrelatedVertices[i].insert(orderProximity[j].second);
			setCorrelatedVertices[orderProximity[j].second].insert(i);
		}
	}

	// Filling the vector of correlated vertices
	for (int i = 1; i <= nbClients; i++)
		for (int x : setCorrelatedVertices[i])
			correlatedVertices[i].push_back(x);

	// Default dynamic behavior is a no-op: all customers active and no locked prefixes.
	resetDynamicState();

	// Safeguards to avoid possible numerical instability in case of instances containing arbitrarily small or large numerical values
	if (maxDist < 0.1 || maxDist > 100000)
		throw std::string(
			"The distances are of very small or large scale. This could impact numerical stability. Please rescale the dataset and run again.");
	if (maxDemand < 0.1 || maxDemand > 100000)
		throw std::string(
			"The demand quantities are of very small or large scale. This could impact numerical stability. Please rescale the dataset and run again.");
	if (nbVehicles < std::ceil(totalDemand / vehicleCapacity))
		throw std::string("Fleet size is insufficient to service the considered clients.");

	// A reasonable scale for the initial values of the penalties
	penaltyDuration = 1;
	penaltyCapacity = std::max<double>(0.1, std::min<double>(1000., maxDist / maxDemand));

	if (verbose)
		std::cout << "----- INSTANCE SUCCESSFULLY LOADED WITH " << nbClients << " CLIENTS AND " << nbVehicles << " VEHICLES" << std::endl;
}

void Params::refreshActiveData()
{
	activeCustomers.clear();
	totalDemandActive = 0.;
	for (int i = 1; i <= nbClients; i++)
	{
		if (customerActive[i])
		{
			activeCustomers.push_back(i);
			totalDemandActive += cli[i].demand;
		}
	}
	nbActiveCustomers = (int)activeCustomers.size();

	correlatedVerticesActive = std::vector<std::vector<int> >(nbClients + 1);
	for (int i = 1; i <= nbClients; i++)
	{
		for (int x : correlatedVertices[i])
			if (customerActive[x]) correlatedVerticesActive[i].push_back(x);
	}
}

void Params::resetDynamicState()
{
	dynamicModeEnabled = false;
	customerActive = std::vector<bool>(nbClients + 1, true);
	customerActive[0] = false;
	dynamicVehicleStates = std::vector<DynamicVehicleState>(nbVehicles);
	for (int r = 0; r < nbVehicles; r++)
	{
		dynamicVehicleStates[r].vehicle_id = r;
		dynamicVehicleStates[r].locked_prefix_length = 0;
		dynamicVehicleStates[r].active = true;
	}
	lockedPrefixesByVehicle = std::vector<std::vector<int> >(nbVehicles);
	lockedPrefixLoadByVehicle = std::vector<double>(nbVehicles, 0.);
	lockedPrefixServiceByVehicle = std::vector<double>(nbVehicles, 0.);
	lockedPrefixTravelByVehicle = std::vector<double>(nbVehicles, 0.);
	lockedPrefixLastNodeByVehicle = std::vector<int>(nbVehicles, 0);
	refreshActiveData();
}

void Params::setCustomerActivity(const std::vector<bool>& activeFlags)
{
	if ((int)activeFlags.size() != nbClients + 1)
		throw std::string("setCustomerActivity expects nbClients + 1 flags (including depot at index 0)");

	dynamicModeEnabled = true;
	customerActive = activeFlags;
	customerActive[0] = false;
	refreshActiveData();
}

void Params::setVehicleStates(const std::vector<DynamicVehicleState>& states)
{
	if (states.empty())
	{
		for (int r = 0; r < nbVehicles; r++)
		{
			dynamicVehicleStates[r].vehicle_id = r;
			dynamicVehicleStates[r].locked_prefix_length = 0;
			dynamicVehicleStates[r].active = true;
		}
		return;
	}

	if ((int)states.size() != nbVehicles)
		throw std::string("setVehicleStates expects exactly nbVehicles states");

	dynamicModeEnabled = true;
	dynamicVehicleStates = states;
	for (int r = 0; r < nbVehicles; r++)
	{
		dynamicVehicleStates[r].vehicle_id = r;
		if (dynamicVehicleStates[r].locked_prefix_length < 0)
			dynamicVehicleStates[r].locked_prefix_length = 0;
	}
}

void Params::setLockedPrefixes(const std::vector<std::vector<int> >& lockedPrefixes)
{
	if ((int)lockedPrefixes.size() != nbVehicles)
		throw std::string("setLockedPrefixes expects exactly nbVehicles routes");
	lockedPrefixesByVehicle = lockedPrefixes;

	lockedPrefixLoadByVehicle.assign(nbVehicles, 0.);
	lockedPrefixServiceByVehicle.assign(nbVehicles, 0.);
	lockedPrefixTravelByVehicle.assign(nbVehicles, 0.);
	lockedPrefixLastNodeByVehicle.assign(nbVehicles, 0);

	for (int r = 0; r < nbVehicles; r++)
	{
		int prev = 0;
		for (int c : lockedPrefixesByVehicle[r])
		{
			if (c < 1 || c > nbClients) throw std::string("setLockedPrefixes contains out-of-range customer id");
			lockedPrefixTravelByVehicle[r] += timeCost[prev][c];
			lockedPrefixLoadByVehicle[r] += cli[c].demand;
			lockedPrefixServiceByVehicle[r] += cli[c].serviceDuration;
			prev = c;
		}
		lockedPrefixLastNodeByVehicle[r] = prev;
	}
}


