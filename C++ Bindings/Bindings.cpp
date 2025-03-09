#ifdef __EMSCRIPTEN__
#include <emscripten.h>
#include <emscripten/bind.h>
#else
#define EMSCRIPTEN_KEEPALIVE
#endif

#include <iostream>
#include <memory>


void LoadResource(
	const std::string& name,
	const std::string& data)
{
	std::cout << "ListResources: " << name << ": " << data << std::endl;
};


void ListResources()
{
	std::cout << "ListResources: " << std::endl;
};


void AcquireData(
	const std::string& file_name)
{
	std::cout << "AcquireData:   " << file_name << std::endl;
};


void ExecuteSQLCommand(
	const std::string& command)
{
	std::cout << "AcquireData:   " << command << std::endl;
};


#ifdef __EMSCRIPTEN__
// Register functions for JavaScript
EMSCRIPTEN_BINDINGS(my_module)
{
	emscripten::function("LoadResource", &LoadResource);
	emscripten::function("ListResources", &ListResources);
	emscripten::function("AcquireData", &AcquireData);
	emscripten::function("ExecuteSQLCommand", &ExecuteSQLCommand);
};
#endif
