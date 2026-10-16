# Language packs

The app reads UTF-8 JSON language packs from `Languages` beside the executable at startup. Each pack is data only; adding a language does not require changing or compiling C# code.

Copy `Languages/en-US.json` to a new file, set its culture code and native name, then translate its `strings` values. Keep the keys unchanged. For example:

```json
{
  "culture": "fr-FR",
  "nativeName": "Français",
  "strings": {
    "Settings.Title": "Paramètres des paroles"
  }
}
```

Restart the app after adding or removing a file. Valid packs appear automatically in the settings language selector. Missing keys fall back to English; malformed packs are skipped so an incomplete translation cannot prevent startup. The three bundled packs cover English (`en-US`), Simplified Chinese (`zh-CN`), and Traditional Chinese (`zh-TW`).

Use valid Windows/.NET culture names. Keep numbered format placeholders such as `{0}` and `{1}` in translated messages; they insert numbers or playback times. Lyric text, supplied lyric translations, track metadata, technical paths, and raw diagnostic errors are preserved as received.

`UiLanguage=system` in `settings.ini` follows the Windows user's display language. Explicit language codes persist across restarts. Unsupported Windows languages fall back to English; related cultures use an available matching language where possible. No numeric culture changes are applied, so settings continue to use invariant decimal formatting.

Language selection previews immediately. Apply or Save commits it; Cancel restores the most recently applied language while discarding other pending edits as usual.
