# Windows publisher fixture

`publisher.dll` is a 6 KB managed test assembly whose only method returns `42`. It is never executed by the tests.
It has an SHA-256 Authenticode signature from an isolated, self-signed **AudioPilot test fixture only** identity,
valid from 2025 through 2099 with the code-signing usage. Its temporary private key was discarded after signing;
the certificate was not added to any Windows certificate store.

Certificate SHA-256: `BB7EE30CA7475E632C3DC74B25E00364AF0DFECE3E317C3738D7ABF2BB5E0371`.

The focused tests verify the signature, reject a different publisher and untrusted ordinary policy, and mutate a
covered PE section to verify rejection of a bad digest. This public test identity never authorizes an updater feed.
