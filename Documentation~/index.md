# Plyry AI Media for Unity

Use the Editor menu at Plyry > AI Media Generator to sign in through the Plyry v1 API, choose a media kind and model, add reference assets, set model options, and import generated files under Assets/PlyryGenerated. The Editor tool supports in-plugin wallet top-ups.

For runtime generation, add PlyryClient, PlyryRuntimePanel, and PlyryGenerationTarget components. The runtime panel supports linked-app sign-in, signup, generation, wallet balance, and in-plugin top-ups. Access tokens are kept in memory and refreshed while the client is open. By default, users sign in again after closing the app. To persist a runtime session, implement and assign a PlyryCredentialStore backed by the platform's secure credential storage; it stores only the rotating refresh token. Do not use PlayerPrefs or ordinary files for tokens. Store errors are available through `PlyryApiClient.CredentialStoreWarning`.

Read the [Plyry API v1 reference](https://plyry.com/docs/api) and [OpenAPI specification](https://plyry.com/v1/openapi.json). See the package README for account, storage, and platform details.
