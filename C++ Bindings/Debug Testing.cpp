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
		void Buh()
	{
		std::cout << "Hello World! (" << i++ << ")" << std::endl;
	};
};
