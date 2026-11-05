using UnityEngine;

namespace Plyry.Unity.Samples
{
    /// <summary>
    /// Add this to a scene object with PlyryClient and PlyryRuntimePanel,
    /// then assign a target from the scene.
    /// </summary>
    public sealed class PlyryQuickStart : MonoBehaviour
    {
        [SerializeField] private PlyryGenerationTarget target;

        private void Start()
        {
            var client = GetComponent<PlyryClient>();
            var panel = GetComponent<PlyryRuntimePanel>();
            if (panel != null)
            {
                panel.Target = target;
                if (client != null) panel.Initialize(client);
            }
        }
    }
}
