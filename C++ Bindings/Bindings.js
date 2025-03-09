
import Data from "../Bindings/Bindings.js";
{
	const instance = await Data();

	// Finding all bound functions
	const boundFunctions = Object.getOwnPropertyNames(instance)
  		.filter(prop => typeof instance[prop] === "function");

	console.log("Functions inside of Data:", boundFunctions);
	//

	instance.AcquireData("Pokedex.csv");

	// Binding the "listResources" function from Data.cpp
	window.ListResources = instance.ListResources;
};
