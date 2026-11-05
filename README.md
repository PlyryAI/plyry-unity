# Plyry AI Media for Unity

Open source Unity package for generating media through the Plyry account service in the Editor and in a game.

## Features

- Sign in or create a linked Plyry app session from the Editor or runtime panel.
- Load the current public model catalog and available media kinds from Plyry.
- Choose model, reference mode, quality, duration, aspect, resolution, audio, and polygon settings when a model supports them.
- Add image, video, and audio references. The runtime panel can capture a target material texture or connect to a game-specific reference picker.
- Queue a generation through the public v1 API, poll the job, download its result, and verify Plyry's SHA-256 delivery receipt.
- Read wallet balances and add funds in the plugin. ONVO card details are sent directly to ONVO for tokenization; Plyry receives only the resulting payment-method ID.
- Import Editor results into Assets/PlyryGenerated/<kind>/.
- Save runtime results under Application.persistentDataPath/Plyry/ and restore the latest file for a target and media kind on later launches.
- Use a PlyryGenerationTarget component to control which media kinds a player can generate for a game object.
- Style the built-in runtime panel with a PlyryTheme asset or replace it with a game-owned UI through the public PlyryClient API.
- MIT licensed.

The package sends account and generation requests only to the configured Plyry API. Provider credentials stay on the Plyry service. When ONVO is configured, card details are sent directly to ONVO for tokenization and are never sent to Plyry. The player sees the current price and wallet balance before submitting each generation.

## Requirements and compatibility

- Unity 2019.4 LTS through Unity 6 and later.
- Runtime network access to the Plyry v1 API at https://plyry.com/v1.
- Windows, macOS, Linux, Android, and iOS builds use Unity's UnityWebRequest and local persistent storage.
- Unity WebGL builds hosted on another website can use the API's bearer-token CORS support. Signing in binds the linked app to that website's origin; use HTTPS for production sites. No Plyry session cookies or embedded Plyry account pages are required.
- Access tokens stay in memory and refresh automatically while the client is open. By default, users sign in again after the game or Editor client closes. Runtime apps can opt into session persistence with a platform-specific `PlyryCredentialStore`; the account's Linked apps page can revoke a Unity link.
- Consoles and platform stores may have their own network, privacy, and account policies. Confirm API access and the payment processor's verification flow for each console before release.
- 3D and splat results are retained as source files. Unity may need a compatible runtime importer to display formats such as GLB or SOG.

## Install

In Unity, open Package Manager, choose Add package from disk, and select this folder's package.json.

The Quick Start component is available under the package's **Samples** section in Package Manager.

For a local dependency in Packages/manifest.json, use a path relative to the Unity project. This example assumes your game project folder is a sibling of this package folder:

~~~json
{
  "dependencies": {
    "com.plyry.unity": "file:../plyry-unity"
  }
}
~~~

The live service must expose the Plyry v1 API described in the online [API documentation](https://plyry.com/docs/api) and its [OpenAPI specification](https://plyry.com/v1/openapi.json).

## Runtime setup

1. Add PlyryClient to a persistent scene object. Its service URL defaults to https://plyry.com.
2. Add PlyryRuntimePanel to that object, or create your own game UI and call the public methods on PlyryClient.
3. Add PlyryGenerationTarget to each object players may customize.
4. Give each target a stable ID. The component creates one in the Editor when first added. If multiple scene objects share a copied component ID, use the **Generate New Stable Target ID** button in the Inspector for each object that needs separate saved media.
5. Set the target's allowed media types. An empty list allows every kind available to the account.
6. Assign the target to the runtime panel. Use its material texture as an image reference, or connect ReferenceSelectionRequested to a game picker and pass the selection to AddReference.
7. Press Play, sign in, choose a reference mode and its labeled media slots when needed, then generate. The most recent result for each target and media kind is restored from local persistent storage when the game starts again.

Sign in or signup sends the user's Plyry credentials directly to Plyry's API and creates a linked-app entry. Runtime builds use the Unity product name by default; set Linked App Name on PlyryClient to customize it. The Editor tool defaults to **Plyry Unity Editor** and lets you change the name. This is direct account authentication, not OAuth or single sign-on. API signup does not add email verification or account recovery. Never forward the password or app refresh token to another service. Users can disconnect the app from Account → Linked apps.

To keep a runtime session across app launches, implement `PlyryCredentialStore` with the platform's secure credential storage (such as Keychain or Android Keystore) and assign that component to PlyryClient. It stores only the rotating refresh token, keyed by the service URL; access tokens remain in memory. Save each replacement token, return it from `TryLoadRefreshToken`, and remove it in `ClearRefreshToken`. Do not use PlayerPrefs or an ordinary file for refresh tokens. Without a store, the client remains memory-only. Store errors are available as `PlyryApiClient.CredentialStoreWarning` and appear in the runtime status.

When a generation needs more wallet funds, choose **Add funds** to enter a top-up amount and billing details in the plugin. The built-in card form currently supports ONVO tokenization. The plugin stores a random idempotency key while an attempt is unresolved and checks that same request after a network interruption or app restart; it will not start another top-up until the first result is known. Provider verification may open ONVO's page in the system browser and return to a minimal payment-complete endpoint; no Plyry sign-in or wallet page is involved.

## Custom runtime UI

Create a PlyryTheme asset from Assets > Create > Plyry > Runtime UI Theme. Assign a GUISkin, panel background, colors, font size, and panel dimensions to match the game.

Set Create Default Ui off on PlyryRuntimePanel to supply a completely game-owned UI. The panel exposes AddReference, NextReferenceSlot, MaximumReferenceCount, the current PlyryReferenceMode, and its labeled reference fields. The client exposes account, catalog, status, sign-in, sign-out, wallet, and generation operations. `OpenWallet()` shows the built-in top-up view or raises `TopupRequested` when the game provides its own UI; the `PlyryApiClient` exposes the wallet and top-up operations for that flow. Listen to the client events to keep custom UI state current.

The default panel includes an account form, signup challenge, wallet actions, media/model selectors, reference mode, quality, model settings, prompt, and generation status. The runtime panel uses Unity's built-in IMGUI and has no UGUI or third-party package dependency.

## Editor generation

Open Plyry > AI Media Generator. Sign in, choose the media kind and model, set references and model options, and submit a prompt. Completed files are imported into the configured project folder, grouped by media kind. A splat scene sidecar is imported beside its primary file.

The Editor window supports image, video, and audio reference files from the Unity project. Texture references are encoded as PNG. Import external reference files into the project first.

## Game object targets

PlyryGenerationTarget can restrict media kinds, add game-authored prompt instructions, and apply compatible outputs:

- Images and anime images can replace a Renderer material texture.
- Videos can play through an assigned VideoPlayer.
- Audio and music can play through an assigned AudioSource.
- Other outputs remain available as local files through OnMediaReady and the MediaReady event.

Use Stable Target Id for durable save mapping. Runtime generated files are organized by target, media kind, and Plyry job hash below Application.persistentDataPath/Plyry/. Previous files remain on disk and in the local index. FindLatestForTarget restores the most recent file for each kind; FindAllForTarget exposes the target's saved history.

## Plyry website API

The plugin uses the same versioned API as other Plyry clients:

- `POST /v1/auth/login`, `POST /v1/auth/signup/challenges`, and `POST /v1/auth/signup` for linked-app sign-in and signup.
- `POST /v1/auth/refresh` and `POST /v1/auth/revoke` for rotating access tokens and sign-out.
- `GET /v1/me`, `GET /v1/wallet`, and public `GET /v1/models` for account, balance, and model information.
- `POST /v1/quotes` and `POST /v1/generations` for price quotes and prompt/reference submissions.
- `GET /v1/generations/{hash}`, `GET /v1/media/{hash}`, and `POST /v1/generations/{hash}/cache` for job tracking and checksum-verified delivery.
- `GET /v1/payments/config`, `POST /v1/wallet/topups`, and `GET /v1/wallet/topups/requests/{requestKey}` for in-plugin top-ups.

The API scopes requested by Unity are `account:read`, `wallet:read`, `wallet:topup`, `generation:create`, and `generation:read`. The public catalog exposes supported model IDs and customer-facing settings without provider credentials or internal routing.

## Project changes

No Plyry website page is loaded for authentication, account linking, generation, or top-up. Only a payment provider's verification step may open an external browser page. The package does not persist card numbers, security codes, or passwords. A user-supplied secure credential store can persist the refresh token for runtime sessions.
