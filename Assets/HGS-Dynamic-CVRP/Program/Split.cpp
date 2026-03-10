#include "Split.h" 

static void validateLabeledDecode(const Params& params, const Individual& indiv)
{
	std::vector<int> count(params.nbClients + 1, 0);

	for (int r = 0; r < params.nbVehicles; r++)
	{
		const int lockLen = std::min<int>((int)params.lockedPrefixesByVehicle[r].size(), (int)indiv.chromR[r].size());
		for (int i = 0; i < lockLen; i++)
			if (indiv.chromR[r][i] != params.lockedPrefixesByVehicle[r][i])
				throw std::string("ERROR : labeled split violated locked prefix preservation");

		for (int c : indiv.chromR[r])
		{
			if (c < 1 || c > params.nbClients)
				throw std::string("ERROR : labeled split generated out-of-range customer id");
			count[c]++;
		}
	}

	for (int c = 1; c <= params.nbClients; c++)
	{
		if (params.customerActive[c] && count[c] != 1)
			throw std::string("ERROR : labeled split coverage mismatch for active customer");
		if (!params.customerActive[c] && count[c] > 1)
			throw std::string("ERROR : labeled split produced duplicate inactive customer");
	}
}

void Split::generalSplit(Individual & indiv, int nbMaxVehicles)
{
	(void)nbMaxVehicles;
	const int nbSequenceCustomers = (int)indiv.chromT.size();
	if (nbSequenceCustomers == 0)
	{
		for (int k = 0; k < params.nbVehicles; k++) indiv.chromR[k].clear();
		indiv.evaluateCompleteCost(params);
		return;
	}

	// Split is intentionally replaced by a prefix-aware labeled decoder.
	splitLabeled(indiv);
	validateLabeledDecode(params, indiv);

	// Build up the rest of the Individual structure
	indiv.evaluateCompleteCost(params);
}

int Split::splitLabeled(Individual & indiv)
{
	const int n = (int)indiv.chromT.size();
	const int m = params.nbVehicles;

	std::vector<std::vector<double> > dynPotential(m + 1, std::vector<double>(n + 1, 1.e30));
	std::vector<std::vector<int> > dynPred(m + 1, std::vector<int>(n + 1, -1));
	dynPotential[0][0] = 0.;
	dynPred[0][0] = 0;

	for (int k = 0; k < m; k++)
	{
		const double fixedLoad = params.lockedPrefixLoadByVehicle[k];
		const double fixedService = params.lockedPrefixServiceByVehicle[k];
		const double fixedTravel = params.lockedPrefixTravelByVehicle[k];
		const int startNode = params.lockedPrefixLastNodeByVehicle[k];
		const bool vehicleActive = params.dynamicVehicleStates[k].active;

		const double emptyDistance = (startNode == 0) ? 0. : (fixedTravel + params.timeCost[startNode][0]);
		const double emptyDurationExcess = params.isDurationConstraint ? std::max<double>(0., emptyDistance + fixedService - params.durationLimit) : 0.;
		const double emptyCapacityExcess = std::max<double>(0., fixedLoad - params.vehicleCapacity);
		const double emptyCost = emptyDistance + params.penaltyCapacity * emptyCapacityExcess + params.penaltyDuration * emptyDurationExcess;

		for (int i = 0; i <= n; i++)
		{
			if (dynPotential[k][i] > 1.e29) continue;

			// Option 1: assign an empty suffix to this vehicle.
			if (dynPotential[k][i] + emptyCost < dynPotential[k + 1][i])
			{
				dynPotential[k + 1][i] = dynPotential[k][i] + emptyCost;
				dynPred[k + 1][i] = i;
			}

			if (!vehicleActive) continue;

			// Option 2: assign a contiguous suffix segment [i, j-1] to this vehicle.
			double segLoad = 0.;
			double segService = 0.;
			double segTravel = 0.;
			for (int j = i + 1; j <= n; j++)
			{
				int current = indiv.chromT[j - 1];
				segLoad += params.cli[current].demand;
				segService += params.cli[current].serviceDuration;

				if (j == i + 1)
					segTravel += params.timeCost[startNode][current];
				else
					segTravel += params.timeCost[indiv.chromT[j - 2]][current];

				double routeDistance = fixedTravel + segTravel + params.timeCost[current][0];
				double routeLoad = fixedLoad + segLoad;
				double routeService = fixedService + segService;
				double capacityExcess = std::max<double>(0., routeLoad - params.vehicleCapacity);
				double durationExcess = params.isDurationConstraint ? std::max<double>(0., routeDistance + routeService - params.durationLimit) : 0.;
				double routeCost = routeDistance + params.penaltyCapacity * capacityExcess + params.penaltyDuration * durationExcess;

				if (dynPotential[k][i] + routeCost < dynPotential[k + 1][j])
				{
					dynPotential[k + 1][j] = dynPotential[k][i] + routeCost;
					dynPred[k + 1][j] = i;
				}
			}
		}
	}

	if (dynPotential[m][n] > 1.e29)
		throw std::string("ERROR : no labeled split solution has been propagated until the last node");

	for (int k = 0; k < params.nbVehicles; k++) indiv.chromR[k].clear();

	int end = n;
	for (int k = m - 1; k >= 0; k--)
	{
		int begin = dynPred[k + 1][end];
		if (begin < 0) throw std::string("ERROR : invalid predecessor during labeled split backtracking");
		for (int ii = begin; ii < end; ii++) indiv.chromR[k].push_back(indiv.chromT[ii]);
		end = begin;
	}

	if (end != 0)
		throw std::string("ERROR : labeled split backtracking did not reach tour start");

	// Final labeled routes are lock prefix + assigned suffix for each vehicle index.
	for (int k = 0; k < params.nbVehicles; k++)
	{
		std::vector<int> fullRoute = params.lockedPrefixesByVehicle[k];
		fullRoute.insert(fullRoute.end(), indiv.chromR[k].begin(), indiv.chromR[k].end());
		indiv.chromR[k] = fullRoute;
	}

	return 1;
}

int Split::splitSimple(Individual & indiv)
{
	const int nbSequenceCustomers = (int)indiv.chromT.size();

	// Reinitialize the potential structures
	potential[0][0] = 0;
	for (int i = 1; i <= nbSequenceCustomers; i++)
		potential[0][i] = 1.e30;

	// MAIN ALGORITHM -- Simple Split using Bellman's algorithm in topological order
	// This code has been maintained as it is very simple and can be easily adapted to a variety of constraints, whereas the O(n) Split has a more restricted application scope
	if (params.isDurationConstraint)
	{
		for (int i = 0; i < nbSequenceCustomers; i++)
		{
			double load = 0.;
			double distance = 0.;
			double serviceDuration = 0.;
			for (int j = i + 1; j <= nbSequenceCustomers && load <= 1.5 * params.vehicleCapacity ; j++)
			{
				load += cliSplit[j].demand;
				serviceDuration += cliSplit[j].serviceTime;
				if (j == i + 1) distance += cliSplit[j].d0_x;
				else distance += cliSplit[j - 1].dnext;
				double cost = distance + cliSplit[j].dx_0
					+ params.penaltyCapacity * std::max<double>(load - params.vehicleCapacity, 0.)
					+ params.penaltyDuration * std::max<double>(distance + cliSplit[j].dx_0 + serviceDuration - params.durationLimit, 0.);
				if (potential[0][i] + cost < potential[0][j])
				{
					potential[0][j] = potential[0][i] + cost;
					pred[0][j] = i;
				}
			}
		}
	}
	else
	{
		Trivial_Deque queue = Trivial_Deque(nbSequenceCustomers + 1, 0);
		for (int i = 1; i <= nbSequenceCustomers; i++)
		{
			// The front is the best predecessor for i
			potential[0][i] = propagate(queue.get_front(), i, 0);
			pred[0][i] = queue.get_front();

			if (i < nbSequenceCustomers)
			{
				// If i is not dominated by the last of the pile
				if (!dominates(queue.get_back(), i, 0))
				{
					// then i will be inserted, need to remove whoever is dominated by i.
					while (queue.size() > 0 && dominatesRight(queue.get_back(), i, 0))
						queue.pop_back();
					queue.push_back(i);
				}
				// Check iteratively if front is dominated by the next front
				while (queue.size() > 1 && propagate(queue.get_front(), i + 1, 0) > propagate(queue.get_next_front(), i + 1, 0) - MY_EPSILON)
					queue.pop_front();
			}
		}
	}

	if (potential[0][nbSequenceCustomers] > 1.e29)
		throw std::string("ERROR : no Split solution has been propagated until the last node");

	// Filling the chromR structure
	for (int k = params.nbVehicles - 1; k >= maxVehicles; k--)
		indiv.chromR[k].clear();

	int end = nbSequenceCustomers;
	for (int k = maxVehicles - 1; k >= 0; k--)
	{
		indiv.chromR[k].clear();
		int begin = pred[0][end];
		for (int ii = begin; ii < end; ii++)
			indiv.chromR[k].push_back(indiv.chromT[ii]);
		end = begin;
	}

	// Return OK in case the Split algorithm reached the beginning of the routes
	return (end == 0);
}

// Split for problems with limited fleet
int Split::splitLF(Individual & indiv)
{
	const int nbSequenceCustomers = (int)indiv.chromT.size();

	// Initialize the potential structures
	potential[0][0] = 0;
	for (int k = 0; k <= maxVehicles; k++)
		for (int i = 1; i <= nbSequenceCustomers; i++)
			potential[k][i] = 1.e30;

	// MAIN ALGORITHM -- Simple Split using Bellman's algorithm in topological order
	// This code has been maintained as it is very simple and can be easily adapted to a variety of constraints, whereas the O(n) Split has a more restricted application scope
	if (params.isDurationConstraint) 
	{
		for (int k = 0; k < maxVehicles; k++)
		{
			for (int i = k; i < nbSequenceCustomers && potential[k][i] < 1.e29 ; i++)
			{
				double load = 0.;
				double serviceDuration = 0.;
				double distance = 0.;
				for (int j = i + 1; j <= nbSequenceCustomers && load <= 1.5 * params.vehicleCapacity ; j++) // Setting a maximum limit on load infeasibility to accelerate the algorithm
				{
					load += cliSplit[j].demand;
					serviceDuration += cliSplit[j].serviceTime;
					if (j == i + 1) distance += cliSplit[j].d0_x;
					else distance += cliSplit[j - 1].dnext;
					double cost = distance + cliSplit[j].dx_0
								+ params.penaltyCapacity * std::max<double>(load - params.vehicleCapacity, 0.)
								+ params.penaltyDuration * std::max<double>(distance + cliSplit[j].dx_0 + serviceDuration - params.durationLimit, 0.);
					if (potential[k][i] + cost < potential[k + 1][j])
					{
						potential[k + 1][j] = potential[k][i] + cost;
						pred[k + 1][j] = i;
					}
				}
			}
		}
	}
	else // MAIN ALGORITHM -- Without duration constraints in O(n), from "Vidal, T. (2016). Split algorithm in O(n) for the capacitated vehicle routing problem. C&OR"
	{
		Trivial_Deque queue = Trivial_Deque(nbSequenceCustomers + 1, 0);
		for (int k = 0; k < maxVehicles; k++)
		{
			// in the Split problem there is always one feasible solution with k routes that reaches the index k in the tour.
			queue.reset(k);

			// The range of potentials < 1.29 is always an interval.
			// The size of the queue will stay >= 1 until we reach the end of this interval.
			for (int i = k + 1; i <= nbSequenceCustomers && queue.size() > 0; i++)
			{
				// The front is the best predecessor for i
				potential[k + 1][i] = propagate(queue.get_front(), i, k);
				pred[k + 1][i] = queue.get_front();

				if (i < nbSequenceCustomers)
				{
					// If i is not dominated by the last of the pile 
					if (!dominates(queue.get_back(), i, k))
					{
						// then i will be inserted, need to remove whoever he dominates
						while (queue.size() > 0 && dominatesRight(queue.get_back(), i, k))
							queue.pop_back();
						queue.push_back(i);
					}

					// Check iteratively if front is dominated by the next front
					while (queue.size() > 1 && propagate(queue.get_front(), i + 1, k) > propagate(queue.get_next_front(), i + 1, k) - MY_EPSILON)
						queue.pop_front();
				}
			}
		}
	}

	if (potential[maxVehicles][nbSequenceCustomers] > 1.e29)
		throw std::string("ERROR : no Split solution has been propagated until the last node");

	// It could be cheaper to use a smaller number of vehicles
	double minCost = potential[maxVehicles][nbSequenceCustomers];
	int nbRoutes = maxVehicles;
	for (int k = 1; k < maxVehicles; k++)
		if (potential[k][nbSequenceCustomers] < minCost)
			{minCost = potential[k][nbSequenceCustomers]; nbRoutes = k;}

	// Filling the chromR structure
	for (int k = params.nbVehicles-1; k >= nbRoutes ; k--)
		indiv.chromR[k].clear();

	int end = nbSequenceCustomers;
	for (int k = nbRoutes - 1; k >= 0; k--)
	{
		indiv.chromR[k].clear();
		int begin = pred[k+1][end];
		for (int ii = begin; ii < end; ii++)
			indiv.chromR[k].push_back(indiv.chromT[ii]);
		end = begin;
	}

	// Return OK in case the Split algorithm reached the beginning of the routes
	return (end == 0);
}

Split::Split(const Params & params): params(params)
{
	// Structures of the linear Split
	cliSplit = std::vector <ClientSplit>(params.nbClients + 1);
	sumDistance = std::vector <double>(params.nbClients + 1,0.);
	sumLoad = std::vector <double>(params.nbClients + 1,0.);
	sumService = std::vector <double>(params.nbClients + 1, 0.);
	potential = std::vector < std::vector <double> >(params.nbVehicles + 1, std::vector <double>(params.nbClients + 1,1.e30));
	pred = std::vector < std::vector <int> >(params.nbVehicles + 1, std::vector <int>(params.nbClients + 1,0));
}
