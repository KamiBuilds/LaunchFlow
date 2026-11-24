# Vault creation regressions

Production source-linked SQLite/Argon2 tests cover failure-atomic first creation,
owned rollback/retry, existing/foreign destination preservation, concurrent
no-clobber publication, retained/uncertain outcomes, WAL/checkpoint/disposal,
sensitive-buffer lifetime and fixed optional diagnostic phase checkpoints.
Modeled fault hooks exist only in this test executable; the no-hooks gate checks
their erasure and exercises real creation/owner reopen without implementations.

Use only generated synthetic fixtures under an owned temporary root. No real
vault or service may be reset to run these tests. The runners disable dumps,
retain actual child codes and safe logs, and accept explicit result locations.

```sh
python3 tests/vault-creation/run.py --label focused \
  --dotnet /path/to/dotnet --evidence /path/to/owned/results
python3 tests/vault-creation/run.py --label no-hooks --no-hooks \
  --dotnet /path/to/dotnet --evidence /path/to/owned/results
```

The additional real Linux permission-denial controls require `setpriv`. They
remove DAC override/read-search capabilities from the owned test child rather
than claiming a root process reproduced a normal permission failure:

```sh
python3 tests/vault-creation/run.py --label permissions --permissions \
  --dotnet /path/to/dotnet --evidence /path/to/owned/results
```

`verify-maintained.py` is a Linux/bash storage-overlay characterization runner.
It archives the selected local HEAD, overlays the two current storage owner
files and these tests, then runs maintained gates in a separate copy. Before a
commit, that intentionally does **not** include unrelated uncommitted UI edits;
its result is not whole-candidate integration approval. Final integrated gates
must separately use the complete frozen candidate source.

```sh
python3 tests/vault-creation/verify-maintained.py \
  --dotnet /path/to/dotnet --evidence /path/to/new/owned/results
```

Existing production format, derivation parameters and password representation
remain compatibility boundaries. Unix publication uses atomic no-clobber linking;
unsupported filesystems fail closed with the committed candidate preserved.
Windows publication/sharing/ACL behavior requires native acceptance. Neither
modeled return faults nor an intent marker proves power-loss durability or
resistance to a malicious process already controlling the same OS user's files.
