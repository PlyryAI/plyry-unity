using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using UnityEngine.Video;

namespace Plyry.Unity
{
    /// <summary>
    /// Add this component to a game object players may customize with generated media.
    /// Assign a stable target ID and allowed media kinds in the Inspector.
    /// </summary>
    public sealed class PlyryGenerationTarget : MonoBehaviour
    {
        [SerializeField] private string stableTargetId;
        [SerializeField] private string displayName;
        [TextArea(2, 5)]
        [SerializeField] private string promptInstructions;
        [SerializeField] private string[] allowedMediaTypes = new string[0];
        [SerializeField] private Renderer targetRenderer;
        [SerializeField] private VideoPlayer targetVideoPlayer;
        [SerializeField] private AudioSource targetAudioSource;
        [SerializeField] private bool applyImageToRenderer = true;
        [SerializeField] private UnityEvent<string> onMediaReady = new UnityEvent<string>();

        private Texture2D loadedTexture;
        private Material loadedMaterial;

        public string StableTargetId { get { return stableTargetId; } }
        public string DisplayName { get { return string.IsNullOrEmpty(displayName) ? gameObject.name : displayName; } }
        public string PromptInstructions { get { return promptInstructions ?? string.Empty; } }
        public UnityEvent<string> OnMediaReady { get { return onMediaReady; } }
        public event Action<PlyryStoredAsset> MediaReady;

        private void OnValidate()
        {
            if (string.IsNullOrEmpty(stableTargetId))
                stableTargetId = Guid.NewGuid().ToString("N");
            if (string.IsNullOrEmpty(displayName))
                displayName = gameObject.name;
            if (targetRenderer == null)
                targetRenderer = GetComponent<Renderer>();
            if (targetVideoPlayer == null)
                targetVideoPlayer = GetComponent<VideoPlayer>();
            if (targetAudioSource == null)
                targetAudioSource = GetComponent<AudioSource>();
        }

        public void GenerateNewStableTargetId()
        {
            stableTargetId = Guid.NewGuid().ToString("N");
        }

        private void OnDestroy()
        {
            if (loadedTexture != null)
            {
                if (Application.isPlaying) Destroy(loadedTexture);
                else DestroyImmediate(loadedTexture);
            }
            if (loadedMaterial != null)
            {
                if (Application.isPlaying) Destroy(loadedMaterial);
                else DestroyImmediate(loadedMaterial);
            }
            loadedTexture = null;
            loadedMaterial = null;
        }

        private void Start()
        {
            LoadSavedMedia();
        }

        public bool Allows(string mediaType)
        {
            if (allowedMediaTypes == null || allowedMediaTypes.Length == 0) return true;
            for (var i = 0; i < allowedMediaTypes.Length; i++)
                if (string.Equals(allowedMediaTypes[i], mediaType, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        public string ComposePrompt(string playerPrompt)
        {
            var prompt = (playerPrompt ?? string.Empty).Trim();
            var instructions = (promptInstructions ?? string.Empty).Trim();
            if (instructions.Length == 0) return prompt;
            return instructions + (prompt.Length == 0 ? string.Empty : "\n\n" + prompt);
        }

        public PlyryReference CaptureTextureReference(int slot)
        {
            if (targetRenderer == null || targetRenderer.sharedMaterial == null) return null;
            var material = targetRenderer.sharedMaterial;
            var texture = material.HasProperty("_BaseMap") ? material.GetTexture("_BaseMap") : null;
            if (texture == null && material.HasProperty("_MainTex")) texture = material.GetTexture("_MainTex");
            if (texture == null) return null;
            try
            {
                var bytes = PlyryImageUtility.EncodePng(texture, 2048);
                if (bytes == null || bytes.Length == 0) return null;
                return new PlyryReference(DisplayName + "-reference.png", "image/png", Mathf.Clamp(slot, 1, 7), bytes);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Plyry could not read this target's texture: " + exception.Message, this);
                return null;
            }
        }

        public void Receive(PlyryStoredAsset asset)
        {
            if (asset == null) return;
            if (applyImageToRenderer && (string.Equals(asset.MediaType, "image", StringComparison.OrdinalIgnoreCase)
                || string.Equals(asset.MediaType, "anime", StringComparison.OrdinalIgnoreCase)))
                ApplyImage(asset.Path);
            else if (string.Equals(asset.MediaType, "video", StringComparison.OrdinalIgnoreCase) && targetVideoPlayer != null)
                PlayVideo(asset.Path);
            else if ((string.Equals(asset.MediaType, "audio", StringComparison.OrdinalIgnoreCase)
                || string.Equals(asset.MediaType, "music", StringComparison.OrdinalIgnoreCase)) && targetAudioSource != null)
                StartCoroutine(LoadAudio(asset.Path));

            if (onMediaReady != null) onMediaReady.Invoke(asset.Path);
            if (MediaReady != null) MediaReady(asset);
        }

        public void LoadSavedMedia()
        {
            if (string.IsNullOrEmpty(stableTargetId)) return;
            if (allowedMediaTypes == null || allowedMediaTypes.Length == 0)
            {
                var saved = PlyryLocalLibrary.FindLatestForTarget(stableTargetId);
                for (var i = 0; i < saved.Length; i++) Receive(saved[i]);
                return;
            }
            for (var i = 0; i < allowedMediaTypes.Length; i++)
            {
                var asset = PlyryLocalLibrary.FindLatest(stableTargetId, allowedMediaTypes[i]);
                if (asset != null) Receive(asset);
            }
        }

        private void ApplyImage(string path)
        {
            if (targetRenderer == null || !File.Exists(path)) return;
            try
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!texture.LoadImage(File.ReadAllBytes(path)))
                {
                    if (Application.isPlaying) Destroy(texture);
                    else DestroyImmediate(texture);
                    return;
                }
                if (loadedTexture != null)
                {
                    if (Application.isPlaying) Destroy(loadedTexture);
                    else DestroyImmediate(loadedTexture);
                }
                loadedTexture = texture;
                if (loadedMaterial == null)
                {
                    loadedMaterial = new Material(targetRenderer.sharedMaterial);
                    targetRenderer.material = loadedMaterial;
                }
                var material = loadedMaterial;
                if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
                else material.mainTexture = texture;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Plyry could not apply the saved image: " + exception.Message, this);
            }
        }

        private void PlayVideo(string path)
        {
            if (targetVideoPlayer == null || !File.Exists(path)) return;
            targetVideoPlayer.source = VideoSource.Url;
            targetVideoPlayer.url = new Uri(path).AbsoluteUri;
            targetVideoPlayer.Play();
        }

        private IEnumerator LoadAudio(string path)
        {
            if (!File.Exists(path)) yield break;
            using (var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.UNKNOWN))
            {
                yield return request.SendWebRequest();
                if (HasRequestError(request))
                {
                    Debug.LogWarning("Plyry could not load the saved audio: " + request.error, this);
                    yield break;
                }
                targetAudioSource.clip = DownloadHandlerAudioClip.GetContent(request);
                targetAudioSource.Play();
            }
        }

        private static bool HasRequestError(UnityWebRequest request)
        {
#if UNITY_2020_2_OR_NEWER
            return request.result != UnityWebRequest.Result.Success;
#else
            return request.isNetworkError || request.isHttpError;
#endif
        }
    }
}


