using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

namespace Plyry.Unity
{
    public sealed class PlyryApiResult
    {
        public bool Success;
        public long StatusCode;
        public string Error;
        public bool Authenticated;
        public PlyryApiResponse Response;
        public PlyryCatalog Catalog;
        public PlyryWallet Wallet;
        public PlyryPaymentConfig PaymentConfig;
        public string PaymentMethodId;
        public string Body;
        public PlyryDownloadedMedia Media;
    }

    public sealed class PlyryGenerationRequest
    {
        public string Type;
        public string Model;
        public string Prompt;
        public string Quality = "full";
        public string ReferenceMode;
        public PlyryModelOption ModelOption;
        public PlyrySettingsSelection Settings;
        public PlyryGenerationOptions SettingsOptions;
        public PlyryReference[] References;
        public string SplatQuality = "low";
    }

    /// <summary>
    /// Small UnityWebRequest client for Plyry's public account and generation API.
    /// Call Tick from Update or EditorApplication.update to deliver completed requests.
    /// </summary>
    public sealed class PlyryApiClient : IDisposable
    {
        private sealed class Pending
        {
            public UnityWebRequest Request;
            public UnityWebRequestAsyncOperation Operation;
            public Action<PlyryApiResult> Callback;
            public bool IsDownload;
            public string TemporaryPath;
            public bool IsAuthenticated;
            public bool RetriedAfterRefresh;
            public Func<UnityWebRequest> RequestFactory;
        }

        private readonly List<Pending> pending = new List<Pending>();
        private bool disposed;

        public string BaseUrl { get; private set; }
        public string ConfigurationError { get; private set; }
        public string CredentialStoreWarning { get; private set; }
        public string LinkedAppName { get; set; }
        private readonly PlyryCredentialStore credentialStore;
        private string accessToken;
        private string refreshToken;
        public bool HasSessionToken { get { return !string.IsNullOrEmpty(accessToken) || !string.IsNullOrEmpty(refreshToken); } }
        public int RequestTimeoutSeconds { get; set; }
        public int DownloadTimeoutSeconds { get; set; }
        private DateTime accessTokenExpiresAtUtc;
        private bool refreshInFlight;
        private int sessionRevision;
        private readonly List<Action<PlyryApiResult>> refreshWaiters = new List<Action<PlyryApiResult>>();

        public PlyryApiClient(string baseUrl, PlyryCredentialStore credentialStore = null)
        {
            RequestTimeoutSeconds = 300;
            DownloadTimeoutSeconds = 900;
            LinkedAppName = "Plyry Unity";
            this.credentialStore = credentialStore;
            if (!SetBaseUrl(baseUrl)) BaseUrl = "https://plyry.com";
        }

        public bool SetBaseUrl(string baseUrl)
        {
            var value = string.IsNullOrEmpty(baseUrl) ? "https://plyry.com" : baseUrl.Trim();
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            {
                ConfigurationError = "Enter a valid Plyry service URL.";
                return false;
            }
            var localHost = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase);
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !(localHost && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
            {
                ConfigurationError = "Use HTTPS for the Plyry service URL.";
                return false;
            }
            var nextBaseUrl = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            if (nextBaseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                nextBaseUrl = nextBaseUrl.Substring(0, nextBaseUrl.Length - 3).TrimEnd('/');
            var serviceChanged = !string.Equals(BaseUrl, nextBaseUrl, StringComparison.OrdinalIgnoreCase);
            if (serviceChanged)
            {
                sessionRevision++;
                ClearMemoryTokens();
            }
            BaseUrl = nextBaseUrl;
            ConfigurationError = string.Empty;
            if (serviceChanged) LoadStoredRefreshToken();
            return true;
        }

        public void GetSession(Action<PlyryApiResult> callback)
        {
            if (!HasSessionToken)
            {
                if (callback != null) callback(new PlyryApiResult { Success = true, Response = new PlyryApiResponse { ok = true } });
                return;
            }
            SendAuthorizedJson("/v1/me", callback);
        }

        public void GetCatalog(Action<PlyryApiResult> callback)
        {
            SendPublicText(UnityWebRequest.Get(BuildUrl("/v1/models")), true, callback);
        }

        public void SignIn(string email, string password, string challenge, string answer, Action<PlyryApiResult> callback)
        {
            sessionRevision++;
            ClearMemoryTokens();
            ClearStoredRefreshToken(BaseUrl);
            var signInRevision = sessionRevision;
            var signInBaseUrl = BaseUrl;
            var scopes = "[\"account:read\",\"wallet:read\",\"wallet:topup\",\"generation:create\",\"generation:read\"]";
            var json = "{\"email\":\"" + Escape(email) + "\",\"password\":\"" + Escape(password)
                + "\",\"app_name\":\"" + Escape(string.IsNullOrWhiteSpace(LinkedAppName) ? "Plyry Unity" : LinkedAppName.Trim())
                + "\",\"scopes\":" + scopes;
            if (!string.IsNullOrEmpty(challenge))
            {
                json += ",\"signup_challenge\":\"" + Escape(challenge) + "\",\"answer\":\"" + Escape(answer) + "\"}";
                SendPublicJson("/v1/auth/signup", json, delegate(PlyryApiResult result)
                {
                    if (RejectStaleAuthenticationResponse(result, signInRevision, signInBaseUrl))
                    {
                        if (callback != null) callback(result);
                        return;
                    }
                    if (result.Success && (result.Response == null || !ApplyTokenResponse(result.Response)))
                    {
                        result.Success = false;
                        result.Error = "Plyry returned an invalid signup token response.";
                    }
                    else if (result.Success)
                    {
                        result.Authenticated = true;
                    }
                    SanitizeAuthResult(result);
                    if (callback != null) callback(result);
                });
                return;
            }

            json += "}";
            SendPublicJson("/v1/auth/login", json, delegate(PlyryApiResult result)
            {
                if (RejectStaleAuthenticationResponse(result, signInRevision, signInBaseUrl))
                {
                    if (callback != null) callback(result);
                    return;
                }
                if (result.Success)
                {
                    if (result.Response == null || !ApplyTokenResponse(result.Response))
                    {
                        result.Success = false;
                        result.Error = "Plyry returned an invalid sign-in token response.";
                        SanitizeAuthResult(result);
                        if (callback != null) callback(result);
                        return;
                    }
                    result.Authenticated = true;
                    SanitizeAuthResult(result);
                    if (callback != null) callback(result);
                    return;
                }
                if (result.Response == null || result.Response.error != "signup_required")
                {
                    SanitizeAuthResult(result);
                    if (callback != null) callback(result);
                    return;
                }
                GetSignupChallenge(email, delegate(PlyryApiResult challengeResult)
                {
                    if (RejectStaleAuthenticationResponse(challengeResult, signInRevision, signInBaseUrl))
                    {
                        if (callback != null) callback(challengeResult);
                        return;
                    }
                    if (challengeResult.Response != null) challengeResult.Response.error = "signup_required";
                    SanitizeAuthResult(challengeResult);
                    if (callback != null) callback(challengeResult);
                });
            });
        }

        public void Logout(Action<PlyryApiResult> callback)
        {
            var token = refreshToken;
            sessionRevision++;
            ClearMemoryTokens();
            ClearStoredRefreshToken(BaseUrl);
            if (string.IsNullOrEmpty(token))
            {
                if (callback != null) callback(new PlyryApiResult { Success = true, Response = new PlyryApiResponse { ok = true } });
                return;
            }
            SendPublicJson("/v1/auth/revoke", "{\"refresh_token\":\"" + Escape(token) + "\"}", callback);
        }

        public void GetSignupChallenge(string email, Action<PlyryApiResult> callback)
        {
            SendPublicJson("/v1/auth/signup/challenges", "{\"email\":\"" + Escape(email) + "\"}", callback);
        }

        public void Generate(PlyryGenerationRequest generation, Action<PlyryApiResult> callback)
        {
            if (!HasSessionToken)
            {
                if (callback != null) callback(Failure("Sign in to your Plyry account before submitting a generation."));
                return;
            }
            if (generation == null)
            {
                if (callback != null) callback(Failure("No generation request was provided."));
                return;
            }

            string referenceError;
            if (!ValidateReferences(generation, out referenceError))
            {
                if (callback != null) callback(Failure(referenceError));
                return;
            }

            var form = new WWWForm();
            form.AddField("prompt", generation.Prompt ?? string.Empty);
            form.AddField("type", generation.Type ?? "image");
            form.AddField("model", generation.Model ?? string.Empty);
            form.AddField("quality", string.Equals(generation.Quality, "test", StringComparison.OrdinalIgnoreCase) ? "test" : "full");
            if (generation.Type == "splat") form.AddField("splat_quality", string.IsNullOrEmpty(generation.SplatQuality) ? "low" : generation.SplatQuality);
            var references = generation.References ?? new PlyryReference[0];
            form.AddField("reference_count", references.Length.ToString());
            if (!string.IsNullOrEmpty(generation.ReferenceMode))
                form.AddField("reference_mode", generation.ReferenceMode);
            if (generation.ModelOption != null)
            {
                var settings = generation.Settings ?? PlyrySettingsSelection.Defaults(generation.SettingsOptions ?? generation.ModelOption.generation_options, false);
                form.AddField("queue_options", settings.ToQueueOptionsJson(generation.SettingsOptions ?? generation.ModelOption.generation_options));
            }

            for (var i = 0; i < references.Length && i < 7; i++)
            {
                var reference = references[i];
                if (reference == null || reference.Data == null || reference.Data.Length == 0) continue;
                var fileName = string.IsNullOrEmpty(reference.FileName) ? "reference-" + (i + 1) + ".png" : SafeFileName(reference.FileName);
                var mimeType = NormalizeMimeType(reference.MimeType);
                form.AddField("reference_slots[]", Mathf.Clamp(reference.Slot, 1, 7).ToString());
                form.AddBinaryData("references[]", reference.Data, fileName, mimeType);
            }

            SendAuthorized(delegate
            {
                return UnityWebRequest.Post(BuildUrl("/v1/generations"), form);
            }, callback);
        }

        public void Quote(PlyryGenerationRequest generation, Action<PlyryApiResult> callback)
        {
            if (!HasSessionToken)
            {
                if (callback != null) callback(Failure("Sign in to your Plyry account before requesting a price quote."));
                return;
            }
            if (generation == null || generation.ModelOption == null)
            {
                if (callback != null) callback(Failure("Choose an available media model before requesting a quote."));
                return;
            }
            var options = generation.SettingsOptions ?? generation.ModelOption.generation_options;
            var settings = generation.Settings ?? PlyrySettingsSelection.Defaults(options, generation.Quality == "test");
            var json = "{\"type\":\"" + Escape(generation.Type) + "\",\"model\":\"" + Escape(generation.Model)
                + "\",\"quality\":\"" + Escape(generation.Quality) + "\",\"reference_mode\":\"" + Escape(generation.ReferenceMode)
                + "\",\"queue_options\":" + settings.ToQueueOptionsJson(options);
            if (generation.Type == "splat")
                json += ",\"splat_quality\":\"" + Escape(generation.SplatQuality) + "\"";
            json += "}";
            SendJson("/v1/quotes", json, callback);
        }

        private static bool ValidateReferences(PlyryGenerationRequest generation, out string error)
        {
            error = string.Empty;
            var references = generation.References ?? new PlyryReference[0];
            var modeName = generation.ReferenceMode ?? string.Empty;
            if (generation.ModelOption != null && generation.ModelOption.requires_reference
                && (modeName.Length == 0 || references.Length == 0))
            {
                error = "This model requires a reference mode and at least one reference file.";
                return false;
            }
            if (references.Length == 0 && modeName.Length == 0) return true;
            if (modeName.Length == 0)
            {
                error = "Choose a reference mode before adding reference files.";
                return false;
            }

            PlyryReferenceMode mode = null;
            var modes = generation.ModelOption == null ? null : generation.ModelOption.references;
            if (modes != null)
            {
                for (var i = 0; i < modes.Length; i++)
                    if (string.Equals(modes[i].mode, modeName, StringComparison.OrdinalIgnoreCase))
                    {
                        mode = modes[i];
                        break;
                    }
            }
            if (mode == null)
            {
                error = "The selected reference mode is not available for this model.";
                return false;
            }
            if (references.Length == 0)
            {
                error = "Add a reference file for the selected reference mode.";
                return false;
            }

            var maximum = Mathf.Clamp(mode.max_files <= 0 ? 1 : mode.max_files, 1, 7);
            if (references.Length > maximum)
            {
                error = "The selected reference mode accepts up to " + maximum + " file(s).";
                return false;
            }

            var usedSlots = new HashSet<int>();
            var mediaTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < references.Length; i++)
            {
                var reference = references[i];
                if (reference == null || reference.Data == null || reference.Data.Length == 0)
                {
                    error = "One of the selected reference files is empty.";
                    return false;
                }
                if (reference.Slot < 1 || reference.Slot > maximum || !usedSlots.Add(reference.Slot))
                {
                    error = "Reference slots must be unique and within the selected mode's labeled slots.";
                    return false;
                }

                var mime = (reference.MimeType ?? string.Empty).Trim().ToLowerInvariant();
                var separator = mime.IndexOf(';');
                if (separator >= 0) mime = mime.Substring(0, separator).Trim();
                var accepted = mode.accept ?? new string[0];
                if (mime.Length == 0 || !ContainsIgnoreCase(accepted, mime))
                {
                    error = "A reference file type is not supported by the selected mode.";
                    return false;
                }
                var mediaType = mime.StartsWith("image/", StringComparison.Ordinal) ? "image"
                    : (mime.StartsWith("video/", StringComparison.Ordinal) ? "video"
                        : (mime.StartsWith("audio/", StringComparison.Ordinal) ? "audio" : string.Empty));
                mediaTypes.Add(mediaType);
                if (mode.fields != null && reference.Slot <= mode.fields.Length)
                {
                    var field = mode.fields[reference.Slot - 1];
                    if (field != null && !string.IsNullOrEmpty(field.media_type)
                        && !string.Equals(field.media_type, mediaType, StringComparison.OrdinalIgnoreCase))
                    {
                        error = "A reference file does not match its labeled media slot.";
                        return false;
                    }
                }
            }

            if (HasMissingMediaType(mode.require_all_media_types, mediaTypes))
            {
                error = "Add a file for each required media type in the selected reference mode.";
                return false;
            }
            var requiredAny = mode.require_any_media_types ?? new string[0];
            if (requiredAny.Length > 0 && !HasAnyMediaType(requiredAny, mediaTypes))
            {
                error = "The selected reference mode needs at least one of its allowed media types.";
                return false;
            }
            return true;
        }

        private static bool ContainsIgnoreCase(string[] values, string sought)
        {
            for (var i = 0; values != null && i < values.Length; i++)
                if (string.Equals(values[i], sought, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool HasMissingMediaType(string[] required, HashSet<string> available)
        {
            for (var i = 0; required != null && i < required.Length; i++)
                if (!available.Contains(required[i])) return true;
            return false;
        }

        private static bool HasAnyMediaType(string[] required, HashSet<string> available)
        {
            for (var i = 0; required != null && i < required.Length; i++)
                if (available.Contains(required[i])) return true;
            return false;
        }

        public void GetJob(string hash, Action<PlyryApiResult> callback)
        {
            SendAuthorized(delegate
            {
                return UnityWebRequest.Get(BuildUrl("/v1/generations/" + Uri.EscapeDataString(hash)));
            }, callback);
        }

        public void DownloadMedia(string mediaUrl, Action<PlyryApiResult> callback)
        {
            string url;
            if (!TryResolveMediaUrl(mediaUrl, out url))
            {
                if (callback != null) callback(Failure("Plyry returned a media URL outside the configured service."));
                return;
            }

            var path = Path.Combine(Application.temporaryCachePath, "plyry-" + Guid.NewGuid().ToString("N") + ".part");
            SendAuthorized(delegate
            {
                var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET);
#if UNITY_WEBGL && !UNITY_EDITOR
                request.downloadHandler = new DownloadHandlerBuffer();
#else
                request.downloadHandler = new DownloadHandlerFile(path);
#endif
                return request;
            }, callback, true, path);
        }

        public void AcknowledgeCache(string hash, string cacheToken, string sha256, Action<PlyryApiResult> callback)
        {
            var json = "{\"cache_token\":\"" + Escape(cacheToken) + "\",\"sha256\":\"" + Escape(sha256) + "\"}";
            SendJson("/v1/generations/" + Uri.EscapeDataString(hash) + "/cache", json, callback);
        }

        public void GetWallet(Action<PlyryApiResult> callback)
        {
            SendAuthorizedJson("/v1/wallet", delegate(PlyryApiResult result)
            {
                if (result.Success)
                {
                    try
                    {
                        result.Wallet = JsonUtility.FromJson<PlyryWallet>(result.Body);
                        if (result.Wallet == null) throw new InvalidDataException("Plyry returned an empty wallet response.");
                        if (result.Response == null) result.Response = new PlyryApiResponse { ok = true };
                        result.Response.wallet = result.Wallet;
                        result.Response.wallet_cents = result.Wallet.wallet_cents;
                    }
                    catch (Exception exception) { result.Success = false; result.Error = "The wallet response could not be read: " + exception.Message; }
                }
                if (callback != null) callback(result);
            });
        }

        public void GetPaymentConfig(Action<PlyryApiResult> callback)
        {
            SendAuthorizedJson("/v1/payments/config", delegate(PlyryApiResult result)
            {
                if (result.Success)
                {
                    try
                    {
                        result.PaymentConfig = JsonUtility.FromJson<PlyryPaymentConfig>(result.Body);
                        if (result.PaymentConfig == null) throw new InvalidDataException("Plyry returned an empty payment configuration.");
                    }
                    catch (Exception exception) { result.Success = false; result.Error = "The payment configuration could not be read: " + exception.Message; }
                }
                if (callback != null) callback(result);
            });
        }

        public void CreateOnvoPaymentMethod(PlyryPaymentConfig config, string email, string holderName,
            string number, int expiryMonth, int expiryYear, string securityCode, string country,
            Action<PlyryApiResult> callback)
        {
            var onvo = config == null ? null : config.onvo;
            var apiBase = onvo == null ? string.Empty : (onvo.api_base ?? string.Empty).TrimEnd('/') + "/";
            var publishableKey = onvo == null ? string.Empty : onvo.publishable_key ?? string.Empty;
            Uri baseUri;
            if (!Uri.TryCreate(apiBase, UriKind.Absolute, out baseUri)
                || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !(string.Equals(baseUri.Host, "onvopay.com", StringComparison.OrdinalIgnoreCase)
                    || baseUri.Host.EndsWith(".onvopay.com", StringComparison.OrdinalIgnoreCase))
                || !Regex.IsMatch(publishableKey, "^onvo_(test|live)_publishable_key_.+$", RegexOptions.CultureInvariant)
                || publishableKey.IndexOf("replace_me", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (callback != null) callback(Failure("Card payments are not configured."));
                return;
            }

            var json = "{\"type\":\"card\",\"card\":{\"number\":\"" + Escape(number)
                + "\",\"expMonth\":" + expiryMonth + ",\"expYear\":" + expiryYear
                + ",\"cvv\":\"" + Escape(securityCode) + "\",\"holderName\":\"" + Escape(holderName)
                + "\"},\"billing\":{\"name\":\"" + Escape(holderName) + "\",\"address\":{\"country\":\""
                + Escape(country) + "\"}},\"customer\":{\"name\":\"" + Escape(holderName)
                + "\",\"email\":\"" + Escape(email) + "\"}}";
            var request = new UnityWebRequest(new Uri(baseUri, "v1/payment-methods").AbsoluteUri, UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Authorization", "Bearer " + publishableKey);
            request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
            request.SetRequestHeader("Accept", "application/json");
            SendPublicRequest(request, delegate(PlyryApiResult result)
            {
                PlyryOnvoPaymentMethod method = null;
                try { method = JsonUtility.FromJson<PlyryOnvoPaymentMethod>(result.Body); }
                catch (Exception) { }
                result.Success = result.StatusCode >= 200 && result.StatusCode < 300 && method != null && !string.IsNullOrEmpty(method.id);
                result.PaymentMethodId = method == null ? string.Empty : method.id;
                if (!result.Success)
                    result.Error = method != null && !string.IsNullOrEmpty(method.message)
                        ? method.message
                        : (string.IsNullOrEmpty(result.Error) ? "The payment provider could not validate this card." : result.Error);
                if (callback != null) callback(result);
            });
        }

        public void CreateTopup(int amountCents, string paymentMethodId, string country, string returnUrl,
            string requestKey, Action<PlyryApiResult> callback)
        {
            var json = "{\"amount_cents\":" + amountCents + ",\"payment_method_id\":\"" + Escape(paymentMethodId)
                + "\",\"billing_country\":\"" + Escape(country) + "\",\"return_url\":\"" + Escape(returnUrl) + "\"}";
            SendJson("/v1/wallet/topups", json, callback, requestKey);
        }

        public void GetTopupStatus(string requestKey, Action<PlyryApiResult> callback)
        {
            SendAuthorized(delegate
            {
                return UnityWebRequest.Get(BuildUrl("/v1/wallet/topups/requests/" + Uri.EscapeDataString(requestKey)));
            }, callback);
        }

        public void Tick()
        {
            if (disposed) return;
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                if (disposed) return;
                if (i >= pending.Count) continue;
                var item = pending[i];
                if (!item.Operation.isDone) continue;
                pending.RemoveAt(i);
                var result = item.IsDownload ? FinishDownload(item) : FinishResponse(item.Request);
                item.Request.Dispose();
                if (item.IsAuthenticated && result.StatusCode == 401 && !item.RetriedAfterRefresh
                    && item.RequestFactory != null)
                {
                    DeleteIfPresent(item.TemporaryPath);
                    EnsureAccessToken(delegate(PlyryApiResult auth)
                    {
                        if (!auth.Success)
                        {
                            if (item.Callback != null) item.Callback(auth);
                            return;
                        }
                        UnityWebRequest retry;
                        try { retry = item.RequestFactory(); }
                        catch (Exception exception)
                        {
                            if (item.Callback != null) item.Callback(Failure(exception.Message));
                            return;
                        }
                        retry.downloadHandler = retry.downloadHandler ?? new DownloadHandlerBuffer();
                        AddHeaders(retry, true);
                        AddPending(retry, item.IsDownload, item.TemporaryPath, item.Callback,
                            item.RequestFactory, true, true);
                    }, true);
                    continue;
                }
                if (item.Callback != null) item.Callback(result);
                if (disposed) return;
            }
        }

        public void Dispose()
        {
            disposed = true;
            for (var i = pending.Count - 1; i >= 0; i--)
            {
                var item = pending[i];
                item.Request.Abort();
                item.Request.Dispose();
                DeleteIfPresent(item.TemporaryPath);
            }
            pending.Clear();
        }

        private void SendJson(string path, string json, Action<PlyryApiResult> callback, string idempotencyKey = null)
        {
            SendAuthorized(delegate
            {
                var request = new UnityWebRequest(BuildUrl(path), UnityWebRequest.kHttpVerbPOST);
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json ?? "{}"));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
                if (!string.IsNullOrEmpty(idempotencyKey)) request.SetRequestHeader("Idempotency-Key", idempotencyKey);
                return request;
            }, callback);
        }

        private void SendAuthorizedJson(string path, Action<PlyryApiResult> callback)
        {
            SendAuthorized(delegate
            {
                return UnityWebRequest.Get(BuildUrl(path));
            }, callback);
        }

        private void SendPublicJson(string path, string json, Action<PlyryApiResult> callback)
        {
            var request = new UnityWebRequest(BuildUrl(path), UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json ?? "{}"));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
            SendPublicRequest(request, callback);
        }

        private void SendPublicText(UnityWebRequest request, bool isCatalog, Action<PlyryApiResult> callback)
        {
            request.downloadHandler = new DownloadHandlerBuffer();
            AddHeaders(request, false);
            AddPending(request, false, null, delegate(PlyryApiResult result)
            {
                if (result.Success && isCatalog)
                {
                    try { result.Catalog = JsonUtility.FromJson<PlyryCatalog>(result.Body); }
                    catch (Exception exception) { result.Success = false; result.Error = "The model catalog could not be read: " + exception.Message; }
                    if (result.Catalog == null || !result.Catalog.ok)
                    {
                        result.Success = false;
                        if (string.IsNullOrEmpty(result.Error)) result.Error = "Plyry returned an invalid model catalog.";
                    }
                }
                if (callback != null) callback(result);
            });
        }

        private void SendPublicRequest(UnityWebRequest request, Action<PlyryApiResult> callback)
        {
            request.downloadHandler = request.downloadHandler ?? new DownloadHandlerBuffer();
            AddHeaders(request, false);
            AddPending(request, false, null, callback);
        }

        private void SendAuthorized(Func<UnityWebRequest> factory, Action<PlyryApiResult> callback,
            bool isDownload = false, string path = null)
        {
            EnsureAccessToken(delegate(PlyryApiResult auth)
            {
                if (!auth.Success)
                {
                    DeleteIfPresent(path);
                    if (callback != null) callback(auth);
                    return;
                }
                UnityWebRequest request;
                try { request = factory(); }
                catch (Exception exception)
                {
                    DeleteIfPresent(path);
                    if (callback != null) callback(Failure(exception.Message));
                    return;
                }
                request.downloadHandler = request.downloadHandler ?? new DownloadHandlerBuffer();
                AddHeaders(request, true);
                AddPending(request, isDownload, path, callback, factory, true);
            });
        }

        private void EnsureAccessToken(Action<PlyryApiResult> callback, bool forceRefresh = false)
        {
            if (string.IsNullOrEmpty(refreshToken))
            {
                if (callback != null) callback(Failure("Sign in to your Plyry account first."));
                return;
            }
            if (!forceRefresh && !string.IsNullOrEmpty(accessToken) && DateTime.UtcNow.AddSeconds(30) < accessTokenExpiresAtUtc)
            {
                if (callback != null) callback(new PlyryApiResult { Success = true });
                return;
            }

            refreshWaiters.Add(callback);
            if (refreshInFlight) return;
            refreshInFlight = true;
            var refreshRevision = sessionRevision;
            var refreshBaseUrl = BaseUrl;
            SendPublicJson("/v1/auth/refresh", "{\"refresh_token\":\"" + Escape(refreshToken) + "\"}", delegate(PlyryApiResult result)
            {
                refreshInFlight = false;
                if (refreshRevision != sessionRevision)
                {
                    result.Success = false;
                    result.Error = "The Plyry session changed while its token was refreshing. Retry the request.";
                    if (result.Response != null && !string.IsNullOrEmpty(result.Response.refresh_token))
                        RevokeRefreshTokenAt(refreshBaseUrl, result.Response.refresh_token);
                    ClearResponseTokens(result.Response);
                }
                else
                {
                    var invalidTokenResponse = result.Success && (result.Response == null || !ApplyTokenResponse(result.Response));
                    if (invalidTokenResponse)
                    {
                        result.Success = false;
                        result.Error = "Plyry returned an invalid token refresh response. Sign in again.";
                    }
                    if (invalidTokenResponse || IsInvalidRefreshToken(result))
                    {
                        ClearMemoryTokens();
                        ClearStoredRefreshToken(BaseUrl);
                    }
                }
                SanitizeAuthResult(result);
                var waiters = refreshWaiters.ToArray();
                refreshWaiters.Clear();
                for (var i = 0; i < waiters.Length; i++)
                    if (waiters[i] != null) waiters[i](result);
            });
        }

        private bool ApplyTokenResponse(PlyryApiResponse response)
        {
            if (response == null || string.IsNullOrEmpty(response.access_token) || string.IsNullOrEmpty(response.refresh_token))
                return false;
            accessToken = response.access_token;
            refreshToken = response.refresh_token;
            accessTokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(Mathf.Clamp(response.expires_in, 60, 86400));
            SaveStoredRefreshToken(BaseUrl, refreshToken);
            ClearResponseTokens(response);
            return true;
        }

        private static void ClearResponseTokens(PlyryApiResponse response)
        {
            if (response == null) return;
            response.access_token = string.Empty;
            response.refresh_token = string.Empty;
        }

        private static void SanitizeAuthResult(PlyryApiResult result)
        {
            if (result == null) return;
            ClearResponseTokens(result.Response);
            result.Body = string.Empty;
        }

        private bool RejectStaleAuthenticationResponse(PlyryApiResult result, int expectedRevision, string expectedBaseUrl)
        {
            if (expectedRevision == sessionRevision && string.Equals(expectedBaseUrl, BaseUrl, StringComparison.OrdinalIgnoreCase))
                return false;
            if (result != null && result.Response != null && !string.IsNullOrEmpty(result.Response.refresh_token))
                RevokeRefreshTokenAt(expectedBaseUrl, result.Response.refresh_token);
            SanitizeAuthResult(result);
            if (result != null)
            {
                result.Success = false;
                result.Authenticated = false;
                result.Error = "The Plyry service or sign-in session changed before authentication completed.";
            }
            return true;
        }

        private void RevokeRefreshTokenAt(string serviceUrl, string token)
        {
            if (string.IsNullOrEmpty(serviceUrl) || string.IsNullOrEmpty(token)) return;
            var body = "{\"refresh_token\":\"" + Escape(token) + "\"}";
            if (string.Equals(serviceUrl, BaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                SendPublicJson("/v1/auth/revoke", body, null);
                return;
            }
            if (!string.IsNullOrEmpty(ConfigurationError)) return;
            var request = new UnityWebRequest(serviceUrl.TrimEnd('/') + "/v1/auth/revoke", UnityWebRequest.kHttpVerbPOST);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
            SendPublicRequest(request, null);
        }

        private static bool IsInvalidRefreshToken(PlyryApiResult result)
        {
            if (result == null) return false;
            return result.StatusCode == 401
                || (result.Response != null && (string.Equals(result.Response.error, "invalid_refresh_token", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(result.Response.error, "refresh_token_expired", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(result.Response.error, "refresh_token_revoked", StringComparison.OrdinalIgnoreCase)));
        }

        private void ClearMemoryTokens()
        {
            accessToken = string.Empty;
            refreshToken = string.Empty;
            accessTokenExpiresAtUtc = DateTime.MinValue;
        }

        private void LoadStoredRefreshToken()
        {
            CredentialStoreWarning = string.Empty;
            if (credentialStore == null || string.IsNullOrEmpty(BaseUrl)) return;
            try
            {
                string storedToken;
                if (credentialStore.TryLoadRefreshToken(BaseUrl, out storedToken) && !string.IsNullOrEmpty(storedToken))
                    refreshToken = storedToken;
                CredentialStoreWarning = string.Empty;
            }
            catch (Exception)
            {
                CredentialStoreWarning = "The configured secure credential store could not load the Plyry session.";
            }
        }

        private void SaveStoredRefreshToken(string serviceUrl, string token)
        {
            if (credentialStore == null) return;
            try
            {
                credentialStore.SaveRefreshToken(serviceUrl, token);
                CredentialStoreWarning = string.Empty;
            }
            catch (Exception)
            {
                CredentialStoreWarning = "The Plyry session is active, but the configured secure credential store could not save it.";
            }
        }

        private void ClearStoredRefreshToken(string serviceUrl)
        {
            if (credentialStore == null || string.IsNullOrEmpty(serviceUrl)) return;
            try
            {
                credentialStore.ClearRefreshToken(serviceUrl);
                CredentialStoreWarning = string.Empty;
            }
            catch (Exception)
            {
                CredentialStoreWarning = "The Plyry session ended, but the configured secure credential store could not clear its saved session.";
            }
        }

        private void AddPending(UnityWebRequest request, bool isDownload, string path, Action<PlyryApiResult> callback,
            Func<UnityWebRequest> requestFactory = null, bool authenticated = false, bool retried = false)
        {
            if (!string.IsNullOrEmpty(ConfigurationError))
            {
                request.Dispose();
                DeleteIfPresent(path);
                if (callback != null) callback(Failure(ConfigurationError));
                return;
            }
            if (disposed)
            {
                request.Abort();
                request.Dispose();
                DeleteIfPresent(path);
                if (callback != null) callback(Failure("The Plyry client has been disposed."));
                return;
            }

            UnityWebRequestAsyncOperation operation;
            try
            {
                request.timeout = Mathf.Max(1, isDownload ? DownloadTimeoutSeconds : RequestTimeoutSeconds);
                operation = request.SendWebRequest();
            }
            catch (Exception exception)
            {
                request.Dispose();
                DeleteIfPresent(path);
                if (callback != null) callback(Failure(exception.Message));
                return;
            }
            pending.Add(new Pending
            {
                Request = request,
                Operation = operation,
                Callback = callback,
                IsDownload = isDownload,
                TemporaryPath = path,
                RequestFactory = requestFactory,
                IsAuthenticated = authenticated,
                RetriedAfterRefresh = retried
            });
        }

        private void AddHeaders(UnityWebRequest request, bool post)
        {
            request.SetRequestHeader("Accept", "application/json");
            if (post && !string.IsNullOrEmpty(accessToken))
                request.SetRequestHeader("Authorization", "Bearer " + accessToken);
        }

        private PlyryApiResult FinishResponse(UnityWebRequest request)
        {
            var result = new PlyryApiResult
            {
                StatusCode = request.responseCode,
                Success = !HasRequestError(request)
            };
            var body = request.downloadHandler == null ? string.Empty : request.downloadHandler.text;
            result.Body = body;
            if (!string.IsNullOrEmpty(body))
            {
                try { result.Response = JsonUtility.FromJson<PlyryApiResponse>(body); }
                catch (Exception) { }
            }

            if (result.Response != null && !result.Response.ok)
                result.Success = false;
            if (!result.Success)
            {
                result.Error = result.Response != null && !string.IsNullOrEmpty(result.Response.message)
                    ? result.Response.message
                    : (string.IsNullOrEmpty(request.error) ? "Plyry request failed." : request.error);
            }
            return result;
        }

        private PlyryApiResult FinishDownload(Pending item)
        {
            var request = item.Request;
            var result = new PlyryApiResult
            {
                StatusCode = request.responseCode,
                Success = !HasRequestError(request)
            };
            if (!result.Success)
            {
                result.Error = string.IsNullOrEmpty(request.error) ? "The media download failed." : request.error;
                DeleteIfPresent(item.TemporaryPath);
                return result;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                var data = request.downloadHandler.data;
                if (data == null || data.Length == 0) throw new IOException("The downloaded media file was empty.");
                File.WriteAllBytes(item.TemporaryPath, data);
            }
            catch (Exception exception)
            {
                result.Success = false;
                result.Error = "Could not save the downloaded media: " + exception.Message;
                DeleteIfPresent(item.TemporaryPath);
                return result;
            }
#endif
            try
            {
                var mime = request.GetResponseHeader("Content-Type");
                if (!string.IsNullOrEmpty(mime) && mime.IndexOf(';') >= 0) mime = mime.Substring(0, mime.IndexOf(';'));
                var digest = HashFile(item.TemporaryPath);
                var expected = request.GetResponseHeader("X-Plyry-Media-SHA256");
                if (string.IsNullOrEmpty(expected) || !Regex.IsMatch(expected, "^[a-fA-F0-9]{64}$"))
                    throw new InvalidDataException("Plyry did not return a valid media checksum receipt.");
                if (!string.Equals(expected, digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded media checksum did not match Plyry's receipt.");

                result.Media = new PlyryDownloadedMedia
                {
                    TemporaryPath = item.TemporaryPath,
                    Extension = ResolveExtension(mime, request.GetResponseHeader("Content-Disposition")),
                    MimeType = string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime,
                    Sha256 = digest,
                    CacheToken = request.GetResponseHeader("X-Plyry-Cache-Token")
                };
            }
            catch (Exception exception)
            {
                result.Success = false;
                result.Error = exception.Message;
                DeleteIfPresent(item.TemporaryPath);
            }
            return result;
        }

        private string BuildUrl(string path)
        {
            return BaseUrl + "/" + (path ?? string.Empty).TrimStart('/');
        }

        private bool TryResolveMediaUrl(string mediaUrl, out string resolved)
        {
            resolved = null;
            if (string.IsNullOrEmpty(mediaUrl)) return false;
            Uri baseUri;
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out baseUri)) return false;
            Uri mediaUri;
            if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out mediaUri))
                mediaUri = new Uri(baseUri, mediaUrl);
            if (!string.Equals(mediaUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
                || mediaUri.Port != baseUri.Port
                || !string.IsNullOrEmpty(mediaUri.UserInfo)
                || !string.Equals(mediaUri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase))
                return false;
            resolved = mediaUri.AbsoluteUri;
            return true;
        }

        private static string HashFile(string path)
        {
            using (var algorithm = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                var hash = algorithm.ComputeHash(stream);
                var builder = new StringBuilder(hash.Length * 2);
                for (var i = 0; i < hash.Length; i++) builder.Append(hash[i].ToString("x2"));
                return builder.ToString();
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

        private static string ResolveExtension(string mime, string disposition)
        {
            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "image/png", ".png" }, { "image/jpeg", ".jpg" }, { "image/webp", ".webp" },
                { "image/bmp", ".bmp" }, { "video/mp4", ".mp4" }, { "video/webm", ".webm" },
                { "video/quicktime", ".mov" }, { "audio/mpeg", ".mp3" }, { "audio/wav", ".wav" },
                { "audio/x-wav", ".wav" }, { "audio/ogg", ".ogg" }, { "audio/mp4", ".m4a" },
                { "model/gltf-binary", ".glb" }, { "model/gltf+json", ".gltf" },
                { "model/obj", ".obj" }, { "model/vnd.usdz+zip", ".usdz" },
                { "application/json", ".json" }, { "application/zip", ".zip" }
            };
            if (!string.IsNullOrEmpty(mime) && known.ContainsKey(mime)) return known[mime];
            if (!string.IsNullOrEmpty(disposition))
            {
                var match = Regex.Match(disposition, @"filename\s*=\s*""?[^"";]*?(\.[a-z0-9]{1,8})""?(?:;|$)", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.ToLowerInvariant();
            }
            return ".bin";
        }

        private static string SafeFileName(string value)
        {
            var name = Path.GetFileName(value);
            if (string.IsNullOrEmpty(name)) return "reference.png";
            var safe = new StringBuilder(Mathf.Min(name.Length, 120));
            for (var i = 0; i < name.Length && safe.Length < 120; i++)
            {
                var character = name[i];
                safe.Append(char.IsLetterOrDigit(character) || character == '.' || character == '-' || character == '_'
                    ? character : '_');
            }
            var result = safe.ToString().Trim('.', '_');
            return string.IsNullOrEmpty(result) ? "reference.png" : result;
        }

        private static string NormalizeMimeType(string value)
        {
            var mime = (value ?? string.Empty).Trim();
            var separator = mime.IndexOf(';');
            if (separator >= 0) mime = mime.Substring(0, separator).Trim();
            return string.IsNullOrEmpty(mime) ? "image/png" : mime.ToLowerInvariant();
        }

        private static string Escape(string value)
        {
            if (value == null) return string.Empty;
            var builder = new StringBuilder(value.Length + 8);
            for (var i = 0; i < value.Length; i++)
            {
                var character = value[i];
                switch (character)
                {
                    case '\\': builder.Append("\\\\"); break;
                    case '"': builder.Append("\\\""); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 0x20) builder.Append("\\u00").Append(((int)character).ToString("x2"));
                        else builder.Append(character);
                        break;
                }
            }
            return builder.ToString();
        }

        private static PlyryApiResult Failure(string message)
        {
            return new PlyryApiResult { Success = false, Error = message };
        }

        private static void DeleteIfPresent(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }
    }
}






