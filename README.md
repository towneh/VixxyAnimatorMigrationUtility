# Vixxy Animator Migration Utility

A Unity Editor tool that migrates avatar toggles, cosmetic sliders, and lighting settings from a source `AnimatorController` (typically a VRChat-style FX-layer outfit controller) into networked [Vixxy](https://docs.hai-vr.dev/docs/basis/avatar-customization/vixxy) controls on a Basis avatar.

The repo distributes a single Unity package, `net.towneh.editor.vixxymigration`, located in the subfolder of the same name. Authored by [towneh](https://github.com/towneh).

## What it does

Scans a `AnimatorController`, identifies the `AnimationClip`s gated by each parameter, and authors a matching `HVRVixxyControl` + `HVRVixxyMenuItem` pair per parameter on your Basis avatar. Categorises items by what they actuate:

- **Material Properties** — items whose curves only write to material properties (UDIM tile discard, hue shifts, brightness, etc.)
- **GameObject / Component Toggles** — items whose curves only toggle `m_Enabled` on components or `m_IsActive` on GameObjects
- **Mixed** — items doing both (e.g. a Shoes toggle that hides the mesh AND disables a JiggleRig)
- **Skipped** — items the tool can't migrate (internal animator parameters like `_DBTNormalize`, gesture-driven layers, sub-state-machines, etc.); listed with a per-item reason

`m_Enabled` curves originally targeting VRChat's `VRCPhysBone` resolve to [JigglePhysics](https://github.com/naelstrof/JigglePhysics) `JiggleRig` components on the same GameObject path when present.

## Requirements

- Unity 2022.3+ (matches the Basis-supported range)
- [Basis SDK](https://github.com/BasisVR/Basis) — the platform this tool targets
- [`dev.hai-vr.basis.comms`](https://github.com/hai-vr/basis-comms) — provides Vixxy itself
- *Optional:* [`com.gator-dragon-games.jigglephysics`](https://github.com/naelstrof/JigglePhysics) — only needed if your avatar has JigglePhysics components you want toggleable

## Installation

Add the package via Unity Package Manager from the git URL:

```
https://github.com/towneh/VixxyAnimatorMigrationUtility.git?path=net.towneh.editor.vixxymigration
```

Or, for local development, add a `file:` reference to your project's `Packages/manifest.json`:

```json
{
  "dependencies": {
    "net.towneh.editor.vixxymigration": "file:../../path/to/VixxyAnimatorMigrationUtility/net.towneh.editor.vixxymigration"
  }
}
```

## Workflow

### Step 0 — Lock your Poiyomi materials

Before running the tool, lock all Poiyomi materials on your avatar. The tool emits property names in Poi's "Rename Animated" suffixed form (e.g. `_UDIMDiscardRow0_0_Jacket`), which only resolve on locked materials. If you skip this, the tool will still run, but writes to those properties will silently no-op at runtime.

<img width="485" height="160" alt="image" src="https://github.com/user-attachments/assets/8f001803-7edd-4d2e-8125-b3673138f7be" />


### Step 1 — Open the migration window

Open **Tools ▸ Vixxy Animator Migration Utility** in Unity's menu bar.

<img width="281" height="107" alt="image" src="https://github.com/user-attachments/assets/8c358db5-b240-43e0-b787-26574b07a10d" />


The window opens with all fields blank, ready to receive an avatar.

<img width="1065" height="793" alt="image" src="https://github.com/user-attachments/assets/78e6e262-dbf3-4537-9dce-828c3fdd3724" />


### Step 2 — Drop your avatar in

Drag the avatar root GameObject (a scene object, not a prefab asset) into the **Avatar Root** field. The **Source Animator** field auto-resolves from the first child `Animator` it finds; override it if your avatar has multiple animators.

**Place Under** defaults to the avatar root — this is the parent transform the migrated controls will be placed beneath. **Container Name** defaults to `HVR.Vixxy` — the GameObject that'll hold all migrated controls.

<img width="758" height="294" alt="{A98C2FA4-7063-488C-9533-8600FC387682}" src="https://github.com/user-attachments/assets/7648fb89-38c3-42de-9036-031e923a60c9" />


### Step 3 — Click **Discover items**

The tool walks the source animator and groups discovered parameters into the four foldouts described in [What it does](#what-it-does). Each row shows the parameter name, a per-item summary (curve and activation counts), and any warnings about unresolved targets.

![Discovery results, with parameters categorised into foldouts](docs/images/04-discovery-results.png)
<!-- IMAGE NEEDED: Migration window after clicking Discover items. Show all four category foldouts populated — Material Properties, GameObject / Component Toggles, Mixed, Skipped. Some items should be ticked (no warnings) and some unticked with warnings displayed inline. The window should reflect a real avatar with realistic counts. -->

### Step 4 — Resolve mismatches *(if any)*

If your avatar has been renamed since the source animator was authored, the tool surfaces the mismatches in three remapping sections that appear above the discovery list. Each section handles a different *kind* of mismatch.

#### Path Remappings — for renamed GameObjects

If the source animator references a GameObject path that doesn't exist on your avatar (e.g. the body mesh was renamed from `CHaracter` to `Body`), each unresolved path appears as a row with a Transform field. Drop the replacement Transform from your Hierarchy onto the field; targets re-resolve in place. Longest-prefix substitution rewrites nested paths automatically — one entry for `CHaracter` fixes `CHaracter`, `CHaracter/SubMesh`, etc.

<img width="1065" height="263" alt="{11D51960-D55A-4DE2-8D79-A77DC2D6ABA1}" src="https://github.com/user-attachments/assets/51f57207-0233-47de-8ca0-3e2afa50ae55" />


#### Material Suffix Remappings — for renamed Poiyomi materials *(Poi only)*

If a Poiyomi material's lock-rename suffix doesn't match the source (e.g. source `_UDIMDiscardRow1_0_Jacket` but your avatar has `_UDIMDiscardRow1_0_JacketLight`), drop the *actual* Material onto the row. The tool inspects that material's shader for properties matching the source's base name and uses whichever it finds — Material name and property suffix don't have to align.

This section appears only when at least one of your avatar's renderers uses a Poiyomi shader. The tool also tries StringTagMap auto-resolve before asking you: if there's exactly one matching candidate property under the source's base name, it auto-applies and surfaces the decision in the per-item warning.

<img width="1056" height="114" alt="{D049FB61-B659-4A49-9F2C-FB2800C6CBAC}" src="https://github.com/user-attachments/assets/57f1bc0a-00f2-490b-aaa6-903676c5627a" />


#### Property Remappings — for property names that don't fit the suffix pattern

If the source name is flat (no suffix to strip — e.g. source `_HueShift` but your shader has `_MainHueShift`), type the actual target property name into the text field. Use this for any unresolved property that doesn't fit the Poi suffix-rename pattern, including all unresolved properties on non-Poiyomi shaders.

<img width="1064" height="122" alt="{E8D1D6C2-F42A-48CE-9398-BE267A3EE8FF}" src="https://github.com/user-attachments/assets/323e0f70-0d7a-48c4-9c23-10eff5a2e282" />


After every change in any remap section, the tool re-resolves and surfaces the result in each item's warning list. Items that resolve drop their warnings; items that still don't resolve get an updated warning telling you which UI to look at next.

### Step 5 — Tick what to migrate

Each category has **All** and **None** bulk-select buttons. Items without warnings are pre-ticked by default; items with warnings start unticked so you can decide whether to migrate them anyway.

<img width="1064" height="294" alt="{52FCF700-8294-4A3C-AEEA-8341D15B1A5C}" src="https://github.com/user-attachments/assets/3a16a319-d5a7-4ae6-85fe-3f8b6c2cb315" />


### Step 6 — Dry-run preview *(optional)*

Click **Dry-run preview** to log the planned writes to the Console without modifying the scene. Each migrated parameter is listed with the curves and activations it would write, the GameObject path it'd land at, and the off/on values it'd use.

<img width="435" height="357" alt="{A2CEEC93-D27B-44FE-974B-13BC3B142FFA}" src="https://github.com/user-attachments/assets/f2fbcbf5-283e-401c-a12a-c4f694e75d9d" />


### Step 7 — Apply

Click **Apply selected**. The tool creates the container GameObject (default `HVR.Vixxy`) under your chosen parent and adds one child per migrated parameter, each carrying an `HVRVixxyMenuItem` and an `HVRVixxyControl`.

A status line below the buttons confirms the count. Single **Ctrl+Z** reverses the entire migration.

<img width="344" height="579" alt="{44AE85C3-3A5E-4891-B0FC-B371F87D9244}" src="https://github.com/user-attachments/assets/eaace6da-f209-483d-b9d8-97f826eb58df" />


### Step 8 — Adjust per-item presentation *(optional)*

The tool authors every control with neutral defaults: `presentation = Default` and empty choice labels. There is no fully-reliable way to distinguish a binary toggle from a continuous slider when both have 2-keyframe `0`→`1` source curves, so the tool leaves these decisions to you.

For continuous controls (hue, brightness, etc.), select the GameObject and switch **Presentation** to **Slider** in the `HVRVixxyMenuItem` Inspector. Binary toggles can stay on **Default** — the menu UI renders 2-choice 0/1 controls as switches automatically. To add labelled choices ("Off"/"On", "Hidden"/"Visible", etc.), set **Title Selection** to **UseCustomTitleAndChoices** and edit the per-choice titles on the `HVRVixxyControl`.

<img width="496" height="788" alt="{BF156E47-4CA1-4985-8B1E-58022F77DE11}" src="https://github.com/user-attachments/assets/225c9897-df60-449c-be6c-aa9a7c0b6c97" />


### Step 9 — Test in Play mode

Enter Play mode (or Basis's local-test flow). Open the avatar customization menu and verify each migrated control behaves as expected.

<img width="998" height="668" alt="{8214A1E5-0EA6-41FF-B48E-4E3BB8A1C8F1}" src="https://github.com/user-attachments/assets/e93f6ed5-5e8b-46ce-9f00-d7383ab4deb5" />


## Output shape

For a parameter like `Toggles/Jacket/Main`, the tool creates:

```
<Container>                                            ← user-supplied name, no prefix
├── Toggles_Jacket_Main                                ← leaf GameObject (flat, full path encoded)
│   ├── HVRVixxyMenuItem                               ← title = "Jacket Main" (category stripped, slashes→spaces)
│   └── HVRVixxyControl
│       ├── choices = [{value: 0}, {value: 1}]
│       ├── defaultValue = (animator parameter default)
│       ├── networked = true
│       ├── subjects[] = one per target Renderer
│       │     └── properties[] = HVRVixxyPropertyFloat per material curve
│       │           ├── variant = MaterialProperty
│       │           ├── propertyName = "_UDIMDiscardRow0_0_Jacket"
│       │           └── choices = [offValue, onValue]
│       └── activations[] = JiggleRig toggles (when present)
├── Toggles_Shorts_Main                                ← same shape; leaf name disambiguates
├── Cosmetics_Eye_Hue                                  ← same shape regardless of intent
└── Settings_Brightness
```

The hierarchy is flat by design: each leaf's GameObject name encodes the full parameter path with `/` and spaces collapsed to `_`. This guarantees uniqueness for the BasisAvatar duplicate-name validator while keeping the container itself (default `HVR.Vixxy`) as the namespace.

The `orchestrator` and `address` fields on each control are left blank — Vixxy resolves the orchestrator via `VixxySetup.EnsureInitialized` at runtime and auto-generates the address from the GameObject path, matching the standard Vixxy authoring convention.

## Limitations

- Animator-state speed parameters (e.g. `Settings/Idles/Tail Wag Speed`) are skipped. Vixxy has no equivalent surface for runtime animation speed control.
- Sub-state-machine transitions and gesture-driven layers (`GestureLeft`, `GestureRight`, `EyeTracking`, etc.) are skipped.
- Multi-state NSFW logic (e.g. `Toggles/Extras/*` interconnected clothing states) is skipped — author manually if needed.
- 5-step blend trees (e.g. the `Settings/Brightness` 5-point variant) collapse to a 2-choice control mapping endpoint to endpoint. Intermediate-clip side effects are lost in the simplification.
- The tool does not verify Poi material lock state. If materials are unlocked when the tool runs, the suffixed property writes will silently no-op at runtime.

## Acknowledgments

- [Hai (HVR)](https://docs.hai-vr.dev/) for [Vixxy](https://docs.hai-vr.dev/docs/basis/avatar-customization/vixxy) and the `dev.hai-vr.basis.comms` package — the entire authoring system this tool migrates animator state into. There is no tool without it.
- [BasisVR](https://github.com/BasisVR) for the [Basis](https://github.com/BasisVR/Basis) avatar platform this tool targets, and for the `BasisDebug` logger used for tagged Console output.
- [Naelstrof](https://github.com/naelstrof) for [JigglePhysics](https://github.com/naelstrof/JigglePhysics), whose `JiggleRig.enabled` contract makes the `VRCPhysBone` → `JiggleRig` activation mapping a one-line substitution rather than a special case.
- [Thryrallo](https://github.com/Thryrallo) for [VRC Avatar Performance Tools](https://github.com/Thryrallo/VRC-Avatar-Performance-Tools) — UX inspiration for the drop-target plus categorised checkbox-list `EditorWindow` shape.
- [Poiyomi](https://www.poiyomi.com/) for the shader whose UDIM tile-discard property naming conventions the tool emits.

## License

MIT. See [LICENSE.md](LICENSE.md).
