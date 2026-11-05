using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Plyry.Unity
{
    /// <summary>
    /// Optional in-game account and generation panel. Uses Unity's built-in IMGUI so it has
    /// no UI package dependency; assign a GUISkin or theme asset to match the game.
    /// </summary>
    public sealed class PlyryRuntimePanel : MonoBehaviour
    {
        [SerializeField] private PlyryClient client;
        [SerializeField] private PlyryGenerationTarget target;
        [SerializeField] private PlyryTheme theme;
        [SerializeField] private bool createDefaultUi = true;
        [SerializeField] private bool showOnStart = true;

        private readonly List<PlyryReference> references = new List<PlyryReference>();
        private Vector2 scroll;
        private string prompt = string.Empty;
        private string email = string.Empty;
        private string password = string.Empty;
        private string challengeAnswer = string.Empty;
        private PlyryApiResponse signupChallenge;
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
        private string polygonCountText;
        private bool modelSettingsValid = true;
        private PlyrySettingsSelection settings;
        private PlyryModelOption activeModel;
        private bool clientEventsAttached;
        private bool showTopup;
        private bool topupBusy;
        private bool topupPending;
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
        private float nextTopupCheckAt;
        private const string PendingTopupKeyPreference = "Plyry.Unity.PendingTopupKey";
        private const string PendingTopupUserPreference = "Plyry.Unity.PendingTopupUser";

        public bool Visible { get; private set; }
        public bool CreatesDefaultUi { get { return createDefaultUi; } }
        public PlyryGenerationTarget Target { get { return target; } set { target = value; } }
        public int MaximumReferenceCount { get { return GetMaximumReferenceCount(); } }
        public PlyryReferenceMode CurrentReferenceMode { get { return GetSelectedReferenceMode(); } }
        public PlyryReferenceField[] CurrentReferenceFields
        {
            get
            {
                var mode = GetSelectedReferenceMode();
                return mode == null || mode.fields == null ? new PlyryReferenceField[0] : mode.fields;
            }
        }
        public int NextReferenceSlot { get { return FindNextReferenceSlot(); } }
        public event Action ReferenceSelectionRequested;

        private void Awake()
        {
            if (client == null) client = GetComponent<PlyryClient>();
            Visible = showOnStart;
        }

        private void OnEnable()
        {
            AttachClient();
        }

        private void OnDisable()
        {
            ClearCardFields();
            DetachClient();
        }

        public void Initialize(PlyryClient value)
        {
            client = value;
            AttachClient();
        }

        public void Show()
        {
            Visible = true;
        }

        public void Hide()
        {
            Visible = false;
            ClearCardFields();
        }

        public void ShowTopup()
        {
            showTopup = true;
            topupStatus = string.Empty;
            LoadPendingTopup();
            if (client != null && client.Api != null)
            {
                client.Api.GetWallet(delegate(PlyryApiResult result)
                {
                    if (result.Success && result.Wallet != null)
                    {
                        minimumTopupCents = result.Wallet.minimum_topup_cents;
                        maximumTopupCents = result.Wallet.maximum_topup_cents;
                    }
                    else topupStatus = result.Error;
                });
            }
        }

        private void Update()
        {
            if (!topupPending || topupBusy || client == null || client.Account == null
                || client.Account.id != pendingTopupUserId || Time.unscaledTime < nextTopupCheckAt) return;
            CheckTopupStatus();
        }

        public void AddReference(PlyryReference reference)
        {
            if (reference == null || reference.Data == null || reference.Data.Length == 0
                || references.Count >= GetMaximumReferenceCount()) return;
            var maximum = GetMaximumReferenceCount();
            var slotInUse = false;
            for (var i = 0; i < references.Count; i++)
                if (references[i].Slot == reference.Slot) slotInUse = true;
            if (reference.Slot < 1 || reference.Slot > maximum || slotInUse)
                reference.Slot = FindNextReferenceSlot();
            if (reference.Slot < 1) return;
            references.Add(reference);
        }

        private void AttachClient()
        {
            if (client == null || clientEventsAttached) return;
            client.AuthenticationResponded += OnAuthenticationResponded;
            client.AccountChanged += OnAccountChanged;
            client.CatalogChanged += OnCatalogChanged;
            clientEventsAttached = true;
            if (activeModel == null) SelectAvailableDefault();
        }

        private void DetachClient()
        {
            if (client == null || !clientEventsAttached) return;
            client.AuthenticationResponded -= OnAuthenticationResponded;
            client.AccountChanged -= OnAccountChanged;
            client.CatalogChanged -= OnCatalogChanged;
            clientEventsAttached = false;
        }

        private void OnAuthenticationResponded(PlyryApiResponse response)
        {
            signupChallenge = response != null && !string.IsNullOrEmpty(response.signup_challenge)
                && response.user == null ? response : null;
            if (response != null && response.user != null)
            {
                password = string.Empty;
                challengeAnswer = string.Empty;
            }
        }

        private void OnAccountChanged(PlyryAccount account)
        {
            if (account == null)
            {
                signupChallenge = null;
                return;
            }
            LoadPendingTopup();
            quoteRevision++;
            quotedPriceCents = -1;
            quoteSignature = string.Empty;
        }

        private void OnCatalogChanged(PlyryCatalog catalog)
        {
            SelectAvailableDefault();
        }

        private void OnGUI()
        {
            if (!createDefaultUi || !Visible || client == null) return;

            var previousSkin = GUI.skin;
            var previousBackground = GUI.backgroundColor;
            var previousContent = GUI.contentColor;
            if (theme != null && theme.skin != null) GUI.skin = theme.skin;
            if (theme != null)
            {
                GUI.backgroundColor = theme.panelTint;
                GUI.contentColor = theme.text;
            }

            var width = theme == null ? 460f : theme.panelWidth;
            var height = theme == null ? 660f : theme.panelHeight;
            width = Mathf.Clamp(width, 280f, Mathf.Max(280f, Screen.width - 24f));
            height = Mathf.Clamp(height, 300f, Mathf.Max(300f, Screen.height - 24f));
            var area = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
            var style = new GUIStyle(GUI.skin.window);
            if (theme != null)
            {
                style.normal.textColor = theme.text;
                style.fontSize = theme.fontSize;
                if (theme.panelBackground != null) style.normal.background = theme.panelBackground;
            }

            GUILayout.BeginArea(area, "Plyry", style);
            if (theme != null) GUI.backgroundColor = theme.surfaceTint;
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("×", GUILayout.Width(32f))) Hide();
            GUILayout.EndHorizontal();
            scroll = GUILayout.BeginScrollView(scroll);

            if (client.Account == null) DrawAccountPanel();
            else if (showTopup) DrawTopupPanel();
            else DrawGeneratorPanel();

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndArea();

            GUI.skin = previousSkin;
            GUI.backgroundColor = previousBackground;
            GUI.contentColor = previousContent;
        }

        private void DrawAccountPanel()
        {
            GUILayout.Label("Sign in or create a Plyry account");
            GUILayout.Label("If this email is new, Plyry will ask one quick question to confirm signup.");
            email = GUILayout.TextField(email, GUILayout.MinHeight(32f));
            password = GUILayout.PasswordField(password, '*', GUILayout.MinHeight(32f));

            if (signupChallenge != null)
            {
                GUILayout.Space(8f);
                GUILayout.Label(signupChallenge.question ?? "Signup check");
                challengeAnswer = GUILayout.TextField(challengeAnswer, GUILayout.MinHeight(32f));
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Terms")) Application.OpenURL(client.Api.BaseUrl + "/terms");
                if (GUILayout.Button("Privacy")) Application.OpenURL(client.Api.BaseUrl + "/privacy");
                if (GUILayout.Button("Refunds")) Application.OpenURL(client.Api.BaseUrl + "/refunds");
                if (GUILayout.Button("Content & Safety")) Application.OpenURL(client.Api.BaseUrl + "/content-safety");
                GUILayout.EndHorizontal();
            }

            var previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && !client.IsBusy && client.Api != null;
            if (GUILayout.Button(signupChallenge == null ? "Continue" : "Create account", GUILayout.Height(42f)))
            {
                client.SignIn(email, password,
                    signupChallenge == null ? string.Empty : signupChallenge.signup_challenge,
                    challengeAnswer);
            }
            GUI.enabled = previousEnabled;
            GUILayout.Space(10f);
            DrawStatus();
            if (GUILayout.Button("Refresh connection"))
                client.RefreshSession();
        }

        private void DrawTopupPanel()
        {
            GUILayout.Label("Add funds to your Plyry wallet");
            if (GUILayout.Button("← Back to generator", GUILayout.Height(32f)))
            {
                showTopup = false;
                ClearCardFields();
            }

            if (!string.IsNullOrEmpty(pendingTopupKey))
            {
                if (pendingTopupUserId != client.Account.id)
                {
                    GUILayout.Label("A previous top-up is linked to another Plyry account. Sign in to that account and check its status before starting another.", MutedLabelStyle());
                }
                else
                {
                    GUILayout.Label("A top-up is still being checked. The plugin will not start another payment until Plyry confirms its result.", MutedLabelStyle());
                    if (GUILayout.Button(topupBusy ? "Checking payment…" : "Check payment status", GUILayout.Height(38f)))
                        CheckTopupStatus();
                }
                if (!string.IsNullOrEmpty(topupStatus)) GUILayout.Label(topupStatus, MutedLabelStyle());
                return;
            }

            GUILayout.Label("Card details go directly to the payment processor for tokenization. Plyry receives only the payment-method token.", MutedLabelStyle());
            topupAmount = LabeledTextField("Amount in USD", topupAmount);
            billingCountry = LabeledTextField("Billing country", billingCountry);
            cardholderName = LabeledTextField("Cardholder name", cardholderName);
            cardNumber = LabeledTextField("Card number", cardNumber);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Expiry", GUILayout.Width(90f));
            cardExpiryMonth = GUILayout.TextField(cardExpiryMonth, GUILayout.Width(52f));
            GUILayout.Label("/", GUILayout.Width(12f));
            cardExpiryYear = GUILayout.TextField(cardExpiryYear, GUILayout.Width(68f));
            GUILayout.Label("Security code", GUILayout.Width(96f));
            cardSecurityCode = GUILayout.PasswordField(cardSecurityCode, '*', GUILayout.Width(70f));
            GUILayout.EndHorizontal();

            var limits = minimumTopupCents > 0 && maximumTopupCents > 0
                ? "Allowed amount: " + FormatMoney(minimumTopupCents) + "–" + FormatMoney(maximumTopupCents) + "."
                : "Loading the current top-up limits…";
            GUILayout.Label(limits, MutedLabelStyle());
            var previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && !topupBusy && !client.IsBusy && minimumTopupCents > 0;
            if (GUILayout.Button(topupBusy ? "Processing…" : "Add funds", GUILayout.Height(42f))) BeginTopup();
            GUI.enabled = previousEnabled;
            if (!string.IsNullOrEmpty(topupStatus)) GUILayout.Label(topupStatus, MutedLabelStyle());
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
            client.Api.GetPaymentConfig(delegate(PlyryApiResult configResult)
            {
                if (!configResult.Success || configResult.PaymentConfig == null)
                {
                    FinishTopupError(configResult.Error);
                    return;
                }
                var config = configResult.PaymentConfig;
                if (!string.Equals(config.driver, "onvo", StringComparison.OrdinalIgnoreCase))
                {
                    FinishTopupError("This payment provider is not supported by the Unity plugin yet.");
                    return;
                }

                int month, year;
                var number = DigitsOnly(cardNumber);
                var holder = (cardholderName ?? string.Empty).Trim();
                var securityCode = (cardSecurityCode ?? string.Empty).Trim();
                if (holder.Length == 0 || number.Length < 12 || number.Length > 19
                    || !int.TryParse(cardExpiryMonth, out month) || month < 1 || month > 12
                    || !int.TryParse(cardExpiryYear, out year)
                    || (cardExpiryYear ?? string.Empty).Length != 2 && (cardExpiryYear ?? string.Empty).Length != 4
                    || !IsValidSecurityCode(securityCode))
                {
                    topupBusy = false;
                    topupStatus = "Enter the cardholder name, valid card number, expiry, and security code.";
                    return;
                }
                if (year < 100) year += 2000;
                if (year < DateTime.UtcNow.Year || year > DateTime.UtcNow.Year + 25
                    || (year == DateTime.UtcNow.Year && month < DateTime.UtcNow.Month))
                {
                    topupBusy = false;
                    topupStatus = "The card expiry date has passed.";
                    return;
                }

                topupStatus = "Tokenizing the card with the payment processor…";
                client.Api.CreateOnvoPaymentMethod(config, client.Account.email, holder, number, month, year,
                    securityCode, country, delegate(PlyryApiResult tokenResult)
                    {
                        if (!tokenResult.Success || string.IsNullOrEmpty(tokenResult.PaymentMethodId))
                        {
                            FinishTopupError(tokenResult.Error);
                            return;
                        }
                        SubmitTopup(amountCents, country, tokenResult.PaymentMethodId, config.return_url);
                    });
                ClearCardFields();
            });
        }

        private void SubmitTopup(int amountCents, string country, string paymentMethodId, string returnUrl)
        {
            pendingTopupKey = Guid.NewGuid().ToString("D").ToLowerInvariant();
            pendingTopupUserId = client.Account.id;
            PlayerPrefs.SetString(PendingTopupPreferenceName(PendingTopupKeyPreference), pendingTopupKey);
            PlayerPrefs.SetInt(PendingTopupPreferenceName(PendingTopupUserPreference), pendingTopupUserId);
            PlayerPrefs.Save();
            topupPending = true;
            topupStatus = "Submitting the top-up. Do not start another payment while its status is being confirmed.";
            client.Api.CreateTopup(amountCents, paymentMethodId, country, returnUrl, pendingTopupKey,
                delegate(PlyryApiResult result)
                {
                    if (!result.Success)
                    {
                        topupBusy = false;
                        nextTopupCheckAt = Time.unscaledTime + 2f;
                        topupStatus = "Checking the same top-up request with Plyry. Do not submit another payment. " + result.Error;
                        return;
                    }
                    HandleTopupResponse(result);
                });
        }

        private void CheckTopupStatus()
        {
            if (topupBusy || string.IsNullOrEmpty(pendingTopupKey) || client == null || client.Api == null) return;
            topupBusy = true;
            topupStatus = "Checking payment status…";
            client.Api.GetTopupStatus(pendingTopupKey, delegate(PlyryApiResult result)
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
                        nextTopupCheckAt = Time.unscaledTime + 10f;
                    }
                    return;
                }
                HandleTopupResponse(result);
            });
        }

        private void HandleTopupResponse(PlyryApiResult result)
        {
            topupBusy = false;
            var response = result.Response;
            var payment = response == null ? null : response.payment;
            var requestStatus = response == null ? string.Empty : (response.request_status ?? string.Empty).ToLowerInvariant();
            var paymentStatus = (payment == null ? string.Empty : payment.status ?? string.Empty).ToLowerInvariant();
            if (requestStatus == "complete" || paymentStatus == "completed")
            {
                ClearPendingTopup();
                topupStatus = "Top-up complete. Refreshing your wallet balance…";
                client.RefreshSession();
                return;
            }
            if (requestStatus == "failed" || paymentStatus == "failed" || paymentStatus == "refunded" || paymentStatus == "chargeback")
            {
                ClearPendingTopup();
                topupStatus = "The payment did not complete. You can try again.";
                return;
            }

            topupPending = true;
            nextTopupCheckAt = Time.unscaledTime + 5f;
            topupStatus = payment != null && !string.IsNullOrEmpty(payment.status)
                ? "Payment status: " + payment.status + ". Return here after completing any bank verification."
                : (response == null || string.IsNullOrEmpty(response.message)
                    ? "The payment is still being confirmed. Do not start another request yet."
                    : response.message);
            if (payment != null && payment.requires_action && !string.IsNullOrEmpty(payment.action_url))
            {
                if (string.Equals(openedProviderActionUrl, payment.action_url, StringComparison.Ordinal))
                    topupStatus += " Complete the verification in your browser, then return here; the plugin will check the result.";
                else if (!OpenProviderAction(payment.action_url))
                    topupStatus += " The payment provider returned an invalid verification link.";
                else
                {
                    openedProviderActionUrl = payment.action_url;
                    topupStatus += " Complete the verification in your browser, then return here; the plugin will check the result.";
                }
            }
        }

        private void LoadPendingTopup()
        {
            if (client == null || client.Account == null) return;
            if (string.IsNullOrEmpty(pendingTopupKey))
            {
                pendingTopupKey = PlayerPrefs.GetString(PendingTopupPreferenceName(PendingTopupKeyPreference), string.Empty);
                pendingTopupUserId = PlayerPrefs.GetInt(PendingTopupPreferenceName(PendingTopupUserPreference), 0);
            }
            topupPending = !string.IsNullOrEmpty(pendingTopupKey);
            if (topupPending && pendingTopupUserId == client.Account.id)
                nextTopupCheckAt = Time.unscaledTime + 1f;
        }

        private void ClearPendingTopup()
        {
            topupPending = false;
            pendingTopupKey = string.Empty;
            pendingTopupUserId = 0;
            openedProviderActionUrl = string.Empty;
            PlayerPrefs.DeleteKey(PendingTopupPreferenceName(PendingTopupKeyPreference));
            PlayerPrefs.DeleteKey(PendingTopupPreferenceName(PendingTopupUserPreference));
            PlayerPrefs.Save();
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

        private static string LabeledTextField(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(130f));
            var result = GUILayout.TextField(value ?? string.Empty);
            GUILayout.EndHorizontal();
            return result;
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

        private static bool IsTerminalPayment(string status)
        {
            var value = (status ?? string.Empty).ToLowerInvariant();
            return value == "completed" || value == "failed" || value == "refunded" || value == "chargeback";
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
            var service = client == null || client.Api == null ? string.Empty : client.Api.BaseUrl;
            var suffix = Convert.ToBase64String(Encoding.UTF8.GetBytes(service))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return name + "." + suffix;
        }

        private void DrawGeneratorPanel()
        {
            var account = client.Account;
            GUILayout.Label((account.email ?? "Plyry account") + "   •   " + FormatMoney(account.wallet_cents));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Add funds", GUILayout.Height(34f))) client.OpenWallet();
            if (GUILayout.Button("Refresh balance", GUILayout.Height(34f))) client.RefreshSession();
            if (GUILayout.Button("Sign out", GUILayout.Height(34f))) client.SignOut();
            GUILayout.EndHorizontal();
            GUILayout.Space(8f);

            if (client.Catalog == null || client.Catalog.models == null || client.Catalog.models.Length == 0)
            {
                GUILayout.Label("Loading available media models…");
                DrawStatus();
                return;
            }

            if (AvailableTypes().Count == 0)
            {
                GUILayout.Label("No media kinds are available for this account or target.");
                DrawStatus();
                return;
            }

            DrawTypeSelector();
            DrawModelSelector();
            if (activeModel == null)
            {
                GUILayout.Label("No models are currently available for this media kind.");
                DrawStatus();
                return;
            }

            GUILayout.Label("Model: " + activeModel.label);
            DrawReferenceModeSelector();
            DrawQualitySelector();
            if (selectedType == "splat") DrawSplatQualitySelector();
            modelSettingsValid = true;
            DrawModelSettings();

            GUILayout.Space(8f);
            GUILayout.Label("Prompt");
            prompt = GUILayout.TextArea(prompt, GUILayout.MinHeight(92f));
            if (target != null)
            {
                GUILayout.Label("Target: " + target.DisplayName, MutedLabelStyle());
                if (!string.IsNullOrEmpty(target.PromptInstructions))
                    GUILayout.Label("Game instructions are added to the prompt.", MutedLabelStyle());
            }

            DrawReferences();
            DrawQuoteAndGenerate();
            DrawStatus();
        }

        private void DrawSplatQualitySelector()
        {
            GUILayout.Label("Splat quality");
            var index = splatQuality == "high" ? 2 : (splatQuality == "medium" ? 1 : 0);
            var selected = GUILayout.SelectionGrid(index, new[] { "Low", "Medium", "High" }, 3);
            splatQuality = selected == 2 ? "high" : (selected == 1 ? "medium" : "low");
        }

        private void DrawQuoteAndGenerate()
        {
            var currentSignature = CurrentQuoteSignature();
            if (quoteSignature != currentSignature)
            {
                quoteSignature = string.Empty;
                quotedPriceCents = -1;
            }

            var needsReference = activeModel.requires_reference || !string.IsNullOrEmpty(selectedReferenceMode);
            var canSubmit = (!activeModel.requires_reference || !string.IsNullOrEmpty(selectedReferenceMode))
                && (!needsReference || references.Count > 0)
                && (!needsReference || references.Count <= GetMaximumReferenceCount())
                && modelSettingsValid;
            if (quotedPriceCents < 0 || quoteSignature != currentSignature)
            {
                var previousEnabled = GUI.enabled;
                GUI.enabled = previousEnabled && !client.IsBusy && canSubmit;
                if (GUILayout.Button(client.IsBusy ? "Checking price…" : "Check current Plyry price", GUILayout.Height(42f)))
                    RequestQuote();
                GUI.enabled = previousEnabled;
                return;
            }

            GUILayout.Label("Plyry charge: " + FormatMoney(quotedPriceCents)
                + "   •   Wallet: " + FormatMoney(quotedWalletCents));
            if (!quoteHasSufficientFunds)
            {
                if (GUILayout.Button("Add funds to generate", GUILayout.Height(42f)))
                    client.OpenWallet();
                return;
            }

            var previousEnabledForGeneration = GUI.enabled;
            GUI.enabled = previousEnabledForGeneration && !client.IsBusy && canSubmit;
            var previous = GUI.backgroundColor;
            if (theme != null) GUI.backgroundColor = theme.accent;
            if (GUILayout.Button(client.IsBusy ? "Working…" : "Generate for " + FormatMoney(quotedPriceCents), GUILayout.Height(44f)))
                client.Generate(BuildGenerationRequest(), target);
            GUI.backgroundColor = previous;
            GUI.enabled = previousEnabledForGeneration;
        }

        private void RequestQuote()
        {
            var requestedSignature = CurrentQuoteSignature();
            var requestedRevision = quoteRevision;
            client.RequestQuote(BuildGenerationRequest(), delegate(PlyryApiResult result)
            {
                if (requestedRevision != quoteRevision) return;
                if (result.Success && result.Response != null)
                {
                    quotedPriceCents = result.Response.price_cents;
                    quotedWalletCents = client.Account == null ? 0 : client.Account.wallet_cents;
                    quoteHasSufficientFunds = result.Response.has_sufficient_funds;
                    quoteSignature = requestedSignature;
                    localStatus = null;
                }
                else
                {
                    quotedPriceCents = -1;
                    SetLocalStatus(result.Error);
                }
            });
        }

        private string CurrentQuoteSignature()
        {
            if (activeModel == null) return string.Empty;
            var options = SelectedOptions(activeModel);
            var queue = settings == null ? string.Empty : settings.ToQueueOptionsJson(options);
            return selectedType + "|" + selectedModelId + "|" + selectedReferenceMode + "|"
                + quality + "|" + splatQuality + "|" + references.Count + "|" + queue;
        }

        private PlyryGenerationRequest BuildGenerationRequest()
        {
            var options = SelectedOptions(activeModel);
            return new PlyryGenerationRequest
            {
                Type = selectedType,
                Model = activeModel.id,
                Prompt = prompt,
                Quality = quality,
                SplatQuality = splatQuality,
                ReferenceMode = selectedReferenceMode,
                ModelOption = activeModel,
                Settings = settings,
                SettingsOptions = options,
                References = string.IsNullOrEmpty(selectedReferenceMode)
                    ? new PlyryReference[0]
                    : references.ToArray()
            };
        }
        private void DrawTypeSelector()
        {
            var types = AvailableTypes();
            if (types.Count == 0)
            {
                GUILayout.Label("No media kinds are available for this account or target.");
                return;
            }
            var labels = new string[types.Count];
            var index = 0;
            for (var i = 0; i < types.Count; i++)
            {
                labels[i] = LabelForType(types[i]);
                if (types[i] == selectedType) index = i;
            }
            GUILayout.Label("Media kind");
            var selected = GUILayout.SelectionGrid(index, labels, Mathf.Min(3, labels.Length));
            selected = Mathf.Clamp(selected, 0, types.Count - 1);
            if (selectedType != types[selected])
            {
                selectedType = types[selected];
                SelectAvailableDefault();
            }
        }

        private void DrawModelSelector()
        {
            var models = GetCurrentType();
            if (models == null || models.options == null || models.options.Length == 0) return;
            var index = FindModelIndex(models.options, selectedModelId);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Model", GUILayout.Width(72f));
            if (GUILayout.Button("‹", GUILayout.Width(38f))) index = (index + models.options.Length - 1) % models.options.Length;
            GUILayout.Label(models.options[index].label ?? models.options[index].id, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("›", GUILayout.Width(38f))) index = (index + 1) % models.options.Length;
            GUILayout.EndHorizontal();
            if (selectedModelId != models.options[index].id)
            {
                selectedModelId = models.options[index].id;
                SelectModel(models.options[index], true);
            }
        }

        private void DrawReferenceModeSelector()
        {
            var modes = activeModel.references ?? new PlyryReferenceMode[0];
            if (modes.Length == 0)
            {
                selectedReferenceMode = string.Empty;
                return;
            }

            var allowNone = !activeModel.requires_reference;
            var count = modes.Length + (allowNone ? 1 : 0);
            var current = 0;
            if (allowNone && string.IsNullOrEmpty(selectedReferenceMode)) current = 0;
            for (var i = 0; i < modes.Length; i++)
                if (modes[i].mode == selectedReferenceMode) current = i + (allowNone ? 1 : 0);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Reference mode", GUILayout.Width(120f));
            if (GUILayout.Button("‹", GUILayout.Width(36f))) current = (current + count - 1) % count;
            var modeLabel = allowNone && current == 0 ? "None" : modes[current - (allowNone ? 1 : 0)].mode;
            GUILayout.Label(modeLabel, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("›", GUILayout.Width(36f))) current = (current + 1) % count;
            GUILayout.EndHorizontal();

            var next = allowNone && current == 0 ? string.Empty : modes[current - (allowNone ? 1 : 0)].mode;
            if (next != selectedReferenceMode)
            {
                selectedReferenceMode = next;
                settings = PlyrySettingsSelection.Defaults(SelectedOptions(activeModel), quality == "test");
                polygonCountText = null;
            }
        }

        private void DrawQualitySelector()
        {
            GUILayout.Label("Quality");
            var index = quality == "test" ? 1 : 0;
            var next = GUILayout.SelectionGrid(index, new[] { "Full quality", "Test quality" }, 2);
            var nextQuality = next == 1 ? "test" : "full";
            if (nextQuality != quality)
            {
                quality = nextQuality;
                if (activeModel != null) settings = PlyrySettingsSelection.Defaults(SelectedOptions(activeModel), quality == "test");
                polygonCountText = null;
            }
        }

        private void DrawModelSettings()
        {
            var options = SelectedOptions(activeModel);
            if (options == null) return;
            if (options.duration != null && options.duration.values != null && options.duration.values.Length > 0)
            {
                settings.duration = DrawIntegerChoice("Duration", options.duration.values, settings.duration,
                    options.duration.default_test);
            }
            if (options.aspect != null && options.aspect.emit && options.aspect.values != null && options.aspect.values.Length > 0)
            {
                settings.aspect = DrawStringChoice("Aspect ratio", options.aspect.values, settings.aspect);
            }
            if (options.resolution != null && options.resolution.values != null && options.resolution.values.Length > 0)
            {
                var label = string.IsNullOrEmpty(options.resolution.label) ? "Resolution" : options.resolution.label;
                settings.resolution = DrawStringChoice(label, options.resolution.values, settings.resolution);
            }
            if (options.audio != null)
                settings.audio = GUILayout.Toggle(settings.audio, "Generate audio");
            if (options.polygon_count != null)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Polygon count", GUILayout.Width(120f));
                if (polygonCountText == null) polygonCountText = settings.polygonCount.ToString(CultureInfo.InvariantCulture);
                var input = GUILayout.TextField(polygonCountText, GUILayout.Width(100f));
                int count;
                if (input != polygonCountText) polygonCountText = input;
                var step = Mathf.Max(1, options.polygon_count.step);
                modelSettingsValid = int.TryParse(polygonCountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out count)
                    && count >= options.polygon_count.min && count <= options.polygon_count.max
                    && (count - options.polygon_count.min) % step == 0;
                if (modelSettingsValid) settings.polygonCount = count;
                GUILayout.Label(options.polygon_count.min + "–" + options.polygon_count.max, MutedLabelStyle());
                GUILayout.EndHorizontal();
                if (!modelSettingsValid)
                    GUILayout.Label("Enter a value in range using the model's step size.", MutedLabelStyle());
            }
        }

        private void DrawReferences()
        {
            GUILayout.Space(8f);
            var maximum = GetMaximumReferenceCount();
            GUILayout.Label(maximum == 0 ? "References (choose a reference mode to attach files)"
                : "References (" + references.Count + "/" + maximum + ")");
            for (var i = references.Count - 1; i >= 0; i--)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(ReferenceSlotLabel(references[i].Slot) + ": "
                    + (references[i].FileName ?? "Reference " + references[i].Slot), GUILayout.ExpandWidth(true));
                if (GUILayout.Button("Remove", GUILayout.Width(74f))) references.RemoveAt(i);
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            var previousEnabled = GUI.enabled;
            GUI.enabled = previousEnabled && maximum > 0 && references.Count < maximum;
            if (target != null && GUILayout.Button("Use target texture"))
            {
                var reference = target.CaptureTextureReference(FindNextReferenceSlot());
                if (reference != null) AddReference(reference);
                else SetLocalStatus("This target has no readable material texture. Use your game's reference picker.");
            }
            if (GUILayout.Button("Choose game reference"))
            {
                if (ReferenceSelectionRequested != null) ReferenceSelectionRequested();
                else SetLocalStatus("Connect your game's image, video, or audio picker to AddReference.");
            }
            GUILayout.EndHorizontal();
            GUI.enabled = previousEnabled;
        }

        private PlyryReferenceMode GetSelectedReferenceMode()
        {
            if (activeModel == null || string.IsNullOrEmpty(selectedReferenceMode)) return null;
            return FindReferenceMode(activeModel, selectedReferenceMode);
        }

        private int GetMaximumReferenceCount()
        {
            var mode = GetSelectedReferenceMode();
            if (mode == null) return 0;
            return Mathf.Clamp(mode.max_files <= 0 ? 1 : mode.max_files, 1, 7);
        }

        private int FindNextReferenceSlot()
        {
            var maximum = GetMaximumReferenceCount();
            for (var slot = 1; slot <= maximum; slot++)
            {
                var found = false;
                for (var i = 0; i < references.Count; i++)
                    if (references[i].Slot == slot) { found = true; break; }
                if (!found) return slot;
            }
            return 0;
        }

        private string ReferenceSlotLabel(int slot)
        {
            var mode = GetSelectedReferenceMode();
            if (mode != null && mode.fields != null && slot > 0 && slot <= mode.fields.Length)
            {
                var field = mode.fields[slot - 1];
                if (field != null && !string.IsNullOrEmpty(field.label)) return field.label;
            }
            return "Reference " + slot;
        }

        private void DrawStatus()
        {
            GUILayout.Space(6f);
            var message = string.IsNullOrEmpty(localStatus) ? client.Status : localStatus;
            if (!string.IsNullOrEmpty(message)) GUILayout.Label(message, MutedLabelStyle());
        }

        private void SetLocalStatus(string message)
        {
            localStatus = message ?? string.Empty;
        }

        private string localStatus;

        private void SubmitGeneration()
        {
            if (activeModel == null) return;
            client.Generate(BuildGenerationRequest(), target);
            localStatus = null;
        }

        private void SelectAvailableDefault()
        {
            var entry = GetCurrentType();
            if (entry == null || entry.options == null || entry.options.Length == 0)
            {
                var available = AvailableTypes();
                if (available.Count > 0) selectedType = available[0];
                entry = GetCurrentType();
            }
            if (entry == null || entry.options == null || entry.options.Length == 0) return;

            var selected = FindModel(entry.options, selectedModelId);
            if (selected == null)
            {
                selected = FindModel(entry.options, entry.default_model);
                if (selected == null) selected = entry.options[0];
            }
            selectedModelId = selected.id;
            SelectModel(selected, true);
        }

        private void SelectModel(PlyryModelOption model, bool resetSettings)
        {
            activeModel = model;
            var modes = model.references ?? new PlyryReferenceMode[0];
            if (model.requires_reference && modes.Length > 0)
                selectedReferenceMode = modes[0].mode;
            else if (FindReferenceMode(model, selectedReferenceMode) == null)
                selectedReferenceMode = string.Empty;

            var options = SelectedOptions(model);
            if (resetSettings || settings == null)
            {
                settings = PlyrySettingsSelection.Defaults(options, quality == "test");
                polygonCountText = null;
            }
        }

        private PlyryGenerationOptions SelectedOptions(PlyryModelOption model)
        {
            var mode = FindReferenceMode(model, selectedReferenceMode);
            return mode != null && mode.generation_options != null ? mode.generation_options : model.generation_options;
        }

        private List<string> AvailableTypes()
        {
            var result = new List<string>();
            if (client == null || client.Catalog == null || client.Catalog.models == null) return result;
            for (var i = 0; i < client.Catalog.models.Length; i++)
            {
                var entry = client.Catalog.models[i];
                if (entry.options == null || entry.options.Length == 0) continue;
                if (target == null || target.Allows(entry.type)) result.Add(entry.type);
            }
            return result;
        }

        private PlyryMediaCatalogEntry GetCurrentType()
        {
            return client == null || client.Catalog == null ? null : client.Catalog.FindType(selectedType);
        }

        private static PlyryModelOption FindModel(PlyryModelOption[] options, string id)
        {
            if (options == null) return null;
            for (var i = 0; i < options.Length; i++)
                if (options[i].id == id) return options[i];
            return null;
        }

        private static int FindModelIndex(PlyryModelOption[] options, string id)
        {
            if (options == null) return 0;
            for (var i = 0; i < options.Length; i++)
                if (options[i].id == id) return i;
            return 0;
        }

        private static PlyryReferenceMode FindReferenceMode(PlyryModelOption model, string mode)
        {
            if (model == null || model.references == null) return null;
            for (var i = 0; i < model.references.Length; i++)
                if (model.references[i].mode == mode) return model.references[i];
            return null;
        }

        private static int DrawIntegerChoice(string label, int[] values, int selected, int fallback)
        {
            var index = Array.IndexOf(values, selected);
            if (index < 0) index = Array.IndexOf(values, fallback);
            if (index < 0) index = 0;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120f));
            if (GUILayout.Button("‹", GUILayout.Width(34f))) index = (index + values.Length - 1) % values.Length;
            GUILayout.Label(values[index] + (label == "Duration" ? " sec" : string.Empty), GUILayout.ExpandWidth(true));
            if (GUILayout.Button("›", GUILayout.Width(34f))) index = (index + 1) % values.Length;
            GUILayout.EndHorizontal();
            return values[index];
        }

        private static string DrawStringChoice(string label, PlyryChoice[] values, string selected)
        {
            var index = 0;
            for (var i = 0; i < values.Length; i++)
                if (values[i].value == selected) index = i;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(120f));
            if (GUILayout.Button("‹", GUILayout.Width(34f))) index = (index + values.Length - 1) % values.Length;
            GUILayout.Label(string.IsNullOrEmpty(values[index].label) ? values[index].value : values[index].label, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("›", GUILayout.Width(34f))) index = (index + 1) % values.Length;
            GUILayout.EndHorizontal();
            return values[index].value;
        }

        private GUIStyle MutedLabelStyle()
        {
            var style = new GUIStyle(GUI.skin.label);
            style.wordWrap = true;
            style.fontSize = Mathf.Max(10, style.fontSize - 1);
            if (theme != null) style.normal.textColor = theme.mutedText;
            return style;
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





