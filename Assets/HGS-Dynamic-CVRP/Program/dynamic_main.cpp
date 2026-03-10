#include "AlgorithmParameters.h"
#include "DynamicReplanning.h"
#include "InstanceCVRPLIB.h"
#include <cctype>
#include <climits>
#include <fstream>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

class DynamicCommandLine
{
public:
	AlgorithmParameters ap = default_algorithm_parameters();
	int nbVeh = INT_MAX;
	bool verbose = true;
	bool isRoundingInteger = true;
	std::string pathInstance;
	std::string pathDynamicJson;
	std::string pathSolution;

	DynamicCommandLine(int argc, char* argv[])
	{
		if (argc < 4 || argc % 2 != 0)
		{
			displayHelp();
			throw std::string("Incorrect line of command");
		}

		pathInstance = std::string(argv[1]);
		pathDynamicJson = std::string(argv[2]);
		pathSolution = std::string(argv[3]);

		for (int i = 4; i < argc; i += 2)
		{
			if (std::string(argv[i]) == "-t") ap.timeLimit = atof(argv[i + 1]);
			else if (std::string(argv[i]) == "-it") ap.nbIter = atoi(argv[i + 1]);
			else if (std::string(argv[i]) == "-seed") ap.seed = atoi(argv[i + 1]);
			else if (std::string(argv[i]) == "-veh") nbVeh = atoi(argv[i + 1]);
			else if (std::string(argv[i]) == "-round") isRoundingInteger = atoi(argv[i + 1]);
			else if (std::string(argv[i]) == "-log") verbose = atoi(argv[i + 1]);
			else if (std::string(argv[i]) == "-nbGranular") ap.nbGranular = atoi(argv[i + 1]);
			else if (std::string(argv[i]) == "-mu") ap.mu = atoi(argv[i + 1]);
			else if (std::string(argv[i]) == "-lambda") ap.lambda = atoi(argv[i + 1]);
			else if (std::string(argv[i]) == "-nbElite") ap.nbElite = atoi(argv[i + 1]);
			else if (std::string(argv[i]) == "-nbClose") ap.nbClose = atoi(argv[i + 1]);
			else if (std::string(argv[i]) == "-nbIterPenaltyManagement") ap.nbIterPenaltyManagement = atoi(argv[i + 1]);
			else if (std::string(argv[i]) == "-nbIterTraces") ap.nbIterTraces = atoi(argv[i + 1]);
			else if (std::string(argv[i]) == "-targetFeasible") ap.targetFeasible = atof(argv[i + 1]);
			else if (std::string(argv[i]) == "-penaltyIncrease") ap.penaltyIncrease = atof(argv[i + 1]);
			else if (std::string(argv[i]) == "-penaltyDecrease") ap.penaltyDecrease = atof(argv[i + 1]);
			else
			{
				std::cout << "----- ARGUMENT NOT RECOGNIZED: " << std::string(argv[i]) << std::endl;
				displayHelp();
				throw std::string("Incorrect line of command");
			}
		}
	}

	void displayHelp()
	{
		std::cout << "Call with: ./hgs_dynamic instancePath dynamicJsonPath solPath [options]" << std::endl;
		std::cout << "JSON keys: customerActive, lockedPrefixLength, previousRoutes, vehicleActive (optional)" << std::endl;
	}
};

static std::string readTextFile(const std::string& filePath)
{
	std::ifstream in(filePath);
	if (!in.is_open()) throw std::string("Could not open dynamic JSON file: " + filePath);
	std::ostringstream buffer;
	buffer << in.rdbuf();
	return buffer.str();
}

static size_t skipWs(const std::string& s, size_t i)
{
	while (i < s.size() && std::isspace(static_cast<unsigned char>(s[i]))) i++;
	return i;
}

static size_t findMatchingBracket(const std::string& s, size_t start, char openC, char closeC)
{
	int depth = 0;
	for (size_t i = start; i < s.size(); i++)
	{
		if (s[i] == openC) depth++;
		else if (s[i] == closeC)
		{
			depth--;
			if (depth == 0) return i;
		}
	}
	throw std::string("Malformed JSON: unmatched bracket");
}

static bool extractArrayPayload(const std::string& json, const std::string& key, std::string& payload)
{
	const std::string token = "\"" + key + "\"";
	size_t p = json.find(token);
	if (p == std::string::npos) return false;
	size_t colon = json.find(':', p + token.size());
	if (colon == std::string::npos) throw std::string("Malformed JSON near key: " + key);
	size_t begin = skipWs(json, colon + 1);
	if (begin >= json.size() || json[begin] != '[') throw std::string("Expected array for key: " + key);
	size_t end = findMatchingBracket(json, begin, '[', ']');
	payload = json.substr(begin + 1, end - begin - 1);
	return true;
}

static std::vector<int> parseIntArray(const std::string& payload)
{
	std::vector<int> out;
	std::string tok;
	for (size_t i = 0; i <= payload.size(); i++)
	{
		char c = (i < payload.size()) ? payload[i] : ',';
		if (c == ',')
		{
			std::string t;
			for (char x : tok) if (!std::isspace(static_cast<unsigned char>(x))) t.push_back(x);
			if (!t.empty()) out.push_back(std::stoi(t));
			tok.clear();
		}
		else tok.push_back(c);
	}
	return out;
}

static std::vector<bool> parseBoolArray(const std::string& payload)
{
	std::vector<bool> out;
	std::string tok;
	for (size_t i = 0; i <= payload.size(); i++)
	{
		char c = (i < payload.size()) ? payload[i] : ',';
		if (c == ',')
		{
			std::string t;
			for (char x : tok) if (!std::isspace(static_cast<unsigned char>(x))) t.push_back((char)std::tolower((unsigned char)x));
			if (!t.empty())
			{
				if (t == "true" || t == "1") out.push_back(true);
				else if (t == "false" || t == "0") out.push_back(false);
				else throw std::string("Invalid boolean token in customerActive/vehicleActive: " + t);
			}
			tok.clear();
		}
		else tok.push_back(c);
	}
	return out;
}

static std::vector<std::vector<int> > parseIntMatrix(const std::string& payload)
{
	std::vector<std::vector<int> > routes;
	size_t i = 0;
	while (i < payload.size())
	{
		i = skipWs(payload, i);
		if (i >= payload.size()) break;
		if (payload[i] == ',') { i++; continue; }
		if (payload[i] != '[') throw std::string("Expected nested array in previousRoutes");
		size_t end = findMatchingBracket(payload, i, '[', ']');
		routes.push_back(parseIntArray(payload.substr(i + 1, end - i - 1)));
		i = end + 1;
	}
	return routes;
}

static DynamicReplanInput parseDynamicInputJson(const std::string& jsonText, int nbClients, int nbVehicles)
{
	DynamicReplanInput input;
	std::string payload;

	if (extractArrayPayload(jsonText, "customerActive", payload))
		input.customerActive = parseBoolArray(payload);

	std::vector<int> lockedPrefixLength;
	if (extractArrayPayload(jsonText, "lockedPrefixLength", payload))
		lockedPrefixLength = parseIntArray(payload);

	if (extractArrayPayload(jsonText, "previousRoutes", payload))
		input.previousRoutes = parseIntMatrix(payload);

	std::vector<bool> vehicleActive;
	if (extractArrayPayload(jsonText, "vehicleActive", payload))
		vehicleActive = parseBoolArray(payload);

	if (!input.customerActive.empty() && (int)input.customerActive.size() != nbClients + 1)
		throw std::string("customerActive must contain nbClients + 1 values (including depot index 0)");

	if (!lockedPrefixLength.empty() && (int)lockedPrefixLength.size() != nbVehicles)
		throw std::string("lockedPrefixLength must contain nbVehicles values");

	if (!input.previousRoutes.empty() && (int)input.previousRoutes.size() != nbVehicles)
		throw std::string("previousRoutes must contain nbVehicles arrays");

	if (!vehicleActive.empty() && (int)vehicleActive.size() != nbVehicles)
		throw std::string("vehicleActive must contain nbVehicles values");

	if (!lockedPrefixLength.empty() || !vehicleActive.empty())
	{
		input.vehicleStates.resize(nbVehicles);
		for (int r = 0; r < nbVehicles; r++)
		{
			input.vehicleStates[r].vehicle_id = r;
			input.vehicleStates[r].locked_prefix_length = (lockedPrefixLength.empty() ? 0 : lockedPrefixLength[r]);
			input.vehicleStates[r].active = (vehicleActive.empty() ? true : vehicleActive[r]);
		}
	}

	return input;
}

static void writeSolutionFile(const DynamicReplanResult& result, const std::string& fileName)
{
	std::ofstream out(fileName);
	if (!out.is_open()) throw std::string("Could not open output solution file: " + fileName);

	for (int r = 0; r < (int)result.routes.size(); r++)
	{
		if (result.routes[r].empty()) continue;
		out << "Route #" << (r + 1) << ":";
		for (int c : result.routes[r]) out << " " << c;
		out << std::endl;
	}
	out << "Cost " << result.penalizedCost << std::endl;
}

static int inferDefaultNbVehicles(const InstanceCVRPLIB& cvrp)
{
	double totalDemand = 0.;
	for (int i = 1; i <= cvrp.nbClients; i++) totalDemand += cvrp.demands[i];
	return (int)std::ceil(1.3 * totalDemand / cvrp.vehicleCapacity) + 3;
}

int main(int argc, char* argv[])
{
	try
	{
		DynamicCommandLine commandline(argc, argv);
		if (commandline.verbose) print_algorithm_parameters(commandline.ap);
		if (commandline.verbose) std::cout << "----- READING INSTANCE: " << commandline.pathInstance << std::endl;

		InstanceCVRPLIB cvrp(commandline.pathInstance, commandline.isRoundingInteger);
		const int nbVehicles = (commandline.nbVeh == INT_MAX) ? inferDefaultNbVehicles(cvrp) : commandline.nbVeh;

		const std::string jsonText = readTextFile(commandline.pathDynamicJson);
		DynamicReplanInput dynamicInput = parseDynamicInputJson(jsonText, (int)cvrp.demands.size() - 1, nbVehicles);

		DynamicReplanResult result = solve_dynamic_cvrp(
			cvrp.x_coords,
			cvrp.y_coords,
			cvrp.dist_mtx,
			cvrp.service_time,
			cvrp.demands,
			cvrp.vehicleCapacity,
			cvrp.durationLimit,
			nbVehicles,
			cvrp.isDurationConstraint,
			commandline.verbose,
			commandline.ap,
			&dynamicInput);

		if (commandline.verbose)
			std::cout << "----- WRITING DYNAMIC SOLUTION IN: " << commandline.pathSolution << std::endl;
		writeSolutionFile(result, commandline.pathSolution);
	}
	catch (const std::string& e) { std::cout << "EXCEPTION | " << e << std::endl; return 1; }
	catch (const std::exception& e) { std::cout << "EXCEPTION | " << e.what() << std::endl; return 1; }
	return 0;
}
