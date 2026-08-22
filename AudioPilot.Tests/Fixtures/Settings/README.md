# Settings compatibility fixtures

`1.0.0.json` is the initial storage-contract baseline. Its synthetic device IDs,
non-default options, hotkeys, and routines are test data only.

Keep this file unchanged after the 1.0.0 schema is released. Do not regenerate it
from current models when the schema evolves. Add fixtures for later released
schemas and adapt the compatibility tests to assert the intended migrated values
when a real conversion changes their representation. Additive migrations should
continue to preserve every existing fixture value.

`SettingsCompatibilityTests` exercise loading, saving, repeated loading, backup
recovery and preservation, and both import modes without touching real audio or user settings.
