# FruitLib mesh format

> Older path. New models should ship as [AssetBundles](BUNDLES.md); see [meshes.md](meshes.md).

`FruitLib.FruitMeshLibrary` loads embedded `*_mesh.json` resources into Unity
`Mesh` objects. This is the schema it expects, and how to embed and load one from
your own mod.

## Schema

```json
{
  "verts":   [[x, y, z], [x, y, z], ...],
  "normals": [[x, y, z], ...],
  "uvs":     [[u, v], ...],
  "tris":    [0, 1, 2, 0, 2, 3, ...],
  "materials": [
    { "name": "GreenBody", "kd": [0.29, 0.33, 0.13], "alpha": 1.0, "triStart": 0, "triCount": 45594 }
  ]
}
```

- `verts` -> required. Flat list of `[x, y, z]` vertex positions.
- `normals` -> optional (`[]` is fine). If omitted, `Mesh.RecalculateNormals()` fills it in.
- `uvs` -> optional (`[]` is fine).
- `tris` -> required. Flat triangle index list (three indices per triangle).
- `materials` -> optional. If present, splits the mesh into submeshes:
  - `name` -> display name, informational only.
  - `kd` -> `[r, g, b]` diffuse colour (same meaning as an OBJ/MTL file's `Kd` directive -> these files are typically exported straight from an `.obj`/`.mtl` pair).
  - `alpha` -> 0–1, defaults to 1.0 if omitted.
  - `triStart` / `triCount` -> this submesh's slice of the flat `tris` array (`triCount` counts indices, i.e. 3× the triangle count, not triangles).

If `materials` is absent or empty, the mesh loads as a single submesh with no
material data, callers are expected to supply a fallback colour/material
themselves (see `FruitMeshUtil.ApplyMaterials` below).

If `materials` has exactly one entry, no submesh split happens (same result as
omitting it) the single colour is just handed back for you to apply.

## Embedding a mesh in your mod

1. Put your file at `<YourMod>/Meshes/Thing_mesh.json` (the `Meshes/` folder isn't
   required by the loader, it's just convention, see `1_BombsAway/Meshes/` for
   examples, but the file must end in `_mesh.json`).
2. Reference it in your `.csproj`:
   ```xml
   <ItemGroup>
     <None Remove="Meshes\Thing_mesh.json" />
   </ItemGroup>
   <ItemGroup>
     <EmbeddedResource Include="Meshes\Thing_mesh.json" />
   </ItemGroup>
   ```

## Loading

```csharp
using FruitLib;
using System.Reflection;

private static FruitMeshLibrary _meshes;

public override void OnInitializeMelon()
{
    _meshes = new FruitMeshLibrary(Assembly.GetExecutingAssembly());
}

// later, when spawning:
var mesh = _meshes.GetMesh("Thing");                 // case-insensitive substring match
var materialGroups = _meshes.GetMaterials("Thing");  // empty array if none

var mf = obj.AddComponent<MeshFilter>();
mf.mesh = mesh;

var mr = obj.AddComponent<MeshRenderer>();
FruitMeshUtil.ApplyMaterials(mr, materialGroups, myShader, fallbackColor: Color.grey);
```

`GetMesh`/`GetMaterials` match by substring (case-insensitive) against the
resource's stripped filename, a resource embedded as `Meshes/C4_mesh.json`
resolves to the name `C4_mesh`, so `GetMesh("C4")` finds it. Construct one
`FruitMeshLibrary` per mod (each mod's meshes live in its own assembly, so the
library can't default to `FruitLib`'s own assembly so always pass
`Assembly.GetExecutingAssembly()` from your own mod code).

## Why not a real JSON/YAML library

`Il2CppNewtonsoft.Json` (the game's own copy) is referenced in `Directory.Build.props`
but deliberately unused anywhere in FruitLib, and bundling a JSON/YAML library into a
MelonLoader mod risks version-conflicting with whatever copy the host game's IL2CPP
runtime already has loaded. `FruitMeshLibrary` uses a small hand-rolled parser instead, scoped to
exactly this schema. If you need a JSON format `FruitMeshLibrary` doesn't cover,
extend `FruitMeshJson` in `FruitMeshLoader.cs` rather than pulling in a general
parser.
