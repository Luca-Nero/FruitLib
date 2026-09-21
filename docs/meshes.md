# Meshes

`FruitMeshLibrary` loads `*_mesh.json` files embedded in your assembly into Unity
`Mesh` objects. It exists so mods can ship custom models without asset bundles.

The file schema, and how to embed one, are documented in
[`../MESH_FORMAT.md`](MESH_FORMAT.md). This page covers the API.

## Loading

One library per mod, your meshes live in your assembly, so it can't default to
FruitLib's:

```csharp
internal static FruitMeshLibrary Meshes;

public override void OnInitializeMelon()
{
    Meshes = new FruitMeshLibrary(Assembly.GetExecutingAssembly());
}
```

Everything is parsed and built up front, once. Construct it at init, not on
demand, building a mesh mid-gameplay costs a frame hitch.

The constructor takes an optional `suffix` (default `"_mesh.json"`) if you want a
different naming convention. It logs a warning and loads nothing if the assembly
has no matching resources, a common symptom of forgetting to set **Build Action:
Embedded Resource**.

## Using

```csharp
Mesh mesh = Meshes.GetMesh("C4");
var groups = Meshes.GetMaterials("C4");

FruitMeshUtil.ApplyMaterials(renderer, groups, myShader, Color.grey);
```

`GetMesh(name)` matches **case-insensitively on substring**, not exact name so
`"C4"` finds `C4_mesh`. Convenient, but it means `"Mine"` would also match
`"Landmine_mesh"`; if you have overlapping names, be specific. Returns `null` and
logs a warning if nothing matches. Passing an empty string returns the first
loaded mesh.

`GetMaterials(name)` returns the material groups, or an empty array never null.

`FruitMeshUtil.ApplyMaterials(renderer, groups, shader, fallbackColor)` handles
all three cases: multiple groups become per-submesh materials, a single group
becomes one tinted material, and no groups falls back to your colour. It creates
new `Material` instances per call, so call it once per spawned object rather than
per frame.

## Notes

- Meshes are created with `HideFlags.DontUnloadUnusedAsset`, so they survive
  scene loads. Load once at init and reuse the instances.
- Normals are always recalculated after building, so the `normals` array in the
  file is effectively advisory.
- The JSON parser is hand-rolled and deliberately minimal it reads the specific
  flat-array shape in `MESH_FORMAT.md` and nothing else. It is not a
  general-purpose JSON reader, and a file with a different structure will parse to
  garbage or fail rather than error usefully. A bundled JSON library was avoided
  on purpose: it risks version-conflicting with whatever the host game's IL2CPP
  runtime already has loaded.
