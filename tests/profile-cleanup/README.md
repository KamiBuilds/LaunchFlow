# Profile cleanup regression

Run on Linux with .NET 8:

```sh
DOTNET=dotnet bash tests/profile-cleanup/run.sh
```

This links the actual profile service and the explicitly simulated DPAPI contract.
It makes a tiny synthetic profile readable but partially undeletable, without mocking
filesystem operations. When run as root, only the owned test child drops DAC bypass
capabilities; ordinary users already receive normal permission enforcement. Root runs
therefore need `capsh`. The script uses a disposable fixture directory, not user data.

The assertions require a complete published legacy archive, atomic removal of the
source from the active namespace before recursive deletion, unchanged ciphertext on
ordinary retry, successful full restoration, and exclusion of `.sealed-*`/`.restore-*`
helpers and historical helper packages from normal profile enumeration.

The original implementation fails the authoritative-directory assertion after a real
partial delete. A retained source can be selected without editing production:

```sh
DOTNET=dotnet bash tests/profile-cleanup/run.sh -p:ProfileSource=/absolute/path/to/baseline/ProfileVaultService.cs
```

Rebuild without the override after the expected RED. This test is not native Windows
ACL/rename, DPAPI, power-loss durability, secure erasure or hostile same-user containment
proof. Quarantined remnants can contain plaintext browser files and need support-led
cleanup; they must never be reused or sealed as a normal workflow profile.
