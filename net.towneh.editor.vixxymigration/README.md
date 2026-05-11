# Vixxy Animator Migration Utility

Editor tool for Basis avatars. Migrates animator-driven toggles, cosmetic sliders, and lighting settings from a source `AnimatorController` (typically a VRChat-style FX outfit controller) into networked [Vixxy](https://docs.hai-vr.dev/docs/basis/avatar-customization/vixxy) controls.

Authored by [towneh](https://github.com/towneh). Open via **Tools ▸ Vixxy Animator Migration Utility**.

## Quick start

1. Lock all Poiyomi materials on the avatar — the tool emits suffixed property names (`_UDIMDiscardRow0_0_Jacket`) that only resolve on locked materials.
2. Drag the avatar root into **Avatar Root**. The Source Animator auto-resolves from the first child `Animator`.
3. Click **Discover items** to populate Material Properties / GameObject / Component Toggles / Mixed / Skipped foldouts.
4. If the avatar has been renamed (e.g. body mesh renamed since the source animator was authored), drop replacement Transforms onto rows in **Path Remappings**. For Poi-locked materials whose properties were lock-renamed (`_Jacket` → `_JacketLight`), drop a Material onto the **Material Suffix Remappings** row. For un-suffixed property mismatches (e.g. source `_HueShift`, target `_MainHueShift`), type the actual name in **Property Remappings**. Targets re-resolve in place after every change.
5. Tick what to migrate (per-category **All** / **None** for bulk selection).
6. **Dry-run preview** logs the planned writes to the Console; **Apply selected** authors the controls. Single **Ctrl+Z** reverses the migration.

## What the tool authors

For each migrated parameter, one `HVRVixxyControl` + `HVRVixxyMenuItem` pair under a chosen container, with the parameter path preserved in the GameObject hierarchy:

```
<Container>/Toggles_Jacket_Main                   ← flat, name encodes full path
   ├── HVRVixxyMenuItem
   └── HVRVixxyControl
         └── subjects → HVRVixxyPropertyFloat per material curve
              (variant: MaterialProperty, propertyName: "_UDIMDiscardRow0_0_Jacket")
```

`m_Enabled` curves originally targeting VRChat's `VRCPhysBone` are mapped to [JigglePhysics](https://github.com/naelstrof/JigglePhysics) `JiggleRig` components at the same path when present. Without JigglePhysics the activation is skipped with a warning.

The orchestrator and address fields are left blank; Vixxy resolves both at runtime.

## Requirements

- `dev.hai-vr.basis.comms` — Vixxy itself.
- `com.basis.sdk` — transitive via Vixxy; provides `BasisDebug` for Console logging.
- Optional: `com.gator-dragon-games.jigglephysics` for `JiggleRig` activation mapping.

## Skipped parameters

The tool intentionally does not migrate:

- Internal animator drivers (`IsLoaded`, `IsLocal`).
- Gesture-driven parameters (`GestureLeft`, `GestureRight`, `EyeTracking`, etc.).
- Sub-state-machines.
- Animator-state speed parameters (no Vixxy equivalent).
- Multi-state interconnected clothing logic.

These appear in the **Skipped** foldout with a per-item reason; author them manually if needed.

## Notes

- 5-step blend trees collapse to a 2-choice control using endpoint clip values. Intermediate clip side-effects are not preserved.
- Every migrated control is authored with neutral `presentation = Default` and empty choice labels. There's no fully-reliable way to distinguish a binary toggle from a continuous slider when both have 2-keyframe `0`→`1` source curves, so the tool leaves the rendering decision and choice naming to the user; flip per-item via the Inspector after migration if you want explicit Slider presentation or named choice labels.
- The tool does not verify material lock state. Unlocked Poi materials will cause writes to silently no-op at runtime.
- The window provides inline status feedback after each action in addition to Console output via `BasisDebug.Log` with `LogTag.Editor`.

## Acknowledgments

Built on top of [Hai's Vixxy](https://docs.hai-vr.dev/docs/basis/avatar-customization/vixxy) for [BasisVR](https://github.com/BasisVR) avatars; uses `BasisDebug` from `com.basis.sdk` for tagged logging. JiggleRig activation mapping uses [Naelstrof's JigglePhysics](https://github.com/naelstrof/JigglePhysics). Window UX inspired by [Thryrallo's VRC Avatar Performance Tools](https://github.com/Thryrallo/VRC-Avatar-Performance-Tools). Designed against [Poiyomi](https://www.poiyomi.com/) shader property conventions.

## License

MIT. See [LICENSE](LICENSE).
