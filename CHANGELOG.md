# Changelog

## 1.1.1

- Added an optional secure refresh-token storage component for runtime apps.
- Preserved sessions after temporary refresh failures and cleared credentials only when the refresh token is rejected.
- Prevented late authentication responses from restoring a session after sign-out or service changes.
- Removed token response bodies and token fields before exposing authentication results.

## 1.1.0

- Replaced cookie and CSRF sessions with the Plyry Public API v1 linked-app bearer-token flow.
- Added signup challenges, rotating refresh tokens, scoped account access, and remote linked-app revocation.
- Moved catalog, quote, generation, job polling, media download, and cache acknowledgment to v1 endpoints.
- Added in-plugin wallet top-ups with direct ONVO tokenization and idempotent request recovery.
- Enabled remote-origin WebGL clients through the API's origin-bound CORS support.
- Updated the setup and API documentation for the unified API.

## 1.0.0

- Added the UPM package for Unity 2019.4 and later.
- Added Editor and runtime generation through the Plyry account API.
- Added model catalog, reference, quality, and generation setting selection.
- Added target components and persistent delivery to game storage.
- Added media checksum verification and cache delivery receipts.
- Added a customizable runtime panel and MIT license.
- Added the Quick Start sample to Package Manager and stable Unity metadata files.
- Guarded account and generation requests until the Plyry CSRF session is ready.
- Added request timeouts, safe reference upload headers, and robust local library recovery.
- Preserved caller prompts when adding target instructions and invalidated prices after account refreshes.
