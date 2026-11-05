using UnityEngine;

namespace Plyry.Unity
{
    /// <summary>
    /// Optional platform-specific storage for a rotating Plyry refresh token.
    /// Implement this component with the operating system's secure credential store.
    /// </summary>
    public abstract class PlyryCredentialStore : MonoBehaviour
    {
        /// <summary>Loads the refresh token saved for this Plyry service, if one exists.</summary>
        public abstract bool TryLoadRefreshToken(string serviceUrl, out string refreshToken);

        /// <summary>Saves the latest refresh token. The token rotates after each successful refresh.</summary>
        public abstract void SaveRefreshToken(string serviceUrl, string refreshToken);

        /// <summary>Removes the saved refresh token for this Plyry service.</summary>
        public abstract void ClearRefreshToken(string serviceUrl);
    }
}
