using System;
using UnityEngine;

namespace Plyry.Unity
{
    public static class PlyryImageUtility
    {
        public static byte[] EncodePng(Texture source, int maxDimension)
        {
            if (source == null) throw new ArgumentNullException("source");
            maxDimension = Mathf.Clamp(maxDimension, 1, 8192);
            var width = Mathf.Max(1, source.width);
            var height = Mathf.Max(1, source.height);
            var scale = Mathf.Min(1f, maxDimension / (float)Mathf.Max(width, height));
            var targetWidth = Mathf.Max(1, Mathf.RoundToInt(width * scale));
            var targetHeight = Mathf.Max(1, Mathf.RoundToInt(height * scale));
            var renderTexture = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            Texture2D readable = null;
            try
            {
                Graphics.Blit(source, renderTexture);
                RenderTexture.active = renderTexture;
                readable = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
                readable.Apply(false, false);
                return readable.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(renderTexture);
                if (readable != null)
                {
                    if (Application.isPlaying) UnityEngine.Object.Destroy(readable);
                    else UnityEngine.Object.DestroyImmediate(readable);
                }
            }
        }
    }
}
