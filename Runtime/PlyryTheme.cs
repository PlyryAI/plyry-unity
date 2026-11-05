using UnityEngine;

namespace Plyry.Unity
{
    [CreateAssetMenu(menuName = "Plyry/Runtime UI Theme", fileName = "PlyryTheme")]
    public sealed class PlyryTheme : ScriptableObject
    {
        [Tooltip("Optional full GUISkin for the in-game panel.")]
        public GUISkin skin;
        public Texture2D panelBackground;
        public Color panelTint = new Color(0.075f, 0.09f, 0.13f, 0.97f);
        public Color surfaceTint = new Color(0.13f, 0.16f, 0.22f, 1f);
        public Color accent = new Color(0.37f, 0.75f, 1f, 1f);
        public Color text = new Color(0.93f, 0.95f, 0.98f, 1f);
        public Color mutedText = new Color(0.66f, 0.71f, 0.78f, 1f);
        public int fontSize = 15;
        public float panelWidth = 460f;
        public float panelHeight = 660f;
    }
}
