#ifdef __EMSCRIPTEN__
#include "emscripten.h"
#else
#define EMSCRIPTEN_KEEPALIVE
#endif

#include <iostream>



extern "C"
{
	static int i = 0;

	EMSCRIPTEN_KEEPALIVE
		void Gump()
	{
		std::cout << "Gump! (" << i++ << ")" << std::endl;
	};

	EMSCRIPTEN_KEEPALIVE
		void ParseReplay(const char* path)
	{ };
};
