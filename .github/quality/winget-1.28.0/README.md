# WinGet 1.28.0 schemas

These unmodified Microsoft schemas validate generated version, installer, and default-locale manifests without
requiring a registered winget client in CI. The source commit is
`5d302e449b63ca1534bf4b7c871f048ad07d5dca` of `microsoft/winget-cli`.
`../schemas-lock.json` records the immutable download URLs and SHA-256 hashes. The upstream MIT license is in `LICENSE`.

The schemas are build inputs and are not distributed inside the application. Release validation additionally checks
manifest consistency, actual MSI identity, unique architectures, and duplicate YAML keys. These checks do not replace
native winget validation or a published installer download check before submission to winget-pkgs.
