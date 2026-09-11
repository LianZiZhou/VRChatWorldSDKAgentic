---
name: vrcw-udon
description: Write UdonSharp for VRChat worlds without fighting the compiler. Use when authoring, debugging or reviewing UdonSharp (.cs files deriving from UdonSharpBehaviour), wiring behaviours into a scene, or choosing a sync strategy.
---

# UdonSharp authoring

Verified against **VRChat Worlds SDK 3.10.5 / Unity 2022.3.22f1**. Every constraint below corresponds to a
diagnostic the SDK actually emits; where a source location is cited, that is where the check lives.

## The loop

Cheapest check first. Each step rules out a class of failure the next step would pay more to discover.

1. **`udon_lint`** — syntax-level, no Unity, ~150ms. Catches most violations.
2. **`udon_whitelist_query` / `udon_syncable`** — does this API exist in Udon at all? Do not guess; Udon
   exposes a hand-curated subset of Unity and absence is a real answer.
3. **`udon_create_script`** — writes the `.cs` and creates the program asset. **Two passes:** the first
   writes the file, then Unity must compile it before the program asset can be linked. Call it again after
   `unity_compile_status` goes idle. It is idempotent; the response's `nextStep` says which pass you are on.
4. **`udon_compile`** — the real UdonSharp compiler, with structured diagnostics.
5. **`udon_attach`** then **`udon_set_fields`**.
6. **`udon_read_vars`** — confirm `mismatchCount` is 0.
7. **`verify_clientsim`** — prove the behaviour actually behaves.

## Three ordering traps

These are not style preferences; getting them wrong produces silence rather than an error.

**Unity compiles before UdonSharp can.** `UdonSharpCompilerV1` checks `DoesUnityProjectHaveCompileErrors()`
and then *returns doing nothing* (`UdonSharpCompilerV1.cs:272`). A U# compile over broken C# reports neither
success nor failure. Always check `unity_compile_status` first — `udon_compile` does this for you and reports
stage `csharp` when it applies.

**A fresh program asset is not ready.** It starts at `ScriptVersion.Unknown`, and `CopyProxyToUdon` *throws*
until the upgrade pass plus a compile have run (`UdonSharpEditorUtility.cs:1172-1176`). `udon_compile` runs
the upgrade pass; if it reports stage `upgrade`, scripts were rewritten and you must compile again.

**Setting a field on the component is not enough.** The values that ship live on a hidden backing
`UdonBehaviour`, and only `CopyProxyToUdon` moves them there. Writing the component through generic Unity
tooling loses them silently — correct in the inspector, absent at runtime. Always use `udon_set_fields`, and
verify with `udon_read_vars`.

## What UdonSharp rejects

| Construct | Why |
|---|---|
| `try` / `catch` / `finally`, `throw` | Udon has no exception handling |
| `lock`, `using` statements, `unsafe` | unsupported node types |
| iterators (`yield`), local functions | unsupported node types |
| LINQ, lambdas, anonymous methods, delegates | no delegates in Udon |
| `async` / `await`, coroutines | no scheduler; use `SendCustomEventDelayedSeconds`/`DelayedFrames` |
| `List<T>`, `Dictionary<,>`, `HashSet<T>`, `Func<>`, `Action<>` | not exposed; use arrays, or `VRC.SDK3.Data.DataDictionary` |
| multidimensional arrays `[,]` | jagged arrays are fine |
| static fields (non-`const`) | `FieldSymbol.cs:94`; `const` is fine, `readonly` does not help |
| constructors, nested types, generic methods on a behaviour | unsupported on `UdonSharpBehaviour` |
| object/collection initializer lists | assign fields separately |
| `AddListener` / `RemoveListener` | wire the UI event to `SendCustomEvent` in the inspector |

**Commonly believed to be unsupported, but fine:** properties, inheritance between UdonSharpBehaviours
(including `abstract` bases), generic methods on plain static classes, jagged arrays, `foreach` over arrays
and strings, `nameof`, recursion on `[RecursiveMethod]`.

## Sync

Ask `udon_syncable` rather than remembering. It calls `VRC.Udon.UdonNetworkTypes`, which is the authority.

Syncable: the numeric types, `bool`, `char`, `string`, `Color`, `Color32`, `Vector2/3/4`, `Quaternion`,
`VRCUrl`, and arrays of those. Notably **not** `Vector3Int`, `Matrix4x4`, `Bounds`, `Rect`, any
`UnityEngine.Object` reference, or user types.

- **Arrays require `BehaviourSyncMode.Manual`.** Continuous sync refuses them.
- `Linear`/`Smooth` interpolation only applies to Continuous, and only to some types (`Color` is linear but
  not smooth; `string` is neither).
- A `[UdonSynced]` field on a `None` or `NoVariableSync` behaviour is an error.
- All `UdonBehaviour`s on one GameObject must share a sync method.
- Budget roughly **50 bytes per behaviour**. The SDK enforces nothing here
  (`ClientSimNetworkCalling.cs:74` says `// TODO: add size limits`), so this is discipline, not a gate.

Manual sync pattern:

```csharp
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class Door : UdonSharpBehaviour
{
    [UdonSynced] public bool isOpen;

    public override void Interact()
    {
        Networking.SetOwner(Networking.LocalPlayer, gameObject);  // only the owner may serialise
        isOpen = !isOpen;
        RequestSerialization();
        _Apply();                                                 // the owner does not get OnDeserialization
    }

    public override void OnDeserialization() => _Apply();         // remotes arrive here

    private void _Apply() { /* ... */ }                           // _ prefix: not network-callable
}
```

## Network reachability

A **public, parameterless** method with no `_` prefix is a legacy network entry point: anyone in the instance
can invoke it. `udon_lint` flags these as `implicit-network-entry`. Prefix local-only methods with `_`, and
mark deliberate ones `[NetworkCallable]` — which must be public, return void, take at most 8 parameters, and
use no `ref`/`out`/`params`/defaults.

Never treat instance master as a security boundary.

## Debugging a running world

Udon **cannot send data out**: `VRCUrl` is not constructible at runtime, so there is no HTTP escape. The only
channel is `Debug.Log`. For automated verification, log tagged lines and read them back:

```csharp
Debug.Log("[AGENTTEL]{\"counter\":" + count + "}");
```

`verify_clientsim` sees them in the console; `verify_multiclient` parses them out of the real client's logs,
which requires the client to be launched with `--enable-udon-debug-logging`.

## Limits of each verification layer

- `udon_lint` — no symbol resolution, so it cannot know whether a specific API is exposed.
- `udon_compile` — authoritative about the language and the exposure whitelist; says nothing about behaviour.
- `verify_clientsim` — proves logic, single-player only. `interact` invokes the entry point directly, so it
  cannot catch a button with no collider or one behind a wall. Use `scene_spatial_audit` for that.
- `verify_multiclient` — the only layer that can prove sync, ownership and late-joiner behaviour.
