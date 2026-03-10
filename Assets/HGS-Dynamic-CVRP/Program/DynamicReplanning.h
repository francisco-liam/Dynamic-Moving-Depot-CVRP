#ifndef DYNAMIC_REPLANNING_H
#define DYNAMIC_REPLANNING_H

#include "AlgorithmParameters.h"
#include "Params.h"
#include <vector>

struct DynamicReplanInput
{
	// Per-customer active flags indexed by original id in [0..n-1]. Empty means all non-depot customers active.
	std::vector<bool> customerActive;

	// Per-vehicle dynamic lock state. Empty means all locks are zero.
	std::vector<DynamicVehicleState> vehicleStates;

	// Previous route layout by vehicle index. Required for lock reconstruction if any lock > 0.
	std::vector<std::vector<int> > previousRoutes;
};

struct DynamicReplanResult
{
	double penalizedCost = 0.;
	double elapsedSeconds = 0.;
	bool isFeasible = false;
	std::vector<std::vector<int> > routes;
};

// Minimal C++ replanning entrypoint.
// If dynamicInput is nullptr, this behaves like a regular static HGS solve.
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
	const DynamicReplanInput* dynamicInput = nullptr);

#endif
