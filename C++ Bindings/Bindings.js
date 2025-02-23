
import Data from "../Bindings/Data.js";
{
	const instance = await Data();

	// Finding all bound functions
	const boundFunctions = Object.getOwnPropertyNames(instance)
  		.filter(prop => typeof instance[prop] === "function");

	console.log("Functions inside of Data:", boundFunctions);
	//

	// Loading Resources
	const pokedexResponse = await fetch("Data/Pokedex.csv");
	const pokedexText = await pokedexResponse.text();
	instance.LoadResource("Pokedex", pokedexText);

	const draftResponse = await fetch("Data/Draft.csv");
	const draftText = await draftResponse.text();
	instance.LoadResource("Draft", draftText);
	//

	// Binding the "listResources" function from Data.cpp
	window.ListResources = instance.ListResources;
};
