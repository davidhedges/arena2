using UnityEditor;
using UnityEngine;

namespace SICS
{
    [ExecuteInEditMode]
    public class WindSettings : MonoBehaviour
    {
        // Fixed wind direction
        [SerializeField] private Vector3 windDirection = new Vector3(1, 0, 0); // Set your fixed wind direction here

        // Wind variables
        [SerializeField] [Range(0.0F, 20.0F)] private float windStrength = 1.0f;
        [SerializeField] [Range(0.0F, 20.0F)] private float windScale = 1.0f;
        [SerializeField] [Range(0.0F, 20.0F)] private float windSpeed = 1.0f;
        [SerializeField] [Range(0.0F, 20.0F)] private float treeJitter = 1.0f;

        // Global Noise Texture
        [SerializeField] private Texture noiseTexture;

        // Previous values to check for changes
        private Vector3 previousWindDirection;
        private float previousWindStrength;
        private float previousWindScale;
        private float previousWindSpeed;
        private float previousTreeJitter;
        private Texture previousNoiseTexture; // Previous noise texture value

        void Start()
        {
            // Initialize previous values
            previousWindDirection = windDirection;
            previousWindStrength = windStrength;
            previousWindScale = windScale;
            previousWindSpeed = windSpeed;
            previousTreeJitter = treeJitter;
            previousNoiseTexture = noiseTexture; // Initialize previous noise texture

            // Set initial shader values
            UpdateShaderVariables();
        }

        void Update()
        {
            // Check if any wind settings have changed
            if (HasWindSettingsChanged())
            {
                UpdateShaderVariables();
            }
        }

        private bool HasWindSettingsChanged()
        {
            // Compare current values with previous values
            return windDirection != previousWindDirection ||
                   windStrength != previousWindStrength ||
                   windScale != previousWindScale ||
                   windSpeed != previousWindSpeed ||
                   treeJitter != previousTreeJitter ||
                   noiseTexture != previousNoiseTexture; // Check for noise texture change
        }

        private void UpdateShaderVariables()
        {
            // Set wind settings in the shader
            Shader.SetGlobalVector("SICSGlobalWindDirection", windDirection);
            Shader.SetGlobalFloat("SICSGlobalWindStrength", windStrength);
            Shader.SetGlobalFloat("SICSGlobalWindScale", windScale);
            Shader.SetGlobalFloat("SICSGlobalWindSpeed", windSpeed);
            Shader.SetGlobalFloat("SICSGlobalTreeJitter", treeJitter);
            Shader.SetGlobalTexture("SICSGlobalNoiseTexture", noiseTexture); // Set global noise texture

            // Update previous values
            previousWindDirection = windDirection;
            previousWindStrength = windStrength;
            previousWindScale = windScale;
            previousWindSpeed = windSpeed;
            previousTreeJitter = treeJitter;
            previousNoiseTexture = noiseTexture; // Update previous noise texture
        }
    }
}
