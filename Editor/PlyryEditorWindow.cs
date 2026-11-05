using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Video;

namespace Plyry.Unity.Editor
{
    public sealed class PlyryEditorWindow : EditorWindow
    {
        private string apiBaseUrl = "https://plyry.com";
        private string linkedAppName = "Plyry Unity Editor";
        private string email = string.Empty;
        private string password = string.Empty;
        private string signupAnswer = string.Empty;
        private bool showTopup;
        private string topupAmount = "20.00";
        private string billingCountry = "GT";
        private string cardholderName = string.Empty;
        private string cardNumber = string.Empty;
        private string cardExpiryMonth = string.Empty;
        private string cardExpiryYear = string.Empty;
        private string cardSecurityCode = string.Empty;
        private string topupStatus = string.Empty;
        private string openedProviderActionUrl = string.Empty;
        private string pendingTopupKey = string.Empty;
        private int pendingTopupUserId;
        private int minimumTopupCents;
        private int maximumTopupCents;
        private bool topupPending;
        private bool topupBusy;
        private double nextTopupCheckAt;
        private const string PendingTopupKeyPreference = "Plyry.Unity.Editor.PendingTopupKey";
        private const string PendingTopupUserPreference = "Plyry.Unity.Editor.PendingTopupUser";
        private string prompt = string.Empty;
        private string outputFolder = "Assets/PlyryGenerated";
        private string selectedType = "image";
        private string selectedModelId = string.Empty;
        private string selectedReferenceMode = string.Empty;
        private string quality = "full";
        private string splatQuality = "low";
        private int quotedPriceCents = -1;
        private int quotedWalletCents;
        private int quoteRevision;
        private bool quoteHasSufficientFunds;
        private string quoteSignature = string.Empty;
        private string status = "Connect to Plyry to load available models.";
        private string lastImportedAsset = string.Empty;
        private string polygonCountText = string.Empty;

        private PlyryApiClient api;
        private PlyryAccount account;
        private PlyryCatalog catalog;
        private PlyryApiResponse signupChallenge;
        private PlyryJob activeJob;
        private bool busy;
        private bool pollInFlight;
        private bool downloading;
        private double nextPollAt;
        private PlyryModelOption activeModel;
        private PlyrySettingsSelection settings;
        private PlyryGenerationTarget target;
        private readonly List<UnityEngine.Object> referenceAssets = new List<UnityEngine.Object>();
        private Vector2 scroll;

        [MenuItem("Plyry/AI Media Generator")]
        public static void Open()
        {
            GetWindow<PlyryEditorWindow>("Plyry");
        }

        private void OnEnable()
        {
            if (api == null) api = new PlyryApiClient(apiBaseUrl);
            EditorApplication.update += Tick;
            LoadAccount();
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            ClearCardFields();
            if (api != null) api.Dispose();
            api = null;
        }

        private void Tick()
        {
            if (api == null) return;
            api.Tick();
            if (account != null && !api.HasSessionToken)
            {
                account = null;
                catalog = null;
                activeJob = null;
                busy = false;
                status = "Your Plyry app session ended. Sign in again to continue.";
            }
            if (activeJob != null && !pollInFlight && !downloading && EditorApplication.timeSinceStartup >= nextPollAt)
                PollJob();
            if (topupPending && !topupBusy && account != null && account.id == pendingTopupUserId
                && EditorApplication.timeSinceStartup >= nextTopupCheckAt) CheckTopupStatus();
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Plyry AI Media Generator", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(status, MessageType.Info);

            if (account == null)
            {
                DrawSignIn();
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawAccount();
            if (showTopup)
            {
                DrawTopup();
                EditorGUILayout.EndScrollView();
                return;
            }
            if (catalog == null || catalog.models == null || catalog.models.Length == 0)
            {
                EditorGUILayout.HelpBox("Loading available media models…", MessageType.None);
                EditorGUILayout.EndScrollView();
                return;
            }

            DrawGenerationForm();
            EditorGUILayout.EndScrollView();
        }

        private void DrawSignIn()
        {
            apiBaseUrl = EditorGUILayout.TextField("Plyry API", apiBaseUrl);
            linkedAppName = EditorGUILayout.TextField("Linked app name", linkedAppName);
            if (GUILayout.Button("Connect"))
            {
                if (!api.SetBaseUrl(apiBaseUrl)) status = api.ConfigurationError;
                else LoadAccount();
            }

            EditorGUILayout.Space();
            email = EditorGUILayout.TextField("Email", email);
            password = EditorGUILayout.PasswordField("Password", password);
            if (signupChallenge != null)
            {
                EditorGUILayout.HelpBox(signupChallenge.question ?? "Answer the signup check to create an account.", MessageType.Warning);
                signupAnswer = EditorGUILayout.TextField("Answer", signupAnswer);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Terms")) Application.OpenURL(api.BaseUrl + "/terms");
                if (GUILayout.Button("Privacy")) Application.OpenURL(api.BaseUrl + "/privacy");
                if (GUILayout.Button("Refunds")) Application.OpenURL(api.BaseUrl + "/refunds");
                if (GUILayout.Button("Content & Safety")) Application.OpenURL(api.BaseUrl + "/content-safety");
                EditorGUILayout.EndHorizontal();
            }

            using (new EditorGUI.DisabledScope(busy || api == null))
            {
                if (GUILayout.Button(signupChallenge == null ? "Continue" : "Create account"))
                    SignIn();
            }
            EditorGUILayout.HelpBox("Sign in or create an account. A new email receives a short signup check from Plyry.", MessageType.None);
        }

        private void DrawAccount()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.helpBox);
            EditorGUILayout.LabelField((account.email ?? "Plyry account") + "  •  " + FormatMoney(account.wallet_cents));
            if (GUILayout.Button("Add funds", GUILayout.Width(90f)))
            {
                showTopup = true;
                topupStatus = string.Empty;
                LoadPendingTopup();
                api.GetWallet(delegate(PlyryApiResult result)
                {
                    if (result.Success && result.Wallet != null)
                    {
                        minimumTopupCents = result.Wallet.minimum_topup_cents;
                        maximumTopupCents = result.Wallet.maximum_topup_cents;
                    }
                    else topupStatus = result.Error;
                    Repaint();
                });
            }
            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button("Refresh", GUILayout.Width(70f)))
                    LoadAccount();
                if (GUILayout.Button("Sign out", GUILayout.Width(70f)))
                {
                    busy = true;
                    api.Logout(delegate(PlyryApiResult result)
                    {
                        busy = false;
                        account = null;
                        catalog = null;
                        quoteRevision++;
                        quotedPriceCents = -1;
                        quoteSignature = string.Empty;
                        status = result.Success ? "Signed out." : "Signed out locally. The service could not confirm revocation: " + result.Error;
                        Repaint();
                    });
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawTopup()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Add funds to your Plyry wallet", EditorStyles.boldLabel);
            if (GUILayout.Button("← Back to generator", GUILayout.Width(150f)))
            {
                showTopup = false;
                ClearCardFields();
            }
            if (!string.IsNullOrEmpty(pendingTopupKey))
            {
                EditorGUILayout.HelpBox(pendingTopupUserId != account.id
                    ? "A previous top-up is linked to another Plyry account. Sign in to that account and check its status before starting another."
                    : "A top-up is still being checked. The plugin will not start another payment until Plyry confirms its result.", MessageType.Info);
                if (pendingTopupUserId == account.id && GUILayout.Button(topupBusy ? "Checking payment…" : "Check payment status"))
                    CheckTopupStatus();
                if (!string.IsNullOrEmpty(topupStatus)) EditorGUILayout.HelpBox(topupStatus, MessageType.None);
                return;
            }

            EditorGUILayout.HelpBox("Card details go directly to the payment processor for tokenization. Plyry receives only the payment-method token.", MessageType.None);
            topupAmount = EditorGUILayout.TextField("Amount (USD)", topupAmount);
            billingCountry = EditorGUILayout.TextField("Billing country", billingCountry);
            cardholderName = EditorGUILayout.TextField("Cardholder name", cardholderName);
            cardNumber = EditorGUILayout.TextField("Card number", cardNumber);
            EditorGUILayout.BeginHorizontal();
            cardExpiryMonth = EditorGUILayout.TextField("Expiry month", cardExpiryMonth, GUILayout.Width(145f));
            cardExpiryYear = EditorGUILayout.TextField("Year", cardExpiryYear, GUILayout.Width(115f));
            cardSecurityCode = EditorGUILayout.PasswordField("Security code", cardSecurityCode, GUILayout.Width(180f));
            EditorGUILayout.EndHorizontal();
            if (minimumTopupCents > 0 && maximumTopupCents > 0)
                EditorGUILayout.LabelField("Allowed amount: " + FormatMoney(minimumTopupCents) + "–" + FormatMoney(maximumTopupCents));
            using (new EditorGUI.DisabledScope(topupBusy || busy || minimumTopupCents <= 0))
                if (GUILayout.Button(topupBusy ? "Processing…" : "Add funds")) BeginTopup();
            if (!string.IsNullOrEmpty(topupStatus)) EditorGUILayout.HelpBox(topupStatus, MessageType.None);
        }

        private void BeginTopup()
        {
            decimal amount;
            if (!decimal.TryParse(topupAmount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
                || amount <= 0 || amount > int.MaxValue / 100m || amount * 100m != decimal.Truncate(amount * 100m))
            {
                topupStatus = "Enter a valid amount with up to two decimal places.";
                return;
            }
            var amountCents = decimal.ToInt32(amount * 100m);
            if (amountCents < minimumTopupCents || amountCents > maximumTopupCents)
            {
                topupStatus = "Enter an amount between " + FormatMoney(minimumTopupCents) + " and " + FormatMoney(maximumTopupCents) + ".";
                return;
            }
            var country = (billingCountry ?? string.Empty).Trim().ToUpperInvariant();
            if (country.Length != 2 || country[0] < 'A' || country[0] > 'Z' || country[1] < 'A' || country[1] > 'Z')
            {
                topupStatus = "Enter a two-letter billing country code, such as GT or US.";
                return;
            }

            topupBusy = true;
            topupStatus = "Loading the payment configuration…";
            api.GetPaymentConfig(delegate(PlyryApiResult configResult)
            {
                if (!configResult.Success || configResult.PaymentConfig == null)
                {
                    FinishTopupError(configResult.Error);
                    Repaint();
                    return;
                }
                var config = configResult.PaymentConfig;
                if (!string.Equals(config.driver, "onvo", StringComparison.OrdinalIgnoreCase))
                {
                    FinishTopupError("This payment provider is not supported by the Unity plugin yet.");
                    Repaint();
                    return;
                }

                int month, year;
                var number = DigitsOnly(cardNumber);
                var holder = (cardholderName ?? string.Empty).Trim();
                var securityCode = (cardSecurityCode ?? string.Empty).Trim();
                if (holder.Length == 0 || number.Length < 12 || number.Length > 19
                    || !int.TryParse(cardExpiryMonth, out month) || month < 1 || month > 12
                    || !int.TryParse(cardExpiryYear, out year)
                    || ((cardExpiryYear ?? string.Empty).Length != 2 && (cardExpiryYear ?? string.Empty).Length != 4)
                    || !IsValidSecurityCode(securityCode))
                {
                    topupBusy = false;
                    topupStatus = "Enter the cardholder name, valid card number, expiry, and security code.";
                    Repaint();
                    return;
                }
                if (year < 100) year += 2000;
                if (year < DateTime.UtcNow.Year || year > DateTime.UtcNow.Year + 25
                    || (year == DateTime.UtcNow.Year && month < DateTime.UtcNow.Month))
                {
                    topupBusy = false;
                    topupStatus = "The card expiry date has passed.";
                    Repaint();
                    return;
                }

                topupStatus = "Tokenizing the card with the payment processor…";
                api.CreateOnvoPaymentMethod(config, account.email, holder, number, month, year, securityCode,
                    country, delegate(PlyryApiResult tokenResult)
                    {
                        if (!tokenResult.Success || string.IsNullOrEmpty(tokenResult.PaymentMethodId))
                            FinishTopupError(tokenResult.Error);
                        else SubmitTopup(amountCents, country, tokenResult.PaymentMethodId, config.return_url);
                        Repaint();
                    });
                ClearCardFields();
            });
        }

        private void SubmitTopup(int amountCents, string country, string paymentMethodId, string returnUrl)
        {
            pendingTopupKey = Guid.NewGuid().ToString("D").ToLowerInvariant();
            pendingTopupUserId = account.id;
            EditorPrefs.SetString(PendingTopupPreferenceName(PendingTopupKeyPreference), pendingTopupKey);
            EditorPrefs.SetInt(PendingTopupPreferenceName(PendingTopupUserPreference), pendingTopupUserId);
            topupPending = true;
            topupStatus = "Submitting the top-up. Do not start another payment while its status is being confirmed.";
            api.CreateTopup(amountCents, paymentMethodId, country, returnUrl, pendingTopupKey,
                delegate(PlyryApiResult result)
                {
                    if (!result.Success)
                    {
                        topupBusy = false;
                        nextTopupCheckAt = EditorApplication.timeSinceStartup + 2d;
                        topupStatus = "Checking the same top-up request with Plyry. Do not submit another payment. " + result.Error;
                    }
                    else HandleTopupResponse(result);
                    Repaint();
                });
        }

        private void CheckTopupStatus()
        {
            if (topupBusy || string.IsNullOrEmpty(pendingTopupKey) || api == null) return;
            topupBusy = true;
            topupStatus = "Checking payment status…";
            api.GetTopupStatus(pendingTopupKey, delegate(PlyryApiResult result)
            {
                topupBusy = false;
                if (!result.Success)
                {
                    if (result.StatusCode == 404 && result.Response != null
                        && result.Response.error == "topup_request_not_found")
                    {
                        ClearPendingTopup();
                        topupStatus = "Plyry did not receive this top-up request. You can try again.";
                    }
                    else
                    {
                        topupStatus = "Payment status is not confirmed yet. The plugin will check again. " + result.Error;
                        nextTopupCheckAt = EditorApplication.timeSinceStartup + 10d;
                    }
                }
                else HandleTopupResponse(result);
                Repaint();
            });
        }

        private void HandleTopupResponse(PlyryApiResult result)
        {
            topupBusy = false;
            var response = result.Response;
            var payment = response == null ? null : response.payment;
            var paymentStatus = (payment == null ? string.Empty : payment.status ?? string.Empty).ToLowerInvariant();
            var requestStatus = response == null ? string.Empty : (response.request_status ?? string.Empty).ToLowerInvariant();
            if (requestStatus == "complete" || paymentStatus == "completed")
            {
                ClearPendingTopup();
                topupStatus = "Top-up complete. Refreshing your wallet balance…";
                LoadAccount();
                return;
            }
            if (requestStatus == "failed" || paymentStatus == "failed" || paymentStatus == "refunded" || paymentStatus == "chargeback")
            {
                ClearPendingTopup();
                topupStatus = "The payment did not complete. You can try again.";
                return;
            }
            topupPending = true;
            nextTopupCheckAt = EditorApplication.timeSinceStartup + 5d;
            topupStatus = payment != null && !string.IsNullOrEmpty(payment.status)
                ? "Payment status: " + payment.status + ". Return here after completing any bank verification."
                : "The payment is still being confirmed. Do not start another request yet.";
            if (payment != null && payment.requires_action && !string.IsNullOrEmpty(payment.action_url))
            {
                if (string.Equals(openedProviderActionUrl, payment.action_url, StringComparison.Ordinal))
                    topupStatus += " Complete verification in your browser, then return here.";
                else if (OpenProviderAction(payment.action_url))
                {
                    openedProviderActionUrl = payment.action_url;
                    topupStatus += " Complete verification in your browser, then return here.";
                }
                else topupStatus += " The payment provider returned an invalid verification link.";
            }
        }

        private void LoadPendingTopup()
        {
            if (account == null) return;
            if (string.IsNullOrEmpty(pendingTopupKey))
            {
                pendingTopupKey = EditorPrefs.GetString(PendingTopupPreferenceName(PendingTopupKeyPreference), string.Empty);
                pendingTopupUserId = EditorPrefs.GetInt(PendingTopupPreferenceName(PendingTopupUserPreference), 0);
            }
            topupPending = !string.IsNullOrEmpty(pendingTopupKey);
            if (topupPending && pendingTopupUserId == account.id)
                nextTopupCheckAt = EditorApplication.timeSinceStartup + 1d;
        }

        private void ClearPendingTopup()
        {
            topupPending = false;
            pendingTopupKey = string.Empty;
            pendingTopupUserId = 0;
            openedProviderActionUrl = string.Empty;
            EditorPrefs.DeleteKey(PendingTopupPreferenceName(PendingTopupKeyPreference));
            EditorPrefs.DeleteKey(PendingTopupPreferenceName(PendingTopupUserPreference));
        }

        private void FinishTopupError(string message)
        {
            topupBusy = false;
            topupStatus = string.IsNullOrEmpty(message) ? "The top-up could not be started." : message;
        }

        private void ClearCardFields()
        {
            cardNumber = string.Empty;
            cardExpiryMonth = string.Empty;
            cardExpiryYear = string.Empty;
            cardSecurityCode = string.Empty;
        }

        private static string DigitsOnly(string value)
        {
            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < (value ?? string.Empty).Length; i++)
                if (char.IsDigit(value[i])) builder.Append(value[i]);
            return builder.ToString();
        }

        private static bool IsValidSecurityCode(string value)
        {
            if (value == null || (value.Length != 3 && value.Length != 4)) return false;
            for (var i = 0; i < value.Length; i++) if (!char.IsDigit(value[i])) return false;
            return true;
        }

        private static bool OpenProviderAction(string actionUrl)
        {
            Uri uri;
            if (!Uri.TryCreate(actionUrl, UriKind.Absolute, out uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !(string.Equals(uri.Host, "onvopay.com", StringComparison.OrdinalIgnoreCase)
                    || uri.Host.EndsWith(".onvopay.com", StringComparison.OrdinalIgnoreCase))) return false;
            Application.OpenURL(uri.AbsoluteUri);
            return true;
        }

        private string PendingTopupPreferenceName(string name)
        {
            var service = api == null ? string.Empty : api.BaseUrl;
            var suffix = Convert.ToBase64String(Encoding.UTF8.GetBytes(service))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return name + "." + suffix;
        }

        private void DrawGenerationForm()
        {
            var entries = catalog.models;
            var typeLabels = new string[entries.Length];
            var typeIndex = 0;
            for (var i = 0; i < entries.Length; i++)
            {
                typeLabels[i] = LabelForType(entries[i].type);
                if (entries[i].type == selectedType) typeIndex = i;
            }
            var nextType = entries[EditorGUILayout.Popup("Media kind", typeIndex, typeLabels)].type;
            if (nextType != selectedType)
            {
                selectedType = nextType;
                selectedModelId = string.Empty;
                SelectDefaultModel();
            }

            var entry = catalog.FindType(selectedType);
            if (entry == null || entry.options == null || entry.options.Length == 0)
            {
                EditorGUILayout.HelpBox("No models are available for this media kind.", MessageType.Warning);
                return;
            }

            var modelLabels = new string[entry.options.Length];
            var modelIndex = 0;
            for (var i = 0; i < entry.options.Length; i++)
            {
                modelLabels[i] = entry.options[i].label ?? entry.options[i].id;
                if (entry.options[i].id == selectedModelId) modelIndex = i;
            }
            var nextModel = entry.options[EditorGUILayout.Popup("Model", modelIndex, modelLabels)];
            if (activeModel == null || nextModel.id != selectedModelId)
            {
                selectedModelId = nextModel.id;
                SelectModel(nextModel);
            }
            activeModel = nextModel;

            DrawReferenceMode();
            var nextQuality = EditorGUILayout.Popup("Quality", quality == "test" ? 1 : 0,
                new[] { "Full quality", "Test quality" }) == 1 ? "test" : "full";
            if (nextQuality != quality)
            {
                quality = nextQuality;
                settings = PlyrySettingsSelection.Defaults(SelectedOptions(), quality == "test");
            }
            if (selectedType == "splat")
            {
                var splatIndex = splatQuality == "high" ? 2 : (splatQuality == "medium" ? 1 : 0);
                var selectedSplat = EditorGUILayout.Popup("Splat quality", splatIndex, new[] { "Low", "Medium", "High" });
                splatQuality = selectedSplat == 2 ? "high" : (selectedSplat == 1 ? "medium" : "low");
            }
            if (settings == null) settings = PlyrySettingsSelection.Defaults(SelectedOptions(), quality == "test");
            DrawSettings();

            target = (PlyryGenerationTarget)EditorGUILayout.ObjectField("Prompt target", target, typeof(PlyryGenerationTarget), true);
            if (target != null)
                EditorGUILayout.HelpBox("This target adds its game-authored prompt instructions. Editor results are imported for you to assign.", MessageType.None);
            outputFolder = EditorGUILayout.TextField("Project output folder", outputFolder);
            prompt = EditorGUILayout.TextArea(prompt, GUILayout.MinHeight(72f));

            DrawReferenceAssets();
            DrawQuoteControls();

            if (!string.IsNullOrEmpty(lastImportedAsset))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Last imported", lastImportedAsset);
                if (GUILayout.Button("Select", GUILayout.Width(60f)))
                    Selection.activeObject = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(lastImportedAsset);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawReferenceMode()
        {
            if (activeModel == null) return;
            var modes = activeModel.references ?? new PlyryReferenceMode[0];
            if (modes.Length == 0)
            {
                selectedReferenceMode = string.Empty;
                return;
            }
            var labels = new List<string>();
            var values = new List<string>();
            if (!activeModel.requires_reference)
            {
                labels.Add("None");
                values.Add(string.Empty);
            }
            for (var i = 0; i < modes.Length; i++)
            {
                labels.Add(modes[i].mode);
                values.Add(modes[i].mode);
            }
            var index = Math.Max(0, values.IndexOf(selectedReferenceMode));
            var next = values[EditorGUILayout.Popup("Reference mode", index, labels.ToArray())];
            if (next != selectedReferenceMode)
            {
                selectedReferenceMode = next;
                settings = PlyrySettingsSelection.Defaults(SelectedOptions(), quality == "test");
            }
        }

        private void DrawSettings()
        {
            var options = SelectedOptions();
            if (options == null) return;
            if (options.duration != null && options.duration.values != null && options.duration.values.Length > 0)
            {
                var values = options.duration.values;
                var labels = Array.ConvertAll(values, value => value + " sec");
                settings.duration = values[EditorGUILayout.Popup("Duration", Math.Max(0, Array.IndexOf(values, settings.duration)), labels)];
            }
            if (options.aspect != null && options.aspect.emit && options.aspect.values != null && options.aspect.values.Length > 0)
            {
                var choices = options.aspect.values;
                var labels = Array.ConvertAll(choices, choice => choice.label ?? choice.value);
                var index = FindChoice(choices, settings.aspect);
                settings.aspect = choices[EditorGUILayout.Popup("Aspect ratio", index, labels)].value;
            }
            if (options.resolution != null && options.resolution.values != null && options.resolution.values.Length > 0)
            {
                var choices = options.resolution.values;
                var labels = Array.ConvertAll(choices, choice => choice.label ?? choice.value);
                var index = FindChoice(choices, settings.resolution);
                var label = string.IsNullOrEmpty(options.resolution.label) ? "Resolution" : options.resolution.label;
                settings.resolution = choices[EditorGUILayout.Popup(label, index, labels)].value;
            }
            if (options.audio != null)
                settings.audio = EditorGUILayout.Toggle("Generate audio", settings.audio);
            if (options.polygon_count != null)
            {
                polygonCountText = EditorGUILayout.TextField("Polygon count", settings.polygonCount.ToString());
                int count;
                if (int.TryParse(polygonCountText, out count))
                    settings.polygonCount = Mathf.Clamp(count, options.polygon_count.min, options.polygon_count.max);
            }
        }

        private void DrawReferenceAssets()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Reference files", EditorStyles.boldLabel);
            var maximum = MaximumReferenceCount();
            if (maximum == 0 && activeModel != null && !activeModel.requires_reference)
                EditorGUILayout.HelpBox("Choose a reference mode to attach files. Stored references are ignored in None mode.", MessageType.None);
            for (var i = referenceAssets.Count - 1; i >= 0; i--)
            {
                EditorGUILayout.BeginHorizontal();
                referenceAssets[i] = EditorGUILayout.ObjectField(ReferenceLabelForSlot(i + 1), referenceAssets[i], typeof(UnityEngine.Object), false);
                if (GUILayout.Button("Remove", GUILayout.Width(70f))) referenceAssets.RemoveAt(i);
                EditorGUILayout.EndHorizontal();
            }
            using (new EditorGUI.DisabledScope(maximum <= 0 || referenceAssets.Count >= maximum))
            {
                if (GUILayout.Button("Add reference file"))
                {
                    var path = EditorUtility.OpenFilePanel("Choose a Plyry reference", Application.dataPath, string.Empty);
                    if (!string.IsNullOrEmpty(path))
                    {
                        var projectRoot = Directory.GetParent(Application.dataPath).FullName;
                        if (path.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            var assetPath = path.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
                            if (asset != null) referenceAssets.Add(asset);
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("Reference file", "Import the reference into this Unity project first, then choose it from the Project window.", "OK");
                        }
                    }
                }
            }
        }

        private void LoadAccount()
        {
            if (api == null) api = new PlyryApiClient(apiBaseUrl);
            if (!api.SetBaseUrl(apiBaseUrl))
            {
                status = api.ConfigurationError;
                Repaint();
                return;
            }
            status = "Connecting to Plyry…";
            api.GetSession(delegate(PlyryApiResult result)
            {
                if (!result.Success || result.Response == null)
                    status = result.Error;
                else
                {
                    account = result.Response.user;
                    quoteRevision++;
                    quotedPriceCents = -1;
                    quoteSignature = string.Empty;
                    status = account == null ? "Sign in to generate media." : "Loading wallet balance…";
                    if (account != null)
                    {
                        api.GetWallet(delegate(PlyryApiResult walletResult)
                        {
                            if (!walletResult.Success || walletResult.Wallet == null) status = walletResult.Error;
                            else
                            {
                                account.wallet_cents = walletResult.Wallet.wallet_cents;
                                minimumTopupCents = walletResult.Wallet.minimum_topup_cents;
                                maximumTopupCents = walletResult.Wallet.maximum_topup_cents;
                                LoadPendingTopup();
                                LoadCatalog();
                            }
                            Repaint();
                        });
                    }
                }
                Repaint();
            });
        }

        private void LoadCatalog()
        {
            api.GetCatalog(delegate(PlyryApiResult result)
            {
                if (!result.Success) status = result.Error;
                else
                {
                    catalog = result.Catalog;
                    SelectDefaultModel();
                    status = "Ready";
                }
                Repaint();
            });
        }

        private void SignIn()
        {
            if (string.IsNullOrWhiteSpace(linkedAppName) || linkedAppName.Trim().Length > 120)
            {
                status = "Enter a linked app name up to 120 characters.";
                Repaint();
                return;
            }
            api.LinkedAppName = linkedAppName.Trim();
            if (!api.SetBaseUrl(apiBaseUrl))
            {
                status = api.ConfigurationError;
                Repaint();
                return;
            }
            busy = true;
            status = "Signing in…";
            api.SignIn(email, password,
                signupChallenge == null ? string.Empty : signupChallenge.signup_challenge,
                signupAnswer,
                delegate(PlyryApiResult result)
                {
                    busy = false;
                    signupChallenge = result.Response != null && !string.IsNullOrEmpty(result.Response.signup_challenge)
                        && !result.Authenticated ? result.Response : null;
                    if (result.Success && !result.Authenticated && signupChallenge != null)
                    {
                        status = "Answer the signup check to create your account.";
                        Repaint();
                        return;
                    }
                    if (!result.Success || result.Response == null)
                        status = result.Error;
                    else
                    {
                        account = result.Response.user;
                        quoteRevision++;
                        quotedPriceCents = -1;
                        quoteSignature = string.Empty;
                        password = string.Empty;
                        signupAnswer = string.Empty;
                        status = "Loading your Plyry account…";
                        LoadAccount();
                    }
                    Repaint();
                });
        }

        private void SelectDefaultModel()
        {
            var entry = catalog == null ? null : catalog.FindType(selectedType);
            if (entry == null || entry.options == null || entry.options.Length == 0)
            {
                if (catalog != null && catalog.models != null && catalog.models.Length > 0)
                {
                    selectedType = catalog.models[0].type;
                    entry = catalog.models[0];
                }
            }
            if (entry == null || entry.options == null || entry.options.Length == 0) return;
            var model = FindModel(entry.options, entry.default_model);
            if (model == null) model = entry.options[0];
            SelectModel(model);
        }

        private void SelectModel(PlyryModelOption model)
        {
            activeModel = model;
            selectedModelId = model.id;
            var modes = model.references ?? new PlyryReferenceMode[0];
            selectedReferenceMode = model.requires_reference && modes.Length > 0 ? modes[0].mode : string.Empty;
            settings = PlyrySettingsSelection.Defaults(SelectedOptions(), quality == "test");
        }

        private PlyryGenerationOptions SelectedOptions()
        {
            if (activeModel == null) return null;
            var modes = activeModel.references ?? new PlyryReferenceMode[0];
            for (var i = 0; i < modes.Length; i++)
                if (modes[i].mode == selectedReferenceMode && modes[i].generation_options != null)
                    return modes[i].generation_options;
            return activeModel.generation_options;
        }

        private void DrawQuoteControls()
        {
            var currentSignature = CurrentQuoteSignature();
            if (quoteSignature != currentSignature)
            {
                quoteSignature = string.Empty;
                quotedPriceCents = -1;
            }

            var needsReference = activeModel.requires_reference || !string.IsNullOrEmpty(selectedReferenceMode);
            var canSubmit = (!activeModel.requires_reference || !string.IsNullOrEmpty(selectedReferenceMode))
                && (!needsReference || referenceAssets.Count > 0)
                && (!needsReference || referenceAssets.Count <= MaximumReferenceCount());
            using (new EditorGUI.DisabledScope(busy || !canSubmit))
            {
                if (quotedPriceCents < 0 || quoteSignature != currentSignature)
                {
                    if (GUILayout.Button(busy ? "Checking price…" : "Check current Plyry price"))
                        RequestQuote();
                    return;
                }

                EditorGUILayout.LabelField("Plyry charge", FormatMoney(quotedPriceCents));
                EditorGUILayout.LabelField("Wallet balance", FormatMoney(quotedWalletCents));
                if (!quoteHasSufficientFunds)
                {
                    if (GUILayout.Button("Add funds to generate"))
                    {
                        showTopup = true;
                        api.GetWallet(delegate(PlyryApiResult walletResult)
                        {
                            if (walletResult.Success && walletResult.Wallet != null)
                            {
                                minimumTopupCents = walletResult.Wallet.minimum_topup_cents;
                                maximumTopupCents = walletResult.Wallet.maximum_topup_cents;
                            }
                            else topupStatus = walletResult.Error;
                            Repaint();
                        });
                    }
                }
                else if (GUILayout.Button(busy ? "Working…" : "Generate for " + FormatMoney(quotedPriceCents), GUILayout.Height(34f)))
                {
                    Submit();
                }
            }
        }

        private void RequestQuote()
        {
            var requestedSignature = CurrentQuoteSignature();
            var requestedRevision = quoteRevision;
            busy = true;
            status = "Getting the current Plyry price…";
            api.Quote(BuildGenerationRequest(false), delegate(PlyryApiResult result)
            {
                if (requestedRevision != quoteRevision)
                {
                    busy = false;
                    Repaint();
                    return;
                }
                busy = false;
                if (result.Success && result.Response != null)
                {
                    quotedPriceCents = result.Response.price_cents;
                    quotedWalletCents = account == null ? 0 : account.wallet_cents;
                    quoteHasSufficientFunds = result.Response.has_sufficient_funds;
                    quoteSignature = requestedSignature;
                    status = "Price quote ready.";
                }
                else status = result.Error;
                Repaint();
            });
        }

        private string CurrentQuoteSignature()
        {
            if (activeModel == null) return string.Empty;
            var options = SelectedOptions();
            var queue = settings == null ? string.Empty : settings.ToQueueOptionsJson(options);
            return selectedType + "|" + selectedModelId + "|" + selectedReferenceMode + "|"
                + quality + "|" + splatQuality + "|" + (string.IsNullOrEmpty(selectedReferenceMode) ? 0 : referenceAssets.Count) + "|" + queue;
        }

        private PlyryGenerationRequest BuildGenerationRequest(bool includeReferences)
        {
            var refs = new List<PlyryReference>();
            if (includeReferences && !string.IsNullOrEmpty(selectedReferenceMode))
            {
                var maximum = MaximumReferenceCount();
                for (var i = 0; i < referenceAssets.Count && refs.Count < maximum; i++)
                {
                    var reference = ReadReference(referenceAssets[i], refs.Count + 1);
                    if (reference != null) refs.Add(reference);
                }
            }
            return new PlyryGenerationRequest
            {
                Type = selectedType,
                Model = activeModel.id,
                Prompt = target == null ? prompt : target.ComposePrompt(prompt),
                Quality = quality,
                SplatQuality = splatQuality,
                ReferenceMode = selectedReferenceMode,
                ModelOption = activeModel,
                Settings = settings,
                SettingsOptions = SelectedOptions(),
                References = refs.ToArray()
            };
        }

        private void Submit()
        {
            if (activeModel == null || string.IsNullOrWhiteSpace(prompt)) return;
            var request = BuildGenerationRequest(true);
            var needsReference = activeModel.requires_reference || !string.IsNullOrEmpty(selectedReferenceMode);
            if ((activeModel.requires_reference && string.IsNullOrEmpty(selectedReferenceMode))
                || (needsReference && request.References.Length == 0)
                || referenceAssets.Count > MaximumReferenceCount())
            {
                status = "Choose a reference mode and add a supported reference file.";
                return;
            }

            busy = true;
            status = "Submitting generation…";
            api.Generate(request, delegate(PlyryApiResult result)
            {
                if (!result.Success || result.Response == null || result.Response.job == null)
                {
                    busy = false;
                    status = result.Error;
                    Repaint();
                    return;
                }
                activeJob = result.Response.job;
                nextPollAt = EditorApplication.timeSinceStartup + 3.0;
                status = "Generation queued.";
                Repaint();
            });
        }

        private static PlyryReference ReadReference(UnityEngine.Object asset, int slot)
        {
            if (asset == null) return null;
            var texture = asset as Texture2D;
            if (texture != null)
            {
                try
                {
                    return new PlyryReference(asset.name + ".png", "image/png", slot,
                        PlyryImageUtility.EncodePng(texture, 2048));
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("Could not read Plyry reference texture: " + exception.Message);
                    return null;
                }
            }

            var path = AssetDatabase.GetAssetPath(asset);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var extension = Path.GetExtension(path).ToLowerInvariant();
            var mime = MimeForExtension(extension);
            if (string.IsNullOrEmpty(mime)) return null;
            try { return new PlyryReference(Path.GetFileName(path), mime, slot, File.ReadAllBytes(path)); }
            catch (Exception) { return null; }
        }

        private void PollJob()
        {
            pollInFlight = true;
            api.GetJob(activeJob.hash, delegate(PlyryApiResult result)
            {
                pollInFlight = false;
                nextPollAt = EditorApplication.timeSinceStartup + 3.0;
                if (!result.Success || result.Response == null || result.Response.job == null)
                {
                    status = string.IsNullOrEmpty(result.Error) ? "Waiting for Plyry…" : result.Error;
                    Repaint();
                    return;
                }

                activeJob = result.Response.job;
                var jobStatus = (activeJob.status ?? string.Empty).ToLowerInvariant();
                if (jobStatus == "failed" || jobStatus == "aborted")
                {
                    busy = false;
                    activeJob = null;
                    status = string.IsNullOrEmpty(result.Response.job.error)
                        ? "Plyry could not complete this generation."
                        : result.Response.job.error;
                    Repaint();
                    return;
                }
                if (jobStatus == "completed" && activeJob.media_available && !string.IsNullOrEmpty(activeJob.media_url))
                    DownloadResult(activeJob);
                else
                {
                    status = "Generation " + (string.IsNullOrEmpty(jobStatus) ? "in progress" : jobStatus) + "…";
                    Repaint();
                }
            });
        }

        private void DownloadResult(PlyryJob job)
        {
            downloading = true;
            status = "Downloading and checking the generated media…";
            api.DownloadMedia(job.media_url, delegate(PlyryApiResult result)
            {
                if (!result.Success || result.Media == null)
                {
                    FinishWithError(result.Error);
                    return;
                }
                string assetPath;
                try { assetPath = ImportFile(job.type, job.hash, result.Media); }
                catch (Exception exception)
                {
                    FinishWithError("The file downloaded but could not be imported: " + exception.Message);
                    return;
                }

                lastImportedAsset = assetPath;
                if (!string.IsNullOrEmpty(job.scene_url))
                    DownloadScene(job, assetPath, result.Media.CacheToken, result.Media.Sha256);
                else FinishDelivery(job, result.Media.CacheToken, result.Media.Sha256);
                Repaint();
            });
        }

        private void DownloadScene(PlyryJob job, string assetPath, string cacheToken, string sha256)
        {
            api.DownloadMedia(job.scene_url, delegate(PlyryApiResult result)
            {
                var sceneSaved = false;
                if (result.Success && result.Media != null)
                {
                    try
                    {
                        ImportFile(job.type, job.hash + "-scene", result.Media);
                        sceneSaved = true;
                    }
                    catch (Exception exception) { Debug.LogWarning("Could not import the splat scene file: " + exception.Message); }
                }
                FinishDelivery(job, sceneSaved ? cacheToken : string.Empty, sha256);
                if (!sceneSaved)
                    Debug.LogWarning("Plyry kept the server copy because the splat scene file was not imported: " + result.Error);
                Repaint();
            });
        }

        private void FinishDelivery(PlyryJob job, string cacheToken, string sha256)
        {
            downloading = false;
            busy = false;
            activeJob = null;
            status = "Saved in " + lastImportedAsset;
            if (!string.IsNullOrEmpty(cacheToken))
            {
                api.AcknowledgeCache(job.hash, cacheToken, sha256, delegate(PlyryApiResult result)
                {
                    if (!result.Success)
                        status = "Asset imported. Plyry kept its server copy because delivery confirmation did not finish.";
                    Repaint();
                });
            }
            LoadAccount();
            Repaint();
        }

        private string ImportFile(string mediaType, string jobHash, PlyryDownloadedMedia media)
        {
            var normalizedOutputFolder = (outputFolder ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            if (normalizedOutputFolder != "Assets" && !normalizedOutputFolder.StartsWith("Assets/", StringComparison.Ordinal))
                throw new InvalidOperationException("Choose a project folder that starts with Assets.");

            var typeFolder = mediaType.Replace("/", "_");
            var relativeDirectory = normalizedOutputFolder + "/" + typeFolder;
            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var destinationDirectory = Path.GetFullPath(Path.Combine(projectRoot, relativeDirectory.Replace('/', Path.DirectorySeparatorChar)));
            var assetsRoot = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!destinationDirectory.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The output folder must stay inside the project's Assets folder.");
            Directory.CreateDirectory(destinationDirectory);
            var safeHash = string.IsNullOrEmpty(jobHash) ? Guid.NewGuid().ToString("N") : jobHash.Replace("/", "_").Replace("\\", "_");
            var filename = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + safeHash + media.Extension;
            var destination = Path.Combine(destinationDirectory, filename);
            File.Copy(media.TemporaryPath, destination, true);
            File.Delete(media.TemporaryPath);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var assetPath = relativeDirectory + "/" + filename;
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            return assetPath;
        }

        private void FinishWithError(string message)
        {
            downloading = false;
            busy = false;
            activeJob = null;
            status = string.IsNullOrEmpty(message) ? "Plyry could not deliver this media." : message;
            Repaint();
        }

        private static PlyryModelOption FindModel(PlyryModelOption[] models, string id)
        {
            if (models == null) return null;
            for (var i = 0; i < models.Length; i++) if (models[i].id == id) return models[i];
            return null;
        }

        private static int FindChoice(PlyryChoice[] choices, string value)
        {
            for (var i = 0; i < choices.Length; i++) if (choices[i].value == value) return i;
            return 0;
        }

        private static PlyryReferenceMode FindReferenceMode(PlyryModelOption model, string modeName)
        {
            if (model == null || model.references == null) return null;
            for (var i = 0; i < model.references.Length; i++)
                if (string.Equals(model.references[i].mode, modeName, StringComparison.OrdinalIgnoreCase))
                    return model.references[i];
            return null;
        }

        private int MaximumReferenceCount()
        {
            var mode = FindReferenceMode(activeModel, selectedReferenceMode);
            return mode == null ? 0 : Mathf.Clamp(mode.max_files <= 0 ? 1 : mode.max_files, 1, 7);
        }

        private string ReferenceLabelForSlot(int slot)
        {
            var mode = FindReferenceMode(activeModel, selectedReferenceMode);
            if (mode != null && mode.fields != null && slot > 0 && slot <= mode.fields.Length
                && mode.fields[slot - 1] != null && !string.IsNullOrEmpty(mode.fields[slot - 1].label))
                return mode.fields[slot - 1].label;
            return "Reference " + slot;
        }

        private static string MimeForExtension(string extension)
        {
            switch (extension)
            {
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                case ".mp4": return "video/mp4";
                case ".webm": return "video/webm";
                case ".mov": return "video/quicktime";
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                case ".m4a": return "audio/mp4";
                case ".ogg": return "audio/ogg";
                default: return string.Empty;
            }
        }

        private static string FormatMoney(int cents)
        {
            return "$" + (cents / 100).ToString(CultureInfo.InvariantCulture) + "."
                + (cents % 100).ToString("D2", CultureInfo.InvariantCulture);
        }

        private static string LabelForType(string type)
        {
            if (type == "3d") return "3D";
            return string.IsNullOrEmpty(type) ? "Media" : char.ToUpperInvariant(type[0]) + type.Substring(1);
        }
    }
}

