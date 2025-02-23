#ifdef __EMSCRIPTEN__
#include <emscripten.h>
#include <emscripten/bind.h>
#else
#define EMSCRIPTEN_KEEPALIVE
#endif

#include "Data.h"

#include <iostream>


void LoadResource(
	const std::string& data_name,
	const std::string& data)
{
	std::cout << "Loaded: " << data_name << std::endl;
	data_map[data_name] = data;
};

void ListResources()
{
	std::cout << "Resources: " << std::endl;
	for (auto& [i, _] : data_map)
		std::cout << i << std::endl;
};

void AcquireData(
	const std::string& file_name)
{
	
};

#ifdef __EMSCRIPTEN__
// Register functions for JavaScript
EMSCRIPTEN_BINDINGS(my_module)
{
	emscripten::function("LoadResource", &LoadResource);
	emscripten::function("ListResources", &ListResources);
	emscripten::function("AcquireData", &AcquireData);
};
#endif
