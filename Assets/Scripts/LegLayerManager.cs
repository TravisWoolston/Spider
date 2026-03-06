using UnityEngine;

public static class LegLayerManager
{
    private static int spiderCount = 0;

    public static void AssignUniqueLegLayers()
    {
        // Find all SpiderControllers in the scene
        SpiderController[] spiderControllers = GameObject.FindObjectsOfType<SpiderController>();

        // Iterate over each SpiderController
        foreach (SpiderController spiderController in spiderControllers)
        {
            // Increment the spider count to ensure unique layers
            spiderCount++;
            
            // Generate a unique layer name for this spider's legs
            string legLayerName = "Legs" + spiderCount;

            // Create the layer if it doesn't exist (Note: Unity doesn't allow runtime creation of layers, so you need to manually add them in the editor)
            int layerIndex = LayerMask.NameToLayer(legLayerName);
            if (layerIndex == -1)
            {
                Debug.LogError($"Layer {legLayerName} does not exist. Please create it in the Tags and Layers settings.");
                continue;
            }

            // Get all leg objects for this spider
            GameObject[] legs = spiderController.GetLegs(); // Assume SpiderController has a method to return all legs

            // Assign the unique layer to each leg
            foreach (GameObject leg in legs)
            {
                leg.layer = layerIndex;

            }

            // Update the physics collision matrix
            for (int i = 0; i < 32; i++)
            {
                if (i != layerIndex && LayerMask.LayerToName(i) != "Web")
                {
                    Physics.IgnoreLayerCollision(layerIndex, i, false);  // Allow collision with other layers except "Web"
                }
                else
                {
                    Physics.IgnoreLayerCollision(layerIndex, i, true);  // Ignore collision with its own layer and the "Web" layer
                }
            }
        }
    }
}
