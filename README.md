# source2vrchat

Turn Garry's Mod / Source Filmmaker player models into VRChat avatars.

Source models don't work flawlessly in Unity. The skeleton uses Valve bone names, the hips and legs
point the wrong way for VRChat's checks, some textures never get converted, procedural helper
bones freeze, and eye tracking rolls the pupils out of view. This repo fixes those in two steps:

1. **`gma2blend`** (Blender): unpacks a `.gma` addon and writes a clean `.blend`, an FBX,
   PNG textures and a `report.json`.
2. **Source Avatar Setup** (Unity editor window): reads that report and sets up the avatar.

## Requirements

- Blender with [SourceIO](https://github.com/REDxEYE/SourceIO) (tested on Blender 5.2)
- Unity 2022.3 with the VRChat Avatars SDK 3.7 or newer, for VRChat constraints (tested on 3.10)
- [lilToon](https://github.com/lilxyzw/lilToon) (optional; materials fall back to Standard)

## 1. Convert the addon

Drag a `.gma` file (or an extracted addon folder) onto `gma2blend.bat`. The output lands in
`<addon>_converted` next to it. If Blender isn't found, set the `BLENDER` environment variable to
`blender.exe`.

From a terminal:

```
blender -b --factory-startup -P blender/gma2blend.py -- addon.gma out_dir [options]
```

| Option | Effect |
| --- | --- |
| `--all-models` | Also convert `c_arms` and viewmodel models |
| `--keep-outline` | Keep inverted-hull outline geometry |
| `--no-fbx` | Write only `.blend` files |
| `--no-legfix` | Leave hip and limb bones as they are |

What it does:

- Extracts the GMAD archive. No Garry's Mod tools are needed.
- Imports each player model with SourceIO.
- Saves every texture as PNG, including those behind shaders SourceIO doesn't support.
- Renames `ValveBiped.*` and SFM `bip_*` bones to Unity humanoid names.
- Repairs shape-key names that were saved with the wrong encoding.
- Moves the hips to thigh height and aims hips, legs and arms along the limbs. VRChat checks the
  thigh bones' rotation against the hips and warns below 175°.
- Removes inverted-hull outline faces, since a toon shader draws outlines better.

SourceIO prints a few tracebacks for shaders it can't build (`MonitorScreen` and others) and one
when Blender exits. They are harmless: the textures are still converted.

## 2. Set up the avatar in Unity

1. Copy `unity/Editor/SourceAvatarSetup.cs` into an `Editor` folder in your project.
2. Copy the converted folder (FBX, `Textures`, `report.json`) into `Assets`.
3. Drag the FBX into the scene and add a VRC Avatar Descriptor.
4. Open **Tools > Source Avatar Setup**, pick the avatar and `report.json`, then press **Run**.

| Step | Why |
| --- | --- |
| Humanoid import, no blend shape normals | Blend shape normals bloat the mesh, especially for Quest |
| Remove a Jaw mapped to a hair bone | Unity's auto-mapper often picks a front hair strand |
| Materials matched by triangle count | Unity can reverse submesh order compared to Blender |
| Eye look rotations | Blank rotations roll Source eye bones and hide the pupils |
| Constraints on helper bones | Source drives elbow, knee and twist helpers procedurally; in Unity they freeze and the joints collapse |
| Hands and feet first in the hierarchy | VRChat expects the next limb bone to be the first child |
| Colliders moved out of PhysBone roots | A collider under the bones its PhysBone moves causes a cyclic dependency |

To edit bones, the tool unpacks the model prefab in the scene. After that, re-exporting the FBX
updates meshes but not the scene's bone transforms, so run the tool on a fresh instance instead.

Things still left to you: PhysBones, expressions, visemes and toggles for bodygroup variants
(each variant is its own mesh).

## License

MIT. SourceIO, lilToon and the VRChat SDK keep their own licenses. Make sure you are allowed to
use the models you convert.
