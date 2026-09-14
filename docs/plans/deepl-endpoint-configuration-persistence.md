# DeepL endpoint configuration persistence

Priority: P2. Covers issue 2.

## Outcome

Saving a custom DeepL endpoint in Jellyfin must update `DeepLApiUrl`, survive reload/restart, and be passed to translation requests.

## Affected code

- `Jellyfin.Plugin.MetaTube/Configuration/configPage.html`
- Verification references: `PluginConfiguration.DeepLApiUrl` and `TranslationHelper`.

## Implementation steps

1. Change the form submission assignment from `config.DeepLXAltUrl` to `config.DeepLApiUrl`.
2. Check that load, save, serialized configuration, and translation all use that same existing property. Preserve other form values and the current configuration schema.
3. Do not add a legacy-property migration: the wrongly named property was not the configured translation endpoint. Users whose attempted value was discarded must re-enter it.

## Verification and acceptance

- Save a synthetic custom URL, navigate away and back, and confirm the field value and configuration API value agree.
- Restart the isolated server and confirm persistence.
- Trigger a synthetic DeepL translation and inspect the loopback request's `deepl-api-url` parameter.
- Clear the field, save, and verify the blank value persists without changing unrelated configuration.
- Check the embedded HTML resource and both server builds. Emby uses its own configuration UI; no Emby property rename is needed.

This is a one-line behavioral fix. Prefer the focused UI/request verification above over adding a test that merely searches the source for a property name. Never record real API keys in evidence.

## Implementation evidence (2026-09-14)

Branch: `bugfix/deepl-endpoint-configuration-persistence`. The submit handler now assigns `DeepLApiUrl`; configuration schema and translation property remain unchanged. No legacy-property migration is added; affected users must re-enter discarded values.

`node tests/deepl-form.cjs` passed by executing the actual page script through a minimal DOM/API adapter: custom endpoint save, navigation/reload, clearing, and unrelated server/token preservation. Two C# cases passed for XML save/reload and loopback `deepl-api-url` transmission, including blank values. This is controlled handler/serialization evidence, not a real browser or server restart. Isolated server UI/restart acceptance remains pending.
