using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Net.Towneh.Editor.VixxyMigration
{
    public static class VixxyMigrationDiscovery
    {
        const string MaterialBindingPrefix = "material.";
        const float DirectBlendTreeOnThreshold = 1f;

        static readonly HashSet<string> ExplicitlySkippedParameters = new HashSet<string>
        {
            "_DBTNormalize",
            "IsLoaded",
            "IsLocal",
            "GestureLeft",
            "GestureRight",
            "GestureLeftWeight",
            "EyeTracking",
            "_Boop",
            "_PatPat",
            "_EarPull_IsGrabbed",
            "_EarPull_Stretch",
            "_TailPull_IsGrabbed",
            "_TailPull_Stretch",
        };

        static readonly string[] SkippedParameterPrefixes =
        {
            "Settings/Idles/",
            "Settings/Gestures",
            "Settings/Contacts",
            "Settings/EyeSqueeze",
            "Settings/Respectfully",
        };

        sealed class GatedClip
        {
            public AnimationClip Clip;
            public float Threshold;
        }

        public static List<MigrationItem> Discover(AnimatorController controller, GameObject avatarRoot)
        {
            var items = new List<MigrationItem>();
            if (controller == null) return items;

            var paramToClips = new Dictionary<string, List<GatedClip>>(StringComparer.Ordinal);

            foreach (var layer in controller.layers)
            {
                if (layer.stateMachine != null)
                {
                    WalkStateMachine(layer.stateMachine, gatingParam: null, paramToClips);
                }
            }

            foreach (var param in controller.parameters)
            {
                items.Add(BuildItem(param, paramToClips, avatarRoot));
            }

            return items;
        }

        static void WalkStateMachine(AnimatorStateMachine sm, string gatingParam, Dictionary<string, List<GatedClip>> sink)
        {
            foreach (var s in sm.states)
            {
                if (s.state != null && s.state.motion != null)
                {
                    WalkMotion(s.state.motion, gatingParam, sink);
                }
            }
            foreach (var ssm in sm.stateMachines)
            {
                if (ssm.stateMachine != null)
                {
                    WalkStateMachine(ssm.stateMachine, gatingParam, sink);
                }
            }
        }

        static void WalkMotion(Motion motion, string gatingParam, Dictionary<string, List<GatedClip>> sink)
        {
            switch (motion)
            {
                case AnimationClip clip when !string.IsNullOrEmpty(gatingParam):
                    AddGatedClip(sink, gatingParam, clip, DirectBlendTreeOnThreshold);
                    break;
                case BlendTree bt:
                    WalkBlendTree(bt, gatingParam, sink);
                    break;
            }
        }

        static void WalkBlendTree(BlendTree bt, string outerGating, Dictionary<string, List<GatedClip>> sink)
        {
            if (bt.blendType == BlendTreeType.Direct)
            {
                foreach (var child in bt.children)
                {
                    var childGating = !string.IsNullOrEmpty(child.directBlendParameter) ? child.directBlendParameter : outerGating;
                    if (child.motion is AnimationClip clip && !string.IsNullOrEmpty(childGating))
                    {
                        AddGatedClip(sink, childGating, clip, DirectBlendTreeOnThreshold);
                    }
                    else if (child.motion is BlendTree subBt)
                    {
                        WalkBlendTree(subBt, childGating, sink);
                    }
                }
            }
            else if (bt.blendType == BlendTreeType.Simple1D)
            {
                var p = bt.blendParameter;
                foreach (var child in bt.children)
                {
                    if (child.motion is AnimationClip clip && !string.IsNullOrEmpty(p))
                    {
                        AddGatedClip(sink, p, clip, child.threshold);
                    }
                    else if (child.motion is BlendTree subBt)
                    {
                        WalkBlendTree(subBt, p, sink);
                    }
                }
            }
        }

        static void AddGatedClip(Dictionary<string, List<GatedClip>> sink, string param, AnimationClip clip, float threshold)
        {
            if (clip == null) return;
            if (!sink.TryGetValue(param, out var list))
            {
                list = new List<GatedClip>();
                sink[param] = list;
            }
            list.Add(new GatedClip { Clip = clip, Threshold = threshold });
        }

        static MigrationItem BuildItem(AnimatorControllerParameter param, Dictionary<string, List<GatedClip>> paramToClips, GameObject avatarRoot)
        {
            var item = new MigrationItem
            {
                ParameterName = param.name,
                Selected = false
            };

            if (ExplicitlySkippedParameters.Contains(param.name) || SkippedParameterPrefixes.Any(p => param.name.StartsWith(p, StringComparison.Ordinal)))
            {
                item.Category = MigrationCategory.Skipped;
                item.Status = MigrationStatus.NotMigratable;
                item.SkipReason = "Internal animator parameter or gesture/sub-state-machine driver";
                return item;
            }

            if (!paramToClips.TryGetValue(param.name, out var clips) || clips.Count == 0)
            {
                item.Category = MigrationCategory.Skipped;
                item.Status = MigrationStatus.NotMigratable;
                item.SkipReason = "Parameter is not gated to any clip in this controller";
                return item;
            }

            ExtractCurvesAndActivations(item, clips, avatarRoot);

            if (item.Curves.Count == 0 && item.Activations.Count == 0)
            {
                item.Category = MigrationCategory.Skipped;
                item.Status = MigrationStatus.NotMigratable;
                item.SkipReason = "Clips reference no migratable properties (no material curves, m_Enabled, or m_IsActive)";
                return item;
            }

            // Category reflects what Vixxy will actually emit, not how the source author named the parameter.
            if (item.Curves.Count > 0 && item.Activations.Count > 0)
            {
                item.Category = MigrationCategory.Mixed;
            }
            else if (item.Curves.Count > 0)
            {
                item.Category = MigrationCategory.Material;
            }
            else
            {
                item.Category = MigrationCategory.Activation;
            }

            // ResolveTargets has already set item.Status based on warnings; just decide the initial selection state.
            item.Selected = item.Status == MigrationStatus.Migratable;
            return item;
        }

        static void ExtractCurvesAndActivations(MigrationItem item, List<GatedClip> clips, GameObject avatarRoot)
        {
            var ordered = clips.OrderBy(c => c.Threshold).ToList();
            var lowClip = ordered.First();
            var highClip = ordered.Last();

            item.OffValue = lowClip.Threshold;
            item.OnValue = highClip.Threshold;

            var curveAggregate = new Dictionary<string, MigrationCurve>(StringComparer.Ordinal);
            var activationAggregate = new Dictionary<string, MigrationActivation>(StringComparer.Ordinal);

            ConsumeClipEndpoints(lowClip.Clip, isHighEndpoint: false, item, curveAggregate, activationAggregate, avatarRoot);

            if (highClip.Clip != lowClip.Clip)
            {
                ConsumeClipEndpoints(highClip.Clip, isHighEndpoint: true, item, curveAggregate, activationAggregate, avatarRoot);
            }
            else
            {
                ConsumeSingleClipEndpoint(lowClip.Clip, item, curveAggregate, activationAggregate);
            }

            item.Curves.AddRange(curveAggregate.Values);
            item.Activations.AddRange(activationAggregate.Values);

            ResolveTargets(item, avatarRoot);
        }

        static void ConsumeClipEndpoints(AnimationClip clip, bool isHighEndpoint, MigrationItem item,
            Dictionary<string, MigrationCurve> curveAggregate,
            Dictionary<string, MigrationActivation> activationAggregate,
            GameObject avatarRoot)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0) continue;

                var endValue = curve.keys[curve.length - 1].value;

                if (binding.propertyName.StartsWith(MaterialBindingPrefix, StringComparison.Ordinal))
                {
                    var shaderProp = StripMaterialPrefixAndUnderlyingMaterialIndex(binding.propertyName);
                    var key = binding.path + "::" + shaderProp + "::" + binding.type.FullName;
                    if (!curveAggregate.TryGetValue(key, out var mc))
                    {
                        mc = new MigrationCurve
                        {
                            GameObjectPath = binding.path,
                            PropertyName = shaderProp,
                            OriginalPropertyName = shaderProp,
                            ComponentTypeFullName = binding.type.FullName,
                            OffValue = endValue,
                            OnValue = endValue
                        };
                        curveAggregate[key] = mc;
                    }
                    if (isHighEndpoint) mc.OnValue = endValue;
                    else mc.OffValue = endValue;
                }
                else if (binding.propertyName == "m_Enabled" || binding.propertyName == "m_IsActive")
                {
                    var key = binding.path + "::" + binding.type.FullName + "::" + binding.propertyName;
                    if (!activationAggregate.TryGetValue(key, out var ma))
                    {
                        ma = new MigrationActivation
                        {
                            OriginalGameObjectPath = binding.path,
                            OriginalComponentTypeName = binding.type.FullName,
                            OffActive = endValue >= 0.5f,
                            OnActive = endValue >= 0.5f
                        };
                        activationAggregate[key] = ma;
                    }
                    if (isHighEndpoint) ma.OnActive = endValue >= 0.5f;
                    else ma.OffActive = endValue >= 0.5f;
                }
            }
        }

        static void ConsumeSingleClipEndpoint(AnimationClip clip, MigrationItem item,
            Dictionary<string, MigrationCurve> curveAggregate,
            Dictionary<string, MigrationActivation> activationAggregate)
        {
            // When only one clip gates the parameter, we treat the clip's first keyframe as the OFF state
            // and its last keyframe as the ON state. This matches the 2-keyframe-step convention used for
            // toggle clips in FX layouts.
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                if (curve == null || curve.length == 0) continue;

                var firstValue = curve.keys[0].value;
                var lastValue = curve.keys[curve.length - 1].value;

                if (binding.propertyName.StartsWith(MaterialBindingPrefix, StringComparison.Ordinal))
                {
                    var shaderProp = StripMaterialPrefixAndUnderlyingMaterialIndex(binding.propertyName);
                    var key = binding.path + "::" + shaderProp + "::" + binding.type.FullName;
                    if (curveAggregate.TryGetValue(key, out var mc))
                    {
                        mc.OffValue = firstValue;
                        mc.OnValue = lastValue;
                    }
                }
                else if (binding.propertyName == "m_Enabled" || binding.propertyName == "m_IsActive")
                {
                    var key = binding.path + "::" + binding.type.FullName + "::" + binding.propertyName;
                    if (activationAggregate.TryGetValue(key, out var ma))
                    {
                        ma.OffActive = firstValue >= 0.5f;
                        ma.OnActive = lastValue >= 0.5f;
                    }
                }
            }
        }

        static string StripMaterialPrefixAndUnderlyingMaterialIndex(string raw)
        {
            // Animator material bindings come through as "material.<propName>" or "material[<index>].<propName>".
            // Vixxy writes via Shader.PropertyToID(propertyName) on a MaterialPropertyBlock applied to the whole
            // renderer, so any per-slot index is irrelevant to us — strip both forms.
            var trimmed = raw.Substring(MaterialBindingPrefix.Length);
            if (trimmed.Length > 0 && trimmed[0] == '[')
            {
                var dotIdx = trimmed.IndexOf('.');
                if (dotIdx >= 0) trimmed = trimmed.Substring(dotIdx + 1);
            }
            return trimmed;
        }

        static void ResolvePropertyNameOnRenderer(MigrationItem item, MigrationCurve curve, IReadOnlyDictionary<string, Material> materialSuffixRemaps, IReadOnlyDictionary<string, string> propertyRemaps)
        {
            // Reset to the original source-animator name on every call so resolution is idempotent —
            // ResolveTargets runs from both Discover (initial) and ReResolveTargets (after the user
            // changes a remap), and we need the source name to be the input each time, not whatever the
            // previous pass mutated PropertyName into.
            if (!string.IsNullOrEmpty(curve.OriginalPropertyName))
                curve.PropertyName = curve.OriginalPropertyName;

            // Step 1: source name as-is. Universal — works on any shader. HasProperty is what
            // MaterialPropertyBlock.SetFloat actually queries at write time.
            if (RendererHasMaterialProperty(curve.ResolvedRenderer, curve.PropertyName)) return;

            // Step 1.5: per-property full-name override. Universal — works on any shader. Wins over
            // suffix-based remaps because it's the most specific signal: the user typed an exact target.
            if (propertyRemaps != null && propertyRemaps.TryGetValue(curve.OriginalPropertyName, out var propertyOverride) && !string.IsNullOrEmpty(propertyOverride))
            {
                if (RendererHasMaterialProperty(curve.ResolvedRenderer, propertyOverride))
                {
                    item.Warnings.Add($"Remapped material property '{curve.OriginalPropertyName}' → '{propertyOverride}' on '{curve.ResolvedRenderer.name}' (full-name override from Property Remappings).");
                    curve.PropertyName = propertyOverride;
                    return;
                }
                item.Warnings.Add($"Property override '{curve.OriginalPropertyName}' → '{propertyOverride}' but '{propertyOverride}' isn't declared on any of '{curve.ResolvedRenderer.name}'s materials. Type a different target name.");
                return;
            }

            // Steps 2 & 3 are Poi-specific (suffix-renamed properties + Poi StringTagMap markers). lilToon,
            // Silent's Filamented, standard PBR shaders etc. don't rename animated properties at lock time,
            // so the suffix-stripping heuristic would just produce false-positive "needs remapping" rows.
            // Skip the suffix paths entirely on non-Poi renderers.
            var isPoi = RendererUsesPoiyomiShader(curve.ResolvedRenderer);
            var stripped = isPoi ? StripTrailingAlphabeticSuffix(curve.PropertyName) : null;
            string sourceSuffix = null;

            if (isPoi)
            {
                if (stripped != null)
                {
                    sourceSuffix = curve.PropertyName.Substring(stripped.Length + 1);

                    // Step 2: user-dropped Material with shader inspection.
                    if (materialSuffixRemaps != null && materialSuffixRemaps.TryGetValue(sourceSuffix, out var droppedMaterial) && droppedMaterial != null && droppedMaterial.shader != null)
                    {
                        var resolved = FindBaseMatchingPropertyOnMaterial(droppedMaterial, stripped);
                        if (resolved != null)
                        {
                            if (RendererHasMaterialProperty(curve.ResolvedRenderer, resolved))
                            {
                                item.Warnings.Add($"Remapped material property '{curve.OriginalPropertyName}' → '{resolved}' on '{curve.ResolvedRenderer.name}' (inspected Material '{droppedMaterial.name}' for properties matching '{stripped}_').");
                                curve.PropertyName = resolved;
                                return;
                            }
                            item.Warnings.Add($"Property '{resolved}' was found on Material '{droppedMaterial.name}' but isn't reachable on the renderer '{curve.ResolvedRenderer.name}' — is that material assigned to one of the renderer's slots?");
                            return;
                        }
                        item.Warnings.Add($"Material '{droppedMaterial.name}' has no shader properties matching base '{stripped}_'. Drop a different Material that exposes a '{stripped}_<name>' property.");
                        return;
                    }

                    // Step 3: StringTagMap auto-detect (Poi-specific).
                    var autoMatch = TryAutoResolveFromStringTagMap(curve.ResolvedRenderer, stripped, curve.OriginalPropertyName, out var matchedFromMaterial);
                    if (autoMatch != null && RendererHasMaterialProperty(curve.ResolvedRenderer, autoMatch))
                    {
                        item.Warnings.Add($"Auto-resolved '{curve.OriginalPropertyName}' → '{autoMatch}' on '{curve.ResolvedRenderer.name}' (Poi 'Animated' tag on Material '{matchedFromMaterial}' was the only candidate sharing the source's base '{stripped}_'). Override via Material Suffix Remappings or Property Remappings if this isn't right.");
                        curve.PropertyName = autoMatch;
                        return;
                    }
                }
            }

            // Step 4: surface as unresolved. The route depends on whether the renderer is Poi:
            //  - Poi + has strippable suffix → Material Suffix Remappings UI (drop a Material)
            //  - Poi + no strippable suffix → Property Remappings UI (type the target)
            //  - non-Poi → always Property Remappings (no suffix concept on non-Poi shaders)
            var shaderHint = curve.ResolvedRenderer.sharedMaterial != null && curve.ResolvedRenderer.sharedMaterial.shader != null
                ? curve.ResolvedRenderer.sharedMaterial.shader.name
                : "<unknown>";

            if (!isPoi)
            {
                item.Warnings.Add($"Material property '{curve.OriginalPropertyName}' not found on '{curve.ResolvedRenderer.name}' (shader '{shaderHint}' isn't Poiyomi — suffix-rename logic doesn't apply). Type the actual target property name in the Property Remappings UI.");
            }
            else if (sourceSuffix != null)
            {
                item.Warnings.Add($"Material property '{curve.OriginalPropertyName}' not found on '{curve.ResolvedRenderer.name}' (suffix '{sourceSuffix}' has no equivalent, and Poi StringTagMap auto-detect found no unique match). Drop a Material onto the suffix row in the Material Suffix Remappings UI.");
            }
            else
            {
                item.Warnings.Add($"Material property '{curve.OriginalPropertyName}' not found on '{curve.ResolvedRenderer.name}' (no strippable suffix; Poi A-marked source whose name has changed on this avatar). Type the actual target property name in the Property Remappings UI.");
            }
        }

        static bool RendererUsesPoiyomiShader(Renderer renderer)
        {
            // Permissive substring match against shader names — covers `.poiyomi/Poiyomi Toon`,
            // `Poiyomi/Toon`, `Hidden/Locked/.poiyomi/Poiyomi Toon/<hash>` (locked variant), and any
            // forks/community variants that keep "Poiyomi" in the name. Returns true if any of the
            // renderer's materials' shaders match — a multi-material renderer with one Poi material is
            // still treated as Poi for resolution purposes.
            if (renderer == null) return false;
            var materials = renderer.sharedMaterials;
            if (materials == null) return false;
            foreach (var mat in materials)
            {
                if (mat == null || mat.shader == null) continue;
                if (mat.shader.name.IndexOf("poiyomi", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        static string TryAutoResolveFromStringTagMap(Renderer renderer, string strippedBase, string sourcePropertyName, out string matchedMaterialName)
        {
            // Walk all the renderer's materials' StringTagMap entries (Poi writes a `<actualName>Animated`
            // tag per animated property at lock time). Collect the unique candidates whose actual name
            // matches the source's stripped base (either as an exact un-suffixed match or with a purely
            // alphabetic "_<word>" suffix). Return the candidate IFF it's the only one; ambiguous cases
            // (e.g. both "_MainHueShift" and "_MainHueShift_HairLight" present) deliberately don't auto-pick.
            matchedMaterialName = null;
            if (renderer == null || string.IsNullOrEmpty(strippedBase)) return null;

            var materials = renderer.sharedMaterials;
            if (materials == null) return null;

            var candidates = new Dictionary<string, string>(StringComparer.Ordinal); // candidate property name → material name
            var prefix = strippedBase + "_";

            foreach (var mat in materials)
            {
                if (mat == null) continue;
                foreach (var animatedProp in EnumerateAnimatedPropertyNamesViaStringTagMap(mat))
                {
                    if (animatedProp == strippedBase)
                    {
                        if (!candidates.ContainsKey(animatedProp)) candidates[animatedProp] = mat.name;
                    }
                    else if (animatedProp.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        var tail = animatedProp.Substring(prefix.Length);
                        if (IsAllLetters(tail) && !candidates.ContainsKey(animatedProp))
                            candidates[animatedProp] = mat.name;
                    }
                }
            }

            // If the source name already resolves directly, we wouldn't be here — but defensively skip a
            // candidate that exactly matches the source name to avoid no-op self-remaps.
            var nonSelfCandidates = candidates.Where(kvp => kvp.Key != sourcePropertyName).ToList();
            if (nonSelfCandidates.Count == 1)
            {
                matchedMaterialName = nonSelfCandidates[0].Value;
                return nonSelfCandidates[0].Key;
            }
            return null;
        }

        static List<string> EnumerateAnimatedPropertyNamesViaStringTagMap(Material material)
        {
            // Read the material's m_SavedProperties.m_StringTagMap via SerializedObject (Unity's public
            // Material API has no GetAllTagNames). Each entry whose key ends in "Animated" is a Poi
            // marker for an animated property; the actual property name is the key minus the "Animated"
            // suffix. Returns the list of stripped property names.
            var result = new List<string>();
            if (material == null) return result;

            SerializedProperty stringTagMap;
            try
            {
                var so = new SerializedObject(material);
                stringTagMap = so.FindProperty("m_SavedProperties.m_StringTagMap");
            }
            catch
            {
                return result;
            }
            if (stringTagMap == null || !stringTagMap.isArray) return result;

            const string animatedSuffix = "Animated";
            for (int i = 0; i < stringTagMap.arraySize; i++)
            {
                var element = stringTagMap.GetArrayElementAtIndex(i);
                if (element == null) continue;
                var keyProp = element.FindPropertyRelative("first");
                if (keyProp == null || keyProp.propertyType != SerializedPropertyType.String) continue;
                var tagName = keyProp.stringValue;
                if (string.IsNullOrEmpty(tagName) || tagName.Length <= animatedSuffix.Length) continue;
                if (!tagName.EndsWith(animatedSuffix, StringComparison.Ordinal)) continue;
                result.Add(tagName.Substring(0, tagName.Length - animatedSuffix.Length));
            }
            return result;
        }

        static bool IsAllLetters(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var c in s) if (!char.IsLetter(c)) return false;
            return true;
        }

        static string FindBaseMatchingPropertyOnMaterial(Material material, string strippedBase)
        {
            // Enumerate the material's shader's declared properties; return the first one whose name is
            // either an exact match for strippedBase (un-suffixed case) or matches "<strippedBase>_<word>"
            // where <word> is purely alphabetic (the Poi rename-animated pattern). Prefer suffixed matches
            // over un-suffixed when both exist, because suffixed forms are more likely to be the
            // user-targeted lock-renamed variant.
            if (material == null || material.shader == null) return null;
            int count;
            try { count = material.shader.GetPropertyCount(); }
            catch { return null; }

            string suffixedMatch = null;
            string unsuffixedMatch = null;
            var prefix = strippedBase + "_";

            for (int i = 0; i < count; i++)
            {
                var name = material.shader.GetPropertyName(i);
                if (name == strippedBase)
                {
                    unsuffixedMatch = name;
                    continue;
                }
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (IsAllLetters(name.Substring(prefix.Length)))
                {
                    if (suffixedMatch == null) suffixedMatch = name;
                    // Don't break — keep looking. (If the material had multiple suffixed matches, we take
                    // the first deterministically; v1 doesn't disambiguate further.)
                }
            }

            return suffixedMatch ?? unsuffixedMatch;
        }

        static bool RendererHasMaterialProperty(Renderer renderer, string propertyName)
        {
            var materials = renderer.sharedMaterials;
            if (materials == null) return false;
            foreach (var mat in materials)
            {
                if (mat == null) continue;
                if (mat.HasProperty(propertyName)) return true;
            }
            return false;
        }

        static string StripTrailingAlphabeticSuffix(string propertyName)
        {
            if (string.IsNullOrEmpty(propertyName)) return null;

            var lastUnderscore = propertyName.LastIndexOf('_');
            if (lastUnderscore <= 0 || lastUnderscore == propertyName.Length - 1) return null;

            var suffix = propertyName.Substring(lastUnderscore + 1);
            foreach (var c in suffix)
            {
                if (!char.IsLetter(c)) return null;
            }
            return propertyName.Substring(0, lastUnderscore);
        }

        static void ResolveTargets(MigrationItem item, GameObject avatarRoot, IReadOnlyDictionary<string, Transform> remaps = null, IReadOnlyDictionary<string, Material> materialSuffixRemaps = null, IReadOnlyDictionary<string, string> propertyRemaps = null)
        {
            if (avatarRoot == null) return;
            var rootTransform = avatarRoot.transform;

            item.Warnings.Clear();

            foreach (var curve in item.Curves)
            {
                curve.ResolvedRenderer = null;
                curve.TargetMissing = false;

                var targetTf = ResolvePathWithRemaps(rootTransform, curve.GameObjectPath, remaps);
                if (targetTf == null)
                {
                    curve.TargetMissing = true;
                    item.Warnings.Add($"GameObject '{curve.GameObjectPath}' not found on avatar (curve: {curve.PropertyName})");
                    continue;
                }

                if (!targetTf.TryGetComponent<Renderer>(out var renderer))
                {
                    curve.TargetMissing = true;
                    item.Warnings.Add($"No Renderer at '{curve.GameObjectPath}' → {GetRelativePath(rootTransform, targetTf)} (curve: {curve.PropertyName})");
                    continue;
                }

                curve.ResolvedRenderer = renderer;
                ResolvePropertyNameOnRenderer(item, curve, materialSuffixRemaps, propertyRemaps);
            }

            foreach (var activation in item.Activations)
            {
                activation.ResolvedComponent = null;
                activation.TargetMissing = false;
                activation.ResolutionNote = null;

                var targetTf = ResolvePathWithRemaps(rootTransform, activation.OriginalGameObjectPath, remaps);
                if (targetTf == null)
                {
                    activation.TargetMissing = true;
                    activation.ResolutionNote = $"GameObject '{activation.OriginalGameObjectPath}' not found on avatar";
                    item.Warnings.Add(activation.ResolutionNote);
                    continue;
                }

                if (activation.OriginalComponentTypeName == typeof(GameObject).FullName)
                {
                    activation.ResolvedComponent = targetTf;
                    activation.ResolutionNote = "Resolved to Transform (GameObject toggle)";
                    continue;
                }

                // Component lookup by full type name. Unity has no `TryGetComponent(string, out)` overload —
                // the typed `TryGetComponent<T>` and `TryGetComponent(Type, out)` variants exist but neither
                // accepts a string. The animator binding gives us the type as a string (which may name a
                // type that doesn't exist in this project, e.g. VRChat's VRCPhysBone on a Basis avatar), so
                // string-by-name is the natural lookup. One-shot per item, never on a per-frame path.
                var directComponent = targetTf.GetComponent(activation.OriginalComponentTypeName);
                if (directComponent != null)
                {
                    activation.ResolvedComponent = directComponent;
                    activation.ResolutionNote = $"Resolved to {activation.OriginalComponentTypeName}";
                    continue;
                }

                var jiggleType = ResolveJigglePhysicsType();
                if (jiggleType != null && targetTf.TryGetComponent(jiggleType, out var jiggleComp))
                {
                    activation.ResolvedComponent = jiggleComp;
                    activation.ResolutionNote = $"Mapped {activation.OriginalComponentTypeName} → JiggleRig";
                    continue;
                }

                activation.TargetMissing = true;
                activation.ResolutionNote = $"No {activation.OriginalComponentTypeName} or JiggleRig at '{activation.OriginalGameObjectPath}'";
                item.Warnings.Add(activation.ResolutionNote);
            }

            item.Status = item.Warnings.Count > 0 ? MigrationStatus.MigratableWithWarning : MigrationStatus.Migratable;
        }

        public static void ReResolveTargets(IEnumerable<MigrationItem> items, GameObject avatarRoot, IReadOnlyDictionary<string, Transform> remaps, IReadOnlyDictionary<string, Material> materialSuffixRemaps = null, IReadOnlyDictionary<string, string> propertyRemaps = null)
        {
            if (items == null || avatarRoot == null) return;
            foreach (var item in items)
            {
                if (item.Category == MigrationCategory.Skipped) continue;
                ResolveTargets(item, avatarRoot, remaps, materialSuffixRemaps, propertyRemaps);
            }
        }

        public static List<string> CollectUnresolvedMaterialSuffixes(IEnumerable<MigrationItem> items)
        {
            // Returns the unique set of source-suffix strings (e.g. "Jacket", "Eyes") whose curves have a
            // ResolvedRenderer but whose property names still don't resolve to anything that material
            // declares. The window uses this to populate the Material Suffix Remappings UI rows.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>();
            if (items == null) return ordered;

            foreach (var item in items)
            {
                if (item.Category == MigrationCategory.Skipped) continue;
                foreach (var c in item.Curves)
                {
                    if (c.ResolvedRenderer == null) continue;
                    if (string.IsNullOrEmpty(c.OriginalPropertyName)) continue;

                    // The property is unresolved if the current effective name doesn't actually exist on
                    // the renderer's materials.
                    if (RendererHasMaterialProperty(c.ResolvedRenderer, c.PropertyName)) continue;

                    // Material Suffix Remappings is a Poi-specific concept (suffix-rename at lock time).
                    // Skip non-Poi renderers — their unresolved properties go through the universal
                    // Property Remappings flow instead.
                    if (!RendererUsesPoiyomiShader(c.ResolvedRenderer)) continue;

                    // Pull the original suffix from the unmodified source name; that's the key the user
                    // would target in the remap UI. Only suffix-based properties surface here — flat A
                    // properties (no strippable suffix) go to CollectUnresolvedPropertyNames instead.
                    var stripped = StripTrailingAlphabeticSuffix(c.OriginalPropertyName);
                    if (stripped == null) continue;
                    var suffix = c.OriginalPropertyName.Substring(stripped.Length + 1);
                    if (seen.Add(suffix)) ordered.Add(suffix);
                }
            }
            return ordered;
        }

        public static List<string> CollectUnresolvedPropertyNames(IEnumerable<MigrationItem> items)
        {
            // Returns the unique set of source full property names whose curves are unresolved AND have no
            // strippable suffix — i.e. flat Poi A-marked properties whose name on the user's avatar isn't
            // what the source animator wrote to. The window uses this to populate the Property Remappings
            // UI rows where the user types the actual target name.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>();
            if (items == null) return ordered;

            foreach (var item in items)
            {
                if (item.Category == MigrationCategory.Skipped) continue;
                foreach (var c in item.Curves)
                {
                    if (c.ResolvedRenderer == null) continue;
                    if (string.IsNullOrEmpty(c.OriginalPropertyName)) continue;
                    if (RendererHasMaterialProperty(c.ResolvedRenderer, c.PropertyName)) continue;

                    // For Poi renderers, suffixed names belong to the Material Suffix Remappings UI
                    // instead — only flat (no strippable suffix) names surface here. For non-Poi renderers,
                    // the suffix UI is hidden, so all unresolved property names need to flow through the
                    // universal Property Remappings UI regardless of name shape.
                    if (RendererUsesPoiyomiShader(c.ResolvedRenderer))
                    {
                        if (StripTrailingAlphabeticSuffix(c.OriginalPropertyName) != null) continue;
                    }

                    if (seen.Add(c.OriginalPropertyName)) ordered.Add(c.OriginalPropertyName);
                }
            }
            return ordered;
        }

        public static List<string> CollectUnresolvedPaths(IEnumerable<MigrationItem> items)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var ordered = new List<string>();
            if (items == null) return ordered;

            foreach (var item in items)
            {
                if (item.Category == MigrationCategory.Skipped) continue;
                foreach (var c in item.Curves)
                {
                    if (c.TargetMissing && !string.IsNullOrEmpty(c.GameObjectPath) && seen.Add(c.GameObjectPath))
                        ordered.Add(c.GameObjectPath);
                }
                foreach (var a in item.Activations)
                {
                    if (a.TargetMissing && !string.IsNullOrEmpty(a.OriginalGameObjectPath) && seen.Add(a.OriginalGameObjectPath))
                        ordered.Add(a.OriginalGameObjectPath);
                }
            }
            return ordered;
        }

        static Transform ResolvePathWithRemaps(Transform root, string originalPath, IReadOnlyDictionary<string, Transform> remaps)
        {
            if (string.IsNullOrEmpty(originalPath)) return root;

            if (remaps != null && remaps.Count > 0)
            {
                if (remaps.TryGetValue(originalPath, out var exact) && exact != null)
                    return exact;

                // Longest prefix wins: a remap of "Physics/PhysBones" should override one of "Physics" if both apply.
                Transform bestMatch = null;
                int bestPrefixLen = -1;
                foreach (var kvp in remaps)
                {
                    if (kvp.Value == null || string.IsNullOrEmpty(kvp.Key)) continue;
                    var prefix = kvp.Key + "/";
                    if (originalPath.StartsWith(prefix, StringComparison.Ordinal) && kvp.Key.Length > bestPrefixLen)
                    {
                        var suffix = originalPath.Substring(prefix.Length);
                        var resolved = kvp.Value.Find(suffix);
                        if (resolved != null)
                        {
                            bestMatch = resolved;
                            bestPrefixLen = kvp.Key.Length;
                        }
                    }
                }
                if (bestMatch != null) return bestMatch;
            }

            return root.Find(originalPath);
        }

        static string GetRelativePath(Transform root, Transform target)
        {
            if (target == null || root == null) return "";
            if (target == root) return "";
            var stack = new List<string>();
            var current = target;
            while (current != null && current != root)
            {
                stack.Add(current.name);
                current = current.parent;
            }
            stack.Reverse();
            return string.Join("/", stack);
        }

        static Type _cachedJiggleRigType;
        static bool _jiggleResolutionAttempted;

        static Type ResolveJigglePhysicsType()
        {
            if (_jiggleResolutionAttempted) return _cachedJiggleRigType;
            _jiggleResolutionAttempted = true;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("GatorDragonGames.JigglePhysics.JiggleRig");
                if (t != null)
                {
                    _cachedJiggleRigType = t;
                    return t;
                }
            }
            return null;
        }
    }
}
