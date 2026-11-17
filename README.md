# C# object pool

[![Build and test](https://github.com/mobeenafzal570/csharp-object-pool/actions/workflows/ci.yml/badge.svg)](https://github.com/mobeenafzal570/csharp-object-pool/actions/workflows/ci.yml)

A dependency-free object pool with explicit leases, bounded idle retention, reset/cleanup callbacks, and predictable disposal behavior.

The library targets **.NET Standard 2.1**, with executable **.NET 10** regression checks and Linux/Windows GitHub Actions. It uses reference identity rather than a pooled type's overridden equality implementation.

## Example

```csharp
using System.Collections.Generic;
using Mobeen.Pooling;

using var pool = new ObjectPool<List<int>>(
    factory: () => new List<int>(),
    maxRetained: 8,
    reset: list => list.Clear());

using (var lease = pool.Rent())
{
    lease.Value.Add(42);
    // Work with the list. Dispose returns it to this pool.
}

using (var lease = pool.Rent())
{
    // The returned list has been cleared and can be reused.
}
```

A lease is a reference type. Disposing multiple references to the same lease returns the object once. Reading `Value` after return throws `ObjectDisposedException`.

## Lifecycle

| Situation | Result |
| --- | --- |
| Idle item available | Rent reuses it. |
| No idle item | The factory creates a new instance. |
| Normal return | Reset runs, then the item is retained if space is available. |
| Retention full | The item is passed to the cleanup callback instead of retained. |
| Reset fails | The dirty item is discarded; it cannot be rented again. |
| Factory returns null or an already owned instance | Rent throws without creating a second lease for that object. |
| Pool disposed | Idle items are cleaned up, and new rentals are rejected. |
| Existing lease returned after pool disposal | The item is cleaned up without another reset or retention. |

`MaxRetained` limits **idle items**, not simultaneous active leases. Zero is valid and disables retention. `CreatedCount`, `ActiveCount`, and `RetainedCount` expose basic diagnostics.

## Unity integration pattern

In a Unity component, a caller can supply the prefab and lifecycle callbacks:

```csharp
var pool = new ObjectPool<UnityEngine.GameObject>(
    factory: () => UnityEngine.Object.Instantiate(prefab),
    maxRetained: 16,
    reset: go => go.SetActive(false),
    destroy: go => UnityEngine.Object.Destroy(go));

using (var lease = pool.Rent())
{
    lease.Value.SetActive(true);
    // Keep the lease until this object's use is finished.
}
```

This is an integration example, not an included Unity package. Main-thread access and the lifetime of the prefab, scene objects, and leases remain the component's responsibility. Do not destroy leased objects through another owner.

## Run the checks

Install the .NET 10 SDK, then run:

```sh
dotnet build src/ObjectPool/ObjectPool.csproj --configuration Release
dotnet run --project tests/ObjectPool.Tests/ObjectPool.Tests.csproj --configuration Release
```

The standard-library test executable returns a nonzero exit code on failure. **19 checks** cover reuse, reset ordering, double disposal, capacity, active-lease disposal, factory errors, reference ownership, cleanup failures, and reentrancy.

## Failure policy and limitations

- Single-threaded. Factory and lifecycle callbacks cannot re-enter the same pool.
- A rejected nested return leaves the other lease valid for a later retry.
- Cleanup attempts every idle item even when one callback throws; errors are aggregated.
- If reset and cleanup both fail, both exceptions are preserved.
- Reset/cleanup failures remove the object from ownership before propagating the error. The caller handles any external resource that its cleanup callback failed to release.
- Leases allocate a small wrapper per rental. This is not a zero-allocation pool, and no benchmark-based speedup is claimed.
- Always return leases. There is no finalizer that automatically recovers forgotten active leases.
- Desktop .NET behavior is covered by the tests; Unity device integration needs separate validation.

This is a newly implemented portfolio utility, developed with AI assistance. The source, runnable checks, and explicit lifecycle guarantees are intended to make it straightforward to review.
