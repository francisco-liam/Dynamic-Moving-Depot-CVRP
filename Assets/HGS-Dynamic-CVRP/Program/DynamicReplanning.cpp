#include "DynamicReplanning.h"

#include "Genetic.h"
#include <set>
#include <stdexcept>

static std::vector<bool> buildActiveFlags(const Params& params, const DynamicReplanInput* dynamicInput)
{
	std::vector<bool> active(params.nbClients + 1, true);
	active[0] = false;
	if (dynamicInput == nullptr) return active;
	if (!dynamicInput->customerActive.empty())
	{
		if ((int)dynamicInput->customerActive.size() != params.nbClients + 1)
			throw std::string("dynamic customerActive must have size nbClients + 1");
		active = dynamicInput->customerActive;
		active[0] = false;
	}
	return active;
}

static std::vector<DynamicVehicleState> buildVehicleStates(const Params& params, const DynamicReplanInput* dynamicInput)
{
	std::vector<DynamicVehicleState> vehicleStates(params.nbVehicles);
	for (int r = 0; r < params.nbVehicles; r++)
	{
		vehicleStates[r].vehicle_id = r;
		vehicleStates[r].locked_prefix_length = 0;
		vehicleStates[r].active = true;
	}

	if (dynamicInput == nullptr || dynamicInput->vehicleStates.empty()) return vehicleStates;
	if ((int)dynamicInput->vehicleStates.size() != params.nbVehicles)
		throw std::string("dynamic vehicleStates must have size nbVehicles");

	vehicleStates = dynamicInput->vehicleStates;
	for (int r = 0; r < params.nbVehicles; r++)
	{
		vehicleStates[r].vehicle_id = r;
		if (vehicleStates[r].locked_prefix_length < 0) vehicleStates[r].locked_prefix_length = 0;
	}
	return vehicleStates;
}

static std::vector<std::vector<int> > buildLockedPrefixes(
	const Params& params,
	const DynamicReplanInput* dynamicInput,
	const std::vector<DynamicVehicleState>& vehicleStates,
	std::vector<bool>& activeFlags)
{
	std::vector<std::vector<int> > lockedPrefixes(params.nbVehicles);
	if (dynamicInput == nullptr) return lockedPrefixes;

	bool hasLocks = false;
	for (const DynamicVehicleState& state : vehicleStates)
		if (state.locked_prefix_length > 0) hasLocks = true;
	if (!hasLocks) return lockedPrefixes;

	if ((int)dynamicInput->previousRoutes.size() != params.nbVehicles)
		throw std::string("dynamic previousRoutes must have size nbVehicles when locks are used");

	std::vector<int> owner(params.nbClients + 1, -1);
	for (int r = 0; r < params.nbVehicles; r++)
	{
		const std::vector<int>& route = dynamicInput->previousRoutes[r];
		const int lockLen = std::min<int>(vehicleStates[r].locked_prefix_length, (int)route.size());
		lockedPrefixes[r].assign(route.begin(), route.begin() + lockLen);

		for (int c : lockedPrefixes[r])
		{
			if (c < 1 || c > params.nbClients) throw std::string("locked prefix contains an out-of-range customer id");
			if (owner[c] != -1) throw std::string("customer appears in multiple locked prefixes");
			owner[c] = r;
			activeFlags[c] = false;
		}
	}

	return lockedPrefixes;
}

static double computePenalizedCostFromRoutes(const Params& params, const std::vector<std::vector<int> >& routes, bool& isFeasible)
{
	double distance = 0.;
	double capacityExcess = 0.;
	double durationExcess = 0.;

	for (const std::vector<int>& route : routes)
	{
		if (route.empty()) continue;
		double routeDistance = params.timeCost[0][route.front()];
		double routeLoad = params.cli[route.front()].demand;
		double routeService = params.cli[route.front()].serviceDuration;
		for (size_t i = 1; i < route.size(); i++)
		{
			routeDistance += params.timeCost[route[i - 1]][route[i]];
			routeLoad += params.cli[route[i]].demand;
			routeService += params.cli[route[i]].serviceDuration;
		}
		routeDistance += params.timeCost[route.back()][0];
		distance += routeDistance;
		if (routeLoad > params.vehicleCapacity) capacityExcess += routeLoad - params.vehicleCapacity;
		if (routeDistance + routeService > params.durationLimit) durationExcess += routeDistance + routeService - params.durationLimit;
	}

	isFeasible = (capacityExcess < MY_EPSILON && durationExcess < MY_EPSILON);
	return distance + params.penaltyCapacity * capacityExcess + params.penaltyDuration * durationExcess;
}

static void validateReplanResult(
	const Params& params,
	const std::vector<std::vector<int> >& finalRoutes,
	const std::vector<std::vector<int> >& lockedPrefixes,
	const std::vector<bool>& activeMovableFlags)
{
	if ((int)finalRoutes.size() != params.nbVehicles)
		throw std::string("route identity continuity error: number of routes does not match nbVehicles");

	std::vector<int> count(params.nbClients + 1, 0);
	for (int r = 0; r < params.nbVehicles; r++)
	{
		if (finalRoutes[r].size() < lockedPrefixes[r].size())
			throw std::string("locked prefix preservation error: route shorter than locked prefix");

		for (size_t i = 0; i < lockedPrefixes[r].size(); i++)
		{
			if (finalRoutes[r][i] != lockedPrefixes[r][i])
				throw std::string("locked prefix preservation error: route prefix changed");
		}

		for (size_t i = lockedPrefixes[r].size(); i < finalRoutes[r].size(); i++)
		{
			const int c = finalRoutes[r][i];
			if (c < 1 || c > params.nbClients) throw std::string("route contains an out-of-range customer id");
			if (!activeMovableFlags[c])
				throw std::string("inactive customer found in optimized suffix");
		}

		for (int c : finalRoutes[r])
		{
			if (c < 1 || c > params.nbClients) throw std::string("route contains an out-of-range customer id");
			count[c]++;
		}
	}

	for (int c = 1; c <= params.nbClients; c++)
	{
		if (activeMovableFlags[c] && count[c] != 1)
			throw std::string("coverage error: active movable customer must appear exactly once");
		if (!activeMovableFlags[c] && count[c] > 1)
			throw std::string("duplicate customer error");
	}
}

DynamicReplanResult solve_dynamic_cvrp(
	const std::vector<double>& x_coords,
	const std::vector<double>& y_coords,
	const std::vector<std::vector<double> >& dist_mtx,
	const std::vector<double>& service_time,
	const std::vector<double>& demands,
	double vehicleCapacity,
	double durationLimit,
	int nbVeh,
	bool isDurationConstraint,
	bool verbose,
	const AlgorithmParameters& ap,
	const DynamicReplanInput* dynamicInput)
{
	Params params(
		x_coords,
		y_coords,
		dist_mtx,
		service_time,
		demands,
		vehicleCapacity,
		durationLimit,
		nbVeh,
		isDurationConstraint,
		verbose,
		ap);

	std::vector<bool> activeFlags = buildActiveFlags(params, dynamicInput);
	std::vector<DynamicVehicleState> vehicleStates = buildVehicleStates(params, dynamicInput);
	std::vector<std::vector<int> > lockedPrefixes = buildLockedPrefixes(params, dynamicInput, vehicleStates, activeFlags);

	if (dynamicInput != nullptr)
	{
		params.setVehicleStates(vehicleStates);
		params.setLockedPrefixes(lockedPrefixes);
		params.setCustomerActivity(activeFlags);
	}

	Genetic solver(params);
	solver.run();

	DynamicReplanResult result;
	result.elapsedSeconds = (double)(clock() - params.startTime) / (double)CLOCKS_PER_SEC;
	result.routes = std::vector<std::vector<int> >(params.nbVehicles);

	const Individual* best = solver.population.getBestFound();
	if (best != nullptr) result.routes = best->chromR;

	if (dynamicInput != nullptr)
		validateReplanResult(params, result.routes, lockedPrefixes, params.customerActive);

	result.penalizedCost = computePenalizedCostFromRoutes(params, result.routes, result.isFeasible);
	return result;
}
