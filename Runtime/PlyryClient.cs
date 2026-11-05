using System;
using System.IO;
using UnityEngine;

namespace Plyry.Unity
{
    /// <summary>
    /// Owns a Plyry account session, catalog, generation job, and local media delivery.
    /// Place one instance in each scene that uses the runtime generator.
    /// </summary>
    public sealed class PlyryClient : MonoBehaviour
    {
        [SerializeField] private string apiBaseUrl = "https://plyry.com";
        [SerializeField] private string linkedAppName = string.Empty;
        [SerializeField] private PlyryCredentialStore credentialStore;
        [SerializeField] private PlyryRuntimePanel runtimePanel;
        [SerializeField] private PlyryGenerationTarget defaultTarget;
        [SerializeField] private float pollIntervalSeconds = 3f;

        private PlyryApiClient api;
        private PlyryJob activeJob;
        private bool pollInFlight;
        private bool isDownloading;
        private float nextPollAt;
        private PlyryGenerationTarget activeTarget;

        public PlyryApiClient Api { get { return api; } }
        public PlyryAccount Account { get; private set; }
        public PlyryCatalog Catalog { get; private set; }
        public string Status { get; private set; }
        public bool IsBusy { get; private set; }

        public event Action<string> StatusChanged;
        public event Action<PlyryAccount> AccountChanged;
        public event Action<PlyryCatalog> CatalogChanged;
        public event Action<PlyryStoredAsset> MediaDelivered;
        public event Action<PlyryApiResponse> AuthenticationResponded;
        public event Action TopupRequested;
        public event Action<string> ErrorRaised;

        private void Awake()
        {
            api = new PlyryApiClient(apiBaseUrl, credentialStore);
            if (!string.IsNullOrWhiteSpace(linkedAppName)) api.LinkedAppName = linkedAppName.Trim();
            else if (!string.IsNullOrWhiteSpace(Application.productName)) api.LinkedAppName = Application.productName;
            if (runtimePanel != null)
            {
                if (runtimePanel.Target == null) runtimePanel.Target = defaultTarget;
                runtimePanel.Initialize(this);
            }
        }

        private void Start()
        {
            RefreshSession();
        }

        private void Update()
        {
            if (api == null) return;
            api.Tick();
            if (Account != null && !api.HasSessionToken)
            {
                SetAccount(null);
                Catalog = null;
                activeJob = null;
                IsBusy = false;
                SetStatus("Your Plyry app session ended. Sign in again to continue.");
            }
            if (activeJob != null && !pollInFlight && !isDownloading && Time.unscaledTime >= nextPollAt)
                PollActiveJob();
        }

        private void OnDestroy()
        {
            if (api != null) api.Dispose();
        }

        public void RefreshSession()
        {
            if (api != null && !string.IsNullOrEmpty(api.ConfigurationError))
            {
                RaiseError(api.ConfigurationError);
                return;
            }
            SetStatus("Connecting to Plyry…");
            api.GetSession(delegate(PlyryApiResult result)
            {
                if (!result.Success || result.Response == null)
                {
                    RaiseError(result.Error);
                    return;
                }
                SetAccount(result.Response.user);
                if (Account == null)
                {
                    SetStatus(WithCredentialStoreWarning("Sign in to generate media."));
                    RefreshCatalog();
                    return;
                }
                api.GetWallet(delegate(PlyryApiResult walletResult)
                {
                    if (walletResult.Success && walletResult.Wallet != null)
                    {
                        Account.wallet_cents = walletResult.Wallet.wallet_cents;
                        SetAccount(Account);
                    }
                    else RaiseError(walletResult.Error);
                    RefreshCatalog();
                });
            });
        }

        public void RefreshCatalog()
        {
            api.GetCatalog(delegate(PlyryApiResult result)
            {
                if (!result.Success || result.Catalog == null)
                {
                    RaiseError(result.Error);
                    return;
                }
                Catalog = result.Catalog;
                SetStatus(WithCredentialStoreWarning(Account == null ? "Sign in to generate media." : "Ready"));
                if (CatalogChanged != null) CatalogChanged(Catalog);
            });
        }

        public void SignIn(string email, string password, string signupChallenge, string answer)
        {
            if (IsBusy) return;
            if (api != null && !string.IsNullOrEmpty(api.ConfigurationError))
            {
                RaiseError(api.ConfigurationError);
                return;
            }
            IsBusy = true;
            SetStatus("Signing in…");
            api.SignIn(email, password, signupChallenge, answer, delegate(PlyryApiResult result)
            {
                IsBusy = false;
                if (AuthenticationResponded != null) AuthenticationResponded(SanitizeAuthenticationResponse(result.Response));
                if (result.Success && !result.Authenticated && result.Response != null
                    && !string.IsNullOrEmpty(result.Response.signup_challenge)
                    && result.Response.user == null)
                {
                    SetStatus("Answer the signup check to create your Plyry account.");
                    return;
                }
                if (!result.Success || result.Response == null)
                {
                    SetStatus(result.Error);
                    if (ErrorRaised != null) ErrorRaised(result.Error);
                    return;
                }
                SetAccount(result.Response.user);
                RefreshSession();
            });
        }

        public void SignOut()
        {
            if (IsBusy) return;
            IsBusy = true;
            api.Logout(delegate(PlyryApiResult result)
            {
                IsBusy = false;
                SetAccount(null);
                activeJob = null;
                Catalog = null;
                SetStatus(WithCredentialStoreWarning(result.Success
                    ? "Signed out."
                    : "Signed out locally. The service could not confirm revocation: " + result.Error));
                RefreshCatalog();
            });
        }

        public void OpenWallet()
        {
            if (runtimePanel != null && runtimePanel.CreatesDefaultUi) runtimePanel.ShowTopup();
            else
            {
                SetStatus("Open the wallet top-up view in your game's account UI.");
                if (TopupRequested != null) TopupRequested();
            }
        }

        public void RequestQuote(PlyryGenerationRequest request, Action<PlyryApiResult> callback)
        {
            if (api != null && !string.IsNullOrEmpty(api.ConfigurationError))
            {
                if (callback != null) callback(new PlyryApiResult { Success = false, Error = api.ConfigurationError });
                return;
            }
            if (IsBusy)
            {
                if (callback != null) callback(new PlyryApiResult { Success = false, Error = "Plyry is already processing a request." });
                return;
            }
            if (Account == null)
            {
                if (callback != null) callback(new PlyryApiResult { Success = false, Error = "Sign in before requesting a generation quote." });
                return;
            }
            IsBusy = true;
            SetStatus("Getting the current Plyry price…");
            api.Quote(request, delegate(PlyryApiResult result)
            {
                IsBusy = false;
                SetStatus(result.Success && result.Response != null ? "Price quote ready." : result.Error);
                if (callback != null) callback(result);
            });
        }

        public void Generate(PlyryGenerationRequest request, PlyryGenerationTarget target)
        {
            if (IsBusy) return;
            if (target == null) target = defaultTarget;
            if (api != null && !string.IsNullOrEmpty(api.ConfigurationError))
            {
                RaiseError(api.ConfigurationError);
                return;
            }
            if (Account == null)
            {
                RaiseError("Sign in to your Plyry account before generating.");
                return;
            }
            if (request == null || request.ModelOption == null)
            {
                RaiseError("Choose an available media model.");
                return;
            }
            if (target != null && !target.Allows(request.Type))
            {
                RaiseError("This game object does not allow that media kind.");
                return;
            }
            var needsReference = request.ModelOption.requires_reference || !string.IsNullOrEmpty(request.ReferenceMode);
            if ((request.ModelOption.requires_reference && string.IsNullOrEmpty(request.ReferenceMode))
                || (needsReference && (request.References == null || request.References.Length == 0)))
            {
                RaiseError("Choose a reference mode and add a supported reference file.");
                return;
            }

            activeTarget = target;
            var submittedRequest = request;
            if (target != null)
            {
                submittedRequest = new PlyryGenerationRequest
                {
                    Type = request.Type,
                    Model = request.Model,
                    Prompt = target.ComposePrompt(request.Prompt),
                    Quality = request.Quality,
                    ReferenceMode = request.ReferenceMode,
                    ModelOption = request.ModelOption,
                    Settings = request.Settings,
                    SettingsOptions = request.SettingsOptions,
                    References = request.References,
                    SplatQuality = request.SplatQuality
                };
            }
            IsBusy = true;
            SetStatus("Submitting generation…");
            api.Generate(submittedRequest, delegate(PlyryApiResult result)
            {
                if (!result.Success || result.Response == null || result.Response.job == null)
                {
                    IsBusy = false;
                    if (result.Response != null && result.Response.error == "payment_required")
                    {
                        SetStatus(result.Error + " Open the wallet to add funds.");
                        if (ErrorRaised != null) ErrorRaised(result.Error);
                    }
                    else RaiseError(result.Error);
                    return;
                }
                activeJob = result.Response.job;
                nextPollAt = Time.unscaledTime + Mathf.Max(1f, pollIntervalSeconds);
                SetStatus("Generation queued. Checking for the result…");
            });
        }

        public void AddReference(PlyryReference reference)
        {
            if (runtimePanel != null) runtimePanel.AddReference(reference);
        }

        private void PollActiveJob()
        {
            pollInFlight = true;
            api.GetJob(activeJob.hash, delegate(PlyryApiResult result)
            {
                pollInFlight = false;
                nextPollAt = Time.unscaledTime + Mathf.Max(1f, pollIntervalSeconds);
                if (!result.Success || result.Response == null || result.Response.job == null)
                {
                    SetStatus(string.IsNullOrEmpty(result.Error) ? "Waiting to check the generation…" : result.Error);
                    return;
                }

                activeJob = result.Response.job;
                var state = (activeJob.status ?? string.Empty).ToLowerInvariant();
                if (state == "failed" || state == "aborted")
                {
                    IsBusy = false;
                    activeJob = null;
                    RaiseError(string.IsNullOrEmpty(result.Response.job.error) ? "Plyry could not complete this generation." : result.Response.job.error);
                    return;
                }
                if (state != "completed")
                {
                    SetStatus("Generation " + (string.IsNullOrEmpty(state) ? "in progress" : state) + "…");
                    return;
                }
                if (!activeJob.media_available || string.IsNullOrEmpty(activeJob.media_url))
                {
                    SetStatus("Generation completed. Waiting for its media file…");
                    return;
                }
                DownloadActiveMedia(activeJob);
            });
        }

        private void DownloadActiveMedia(PlyryJob job)
        {
            isDownloading = true;
            SetStatus("Downloading and checking the generated file…");
            api.DownloadMedia(job.media_url, delegate(PlyryApiResult result)
            {
                if (!result.Success || result.Media == null)
                {
                    isDownloading = false;
                    IsBusy = false;
                    activeJob = null;
                    RaiseError(result.Error);
                    return;
                }

                try
                {
                    var targetId = activeTarget == null ? "library" : activeTarget.StableTargetId;
                    var stored = PlyryLocalLibrary.Store(targetId, job.type, job.hash,
                        result.Media.Extension, result.Media.TemporaryPath, result.Media.MimeType, result.Media.Sha256);
                    if (!string.IsNullOrEmpty(job.scene_url))
                    {
                        DownloadScene(job, stored, result.Media.CacheToken, result.Media.Sha256);
                        return;
                    }
                    PublishStoredAsset(job, stored, result.Media.CacheToken, result.Media.Sha256);
                }
                catch (Exception exception)
                {
                    isDownloading = false;
                    IsBusy = false;
                    activeJob = null;
                    RaiseError("The media was downloaded but could not be saved on this device: " + exception.Message);
                }
            });
        }

        private void DownloadScene(PlyryJob job, PlyryStoredAsset stored, string cacheToken, string sha256)
        {
            api.DownloadMedia(job.scene_url, delegate(PlyryApiResult result)
            {
                var sceneSaved = false;
                if (result.Success && result.Media != null)
                {
                    try
                    {
                        stored.ScenePath = PlyryLocalLibrary.StoreScene(stored.TargetId, job.type, job.hash, result.Media.TemporaryPath);
                        sceneSaved = true;
                    }
                    catch (Exception exception) { Debug.LogWarning("Plyry could not save the splat scene file: " + exception.Message); }
                }
                PublishStoredAsset(job, stored, sceneSaved ? cacheToken : string.Empty, sha256);
                if (!sceneSaved)
                    Debug.LogWarning("Plyry kept the server copy because the splat scene file was not saved: " + result.Error);
            });
        }

        private void PublishStoredAsset(PlyryJob job, PlyryStoredAsset stored, string cacheToken, string sha256)
        {
            if (activeTarget != null) activeTarget.Receive(stored);
            if (MediaDelivered != null) MediaDelivered(stored);
            FinishDelivery(job, cacheToken, sha256);
        }

        private void FinishDelivery(PlyryJob job, string cacheToken, string sha256)
        {
            isDownloading = false;
            IsBusy = false;
            activeJob = null;
            SetStatus("Saved to this device. " + job.type + " media is ready.");
            if (string.IsNullOrEmpty(cacheToken))
            {
                RefreshSession();
                return;
            }
            api.AcknowledgeCache(job.hash, cacheToken, sha256, delegate(PlyryApiResult result)
            {
                if (!result.Success)
                    SetStatus("Media is saved on this device. Plyry kept its server copy because delivery confirmation did not finish.");
            });
            RefreshSession();
        }

        private void SetAccount(PlyryAccount account)
        {
            Account = account;
            if (AccountChanged != null) AccountChanged(Account);
        }

        private static PlyryApiResponse SanitizeAuthenticationResponse(PlyryApiResponse response)
        {
            if (response == null) return null;
            return new PlyryApiResponse
            {
                ok = response.ok,
                error = response.error,
                message = response.message,
                question = response.question,
                signup_challenge = response.signup_challenge,
                created = response.created,
                user = response.user,
                linked_app = response.linked_app
            };
        }

        private void SetStatus(string message)
        {
            Status = message ?? string.Empty;
            if (StatusChanged != null) StatusChanged(Status);
        }

        private string WithCredentialStoreWarning(string message)
        {
            if (api == null || string.IsNullOrEmpty(api.CredentialStoreWarning)) return message;
            return message + " " + api.CredentialStoreWarning;
        }

        private void RaiseError(string message)
        {
            if (api != null && !api.HasSessionToken && Account != null)
            {
                SetAccount(null);
                Catalog = null;
                activeJob = null;
            }
            var text = string.IsNullOrEmpty(message) ? "Plyry could not complete the request." : message;
            SetStatus(WithCredentialStoreWarning(text));
            if (ErrorRaised != null) ErrorRaised(text);
        }
    }
}


