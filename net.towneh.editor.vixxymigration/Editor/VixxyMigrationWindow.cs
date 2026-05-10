using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Net.Towneh.Editor.VixxyMigration
{
    public sealed class VixxyMigrationWindow : EditorWindow
    {
        const string MenuPath = "Tools/Vixxy Animator Migration Utility";
        const string DefaultContainerName = "HVR.Vixxy";

        [Serializable]
        sealed class PathRemap
        {
            public string OriginalPath;
            public Transform Replacement;
        }

        [Serializable]
        sealed class MaterialSuffixRemap
        {
            // Source suffix from the original animator's renamed-animated property names
            // (e.g. "Jacket" from "_UDIMDiscardRow1_0_Jacket"). Read-only label in the UI.
            public string SourceSuffix;
            // User drops a Material here; the discovery layer inspects its shader for properties matching
            // the source's stripped base name and uses whichever it finds.
            public Material Replacement;
        }

        [Serializable]
        sealed class PropertyRemap
        {
            // Source full property name from the original animator (e.g. "_HueShift" — a Poi A-marked
            // property that wasn't renamed during lock). Read-only label in the UI.
            public string SourceProperty;
            // Target property name the user types — what the migration actually writes to. e.g.
            // user types "_MainHueShift" because that's what the property is called on the current avatar.
            public string TargetProperty;
        }

        [SerializeField] GameObject _avatarRoot;
        [SerializeField] AnimatorController _controller;
        [SerializeField] Transform _outputParent;
        [SerializeField] string _containerName = DefaultContainerName;
        [SerializeField] List<PathRemap> _pathRemaps = new List<PathRemap>();
        [SerializeField] List<MaterialSuffixRemap> _materialSuffixRemaps = new List<MaterialSuffixRemap>();
        [SerializeField] List<PropertyRemap> _propertyRemaps = new List<PropertyRemap>();
        [SerializeField] bool _remapsFoldout = true;
        [SerializeField] bool _materialRemapsFoldout = true;
        [SerializeField] bool _propertyRemapsFoldout = true;

        List<MigrationItem> _items = new List<MigrationItem>();
        Vector2 _scroll;

        string _statusMessage;
        MessageType _statusType = MessageType.Info;

        readonly Dictionary<MigrationCategory, bool> _foldouts = new Dictionary<MigrationCategory, bool>
        {
            { MigrationCategory.Material, true },
            { MigrationCategory.Activation, true },
            { MigrationCategory.Mixed, true },
            { MigrationCategory.Skipped, false },
        };

        [MenuItem(MenuPath)]
        public static void Open()
        {
            var w = GetWindow<VixxyMigrationWindow>("Vixxy Animator Migration Utility");
            w.minSize = new Vector2(520, 600);
        }

        void OnGUI()
        {
            DrawHeader();
            DrawRemaps();
            DrawMaterialSuffixRemaps();
            DrawPropertyRemaps();
            DrawDiscoveryList();
            DrawFooter();
        }

        void DrawHeader()
        {
            EditorGUILayout.LabelField("Avatar & source", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _avatarRoot = (GameObject)EditorGUILayout.ObjectField("Avatar Root", _avatarRoot, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
            {
                AutoResolveControllerFromAvatar();
                _outputParent = _avatarRoot != null ? _avatarRoot.transform : null;
            }

            _controller = (AnimatorController)EditorGUILayout.ObjectField("Source Animator", _controller, typeof(AnimatorController), false);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            _outputParent = (Transform)EditorGUILayout.ObjectField("Place Under", _outputParent, typeof(Transform), true);
            _containerName = EditorGUILayout.TextField("Container Name", _containerName);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_avatarRoot == null || _controller == null))
            {
                if (GUILayout.Button("Discover items"))
                {
                    _items = VixxyMigrationDiscovery.Discover(_controller, _avatarRoot);
                    // Discover() resolves targets without remaps, so any previously-assigned remap entries
                    // would otherwise be re-shown as unresolved until the user nudges a field. Apply the
                    // current remap dict immediately so re-discovery is visually consistent with prior state.
                    var remaps = BuildRemapDict();
                    var matSuffixRemaps = BuildMaterialSuffixRemapDict();
                    var propRemaps = BuildPropertyRemapDict();
                    if (remaps.Count > 0 || matSuffixRemaps.Count > 0 || propRemaps.Count > 0)
                    {
                        VixxyMigrationDiscovery.ReResolveTargets(_items, _avatarRoot, remaps, matSuffixRemaps, propRemaps);
                    }
                    SyncRemapsWithUnresolvedPaths();
                    SyncMaterialSuffixRemapsWithUnresolved();
                    SyncPropertyRemapsWithUnresolved();
                }
            }

            if (_avatarRoot != null && string.IsNullOrEmpty(_containerName))
            {
                EditorGUILayout.HelpBox("Container Name cannot be empty.", MessageType.Warning);
            }

            EditorGUILayout.HelpBox(
                "Lock all Poiyomi materials before testing — UDIM-suffixed property names (e.g. _UDIMDiscardRow0_0_Jacket) only resolve on locked materials.",
                MessageType.Info);

            EditorGUILayout.Space();
        }

        void AutoResolveControllerFromAvatar()
        {
            if (_avatarRoot == null) return;
            var animator = _avatarRoot.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.runtimeAnimatorController is AnimatorController ac)
            {
                _controller = ac;
            }
        }

        void DrawDiscoveryList()
        {
            EditorGUILayout.LabelField($"Discovered items ({_items.Count})", EditorStyles.boldLabel);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));

            DrawCategorySection(MigrationCategory.Material, "Material Properties");
            DrawCategorySection(MigrationCategory.Activation, "GameObject / Component Toggles");
            DrawCategorySection(MigrationCategory.Mixed, "Mixed — Material + Component");
            DrawCategorySection(MigrationCategory.Skipped, "Skipped");

            EditorGUILayout.EndScrollView();
        }

        void DrawCategorySection(MigrationCategory category, string label)
        {
            var inCategory = _items
                .Where(i => i.Category == category)
                .OrderBy(i => i.ParameterName, StringComparer.Ordinal)
                .ToList();
            if (inCategory.Count == 0) return;

            EditorGUILayout.BeginHorizontal();
            _foldouts[category] = EditorGUILayout.Foldout(_foldouts[category], $"{label} ({inCategory.Count})", true, EditorStyles.foldoutHeader);
            GUILayout.FlexibleSpace();

            if (category != MigrationCategory.Skipped)
            {
                if (GUILayout.Button("All", EditorStyles.miniButtonLeft, GUILayout.Width(40)))
                {
                    foreach (var i in inCategory.Where(i => i.Status != MigrationStatus.NotMigratable)) i.Selected = true;
                }
                if (GUILayout.Button("None", EditorStyles.miniButtonRight, GUILayout.Width(46)))
                {
                    foreach (var i in inCategory) i.Selected = false;
                }
            }
            EditorGUILayout.EndHorizontal();

            if (!_foldouts[category]) return;

            EditorGUI.indentLevel++;
            foreach (var item in inCategory)
            {
                DrawItemRow(item);
            }
            EditorGUI.indentLevel--;

            EditorGUILayout.Space(4);
        }

        void DrawItemRow(MigrationItem item)
        {
            EditorGUILayout.BeginHorizontal();

            using (new EditorGUI.DisabledScope(item.Status == MigrationStatus.NotMigratable))
            {
                item.Selected = EditorGUILayout.ToggleLeft(GUIContent.none, item.Selected, GUILayout.Width(24));
            }

            var icon = item.Status switch
            {
                MigrationStatus.Migratable => "",
                MigrationStatus.MigratableWithWarning => "⚠ ",
                MigrationStatus.NotMigratable => "⊘ ",
                _ => "",
            };

            EditorGUILayout.LabelField(icon + item.ParameterName, GUILayout.MinWidth(220));

            var summary = BuildSummary(item);
            EditorGUILayout.LabelField(summary, EditorStyles.miniLabel, GUILayout.MinWidth(180));

            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(item.SkipReason))
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField(item.SkipReason, EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
            }

            if (item.Warnings.Count > 0)
            {
                EditorGUI.indentLevel++;
                foreach (var w in item.Warnings)
                {
                    EditorGUILayout.LabelField("⚠ " + w, EditorStyles.miniLabel);
                }
                EditorGUI.indentLevel--;
            }
        }

        string BuildSummary(MigrationItem item)
        {
            if (item.Category == MigrationCategory.Skipped) return "";
            var sb = new StringBuilder();
            if (item.Curves.Count > 0) sb.Append($"{item.Curves.Count} curve(s)");
            if (item.Activations.Count > 0)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append($"{item.Activations.Count} activation(s)");
            }
            return sb.ToString();
        }

        void DrawFooter()
        {
            EditorGUILayout.Space();

            var selectedCount = _items.Count(i => i.Selected && i.Category != MigrationCategory.Skipped);
            var blockers = CollectActionBlockers(selectedCount);

            if (blockers.Count > 0)
            {
                EditorGUILayout.HelpBox("Cannot apply yet:\n  • " + string.Join("\n  • ", blockers), MessageType.Info);
            }
            else
            {
                EditorGUILayout.LabelField($"{selectedCount} item(s) selected", EditorStyles.miniLabel);
            }

            using (new EditorGUI.DisabledScope(blockers.Count > 0))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Dry-run preview"))
                {
                    SafeRun("Dry-run", () =>
                    {
                        var detail = BuildDryRunReport();
                        BasisDebug.Log(detail, BasisDebug.LogTag.Editor);
                        SetStatus($"Dry-run complete — {selectedCount} item(s) printed to Console.", MessageType.Info);
                    });
                }
                if (GUILayout.Button("Apply selected"))
                {
                    SafeRun("Apply", () =>
                    {
                        var count = VixxyMigrationApply.Apply(_items, _controller, _avatarRoot, _outputParent, _containerName);
                        BasisDebug.Log($"[Vixxy Animator Migration] Created {count} control(s) under '{_containerName}'.", BasisDebug.LogTag.Editor);
                        SetStatus($"Created {count} Vixxy control(s) under '{_containerName}'. Ctrl+Z reverses the whole migration.", MessageType.Info);
                    });
                }
                EditorGUILayout.EndHorizontal();
            }

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_statusMessage, _statusType);
            }
        }

        List<string> CollectActionBlockers(int selectedCount)
        {
            var issues = new List<string>();
            if (_avatarRoot == null) issues.Add("Drop an Avatar Root in the header.");
            if (_outputParent == null) issues.Add("Set the 'Place Under' Transform in the header.");
            if (string.IsNullOrEmpty(_containerName)) issues.Add("Set a Container Name in the header.");
            if (_items.Count == 0) issues.Add("Click 'Discover items' to scan the source animator.");
            else if (selectedCount == 0) issues.Add("Tick at least one item to migrate (use the 'All' button on a category to bulk-select).");
            return issues;
        }

        void SafeRun(string actionLabel, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                BasisDebug.LogError($"[Vixxy Animator Migration] {actionLabel} failed: {ex}", BasisDebug.LogTag.Editor);
                SetStatus($"{actionLabel} failed: {ex.Message}\nSee Console for full stack trace.", MessageType.Error);
            }
        }

        void SetStatus(string message, MessageType type)
        {
            _statusMessage = message;
            _statusType = type;
        }

        string BuildDryRunReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Vixxy Animator Migration] Dry-run preview:");
            foreach (var item in _items.Where(i => i.Selected && i.Category != MigrationCategory.Skipped))
            {
                sb.Append("  ").Append(item.ParameterName).Append("  →  ");
                sb.Append(item.Curves.Count).Append(" curve(s)");
                if (item.Activations.Count > 0) sb.Append(", ").Append(item.Activations.Count).Append(" activation(s)");
                sb.AppendLine();
                foreach (var c in item.Curves)
                {
                    sb.Append("      curve: ").Append(c.GameObjectPath).Append(" / ").Append(c.PropertyName)
                      .Append("  [").Append(c.OffValue).Append(" → ").Append(c.OnValue).Append("]");
                    if (c.TargetMissing) sb.Append("  ⚠ target missing");
                    sb.AppendLine();
                }
                foreach (var a in item.Activations)
                {
                    sb.Append("      activation: ").Append(a.OriginalGameObjectPath).Append(" / ").Append(a.OriginalComponentTypeName)
                      .Append("  [").Append(a.OffActive).Append(" → ").Append(a.OnActive).Append("]");
                    if (!string.IsNullOrEmpty(a.ResolutionNote)) sb.Append("  — ").Append(a.ResolutionNote);
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        void DrawRemaps()
        {
            // _pathRemaps accumulates every user-assigned remap and is the source of truth fed into ReResolveTargets.
            // Visible rows are filtered to the currently-unresolved paths; assigned remaps that have done their job
            // are kept in _pathRemaps so they continue to apply, but hidden from the UI to keep the list focused.
            var unresolvedSet = new HashSet<string>(VixxyMigrationDiscovery.CollectUnresolvedPaths(_items), StringComparer.Ordinal);
            var visibleRemaps = _pathRemaps.Where(r => unresolvedSet.Contains(r.OriginalPath)).ToList();
            var activeRemaps = _pathRemaps.Where(r => r.Replacement != null && !unresolvedSet.Contains(r.OriginalPath)).ToList();

            if (visibleRemaps.Count == 0 && activeRemaps.Count == 0) return;

            EditorGUILayout.BeginHorizontal();
            var headerLabel = activeRemaps.Count > 0
                ? $"Path Remappings ({visibleRemaps.Count} unresolved, {activeRemaps.Count} applied)"
                : $"Path Remappings ({visibleRemaps.Count})";
            _remapsFoldout = EditorGUILayout.Foldout(_remapsFoldout, headerLabel, true, EditorStyles.foldoutHeader);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (!_remapsFoldout)
            {
                EditorGUILayout.Space();
                return;
            }

            EditorGUILayout.HelpBox(
                "Original GameObject paths from the source animator that don't resolve on this avatar. Drag the replacement Transform onto a row to remap; targets re-resolve automatically.",
                MessageType.None);

            bool changed = false;

            EditorGUI.indentLevel++;
            foreach (var remap in visibleRemaps)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(remap.OriginalPath, GUILayout.MinWidth(200));
                EditorGUILayout.LabelField("→", GUILayout.Width(16));

                EditorGUI.BeginChangeCheck();
                remap.Replacement = (Transform)EditorGUILayout.ObjectField(remap.Replacement, typeof(Transform), allowSceneObjects: true);
                if (EditorGUI.EndChangeCheck()) changed = true;

                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.indentLevel--;

            if (activeRemaps.Count > 0)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Applied (resolving — clear to re-edit):", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                foreach (var remap in activeRemaps)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(remap.OriginalPath, EditorStyles.miniLabel, GUILayout.MinWidth(200));
                    EditorGUILayout.LabelField("→", GUILayout.Width(16));

                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField(remap.Replacement, typeof(Transform), allowSceneObjects: true);
                    }
                    if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(48)))
                    {
                        remap.Replacement = null;
                        changed = true;
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUI.indentLevel--;
            }

            if (changed)
            {
                VixxyMigrationDiscovery.ReResolveTargets(_items, _avatarRoot, BuildRemapDict(), BuildMaterialSuffixRemapDict(), BuildPropertyRemapDict());
                SyncMaterialSuffixRemapsWithUnresolved();
                SyncRemapsWithUnresolvedPaths();
                SyncPropertyRemapsWithUnresolved();
            }

            EditorGUILayout.Space();
        }

        void SyncRemapsWithUnresolvedPaths()
        {
            // _pathRemaps is the accumulated dictionary of every user-assigned remap. It must NOT lose entries
            // just because they currently happen to resolve — dropping them would also drop the mapping that
            // made them resolve, and the next ReResolveTargets pass would see them go unresolved again.
            //
            // So: add new entries for newly-unresolved paths, but only prune entries that have never been
            // assigned (Replacement == null) and are no longer unresolved.
            var unresolved = VixxyMigrationDiscovery.CollectUnresolvedPaths(_items);
            var unresolvedSet = new HashSet<string>(unresolved, StringComparer.Ordinal);
            var tracked = new HashSet<string>(_pathRemaps.Select(r => r.OriginalPath), StringComparer.Ordinal);

            foreach (var p in unresolved)
            {
                if (!tracked.Contains(p))
                {
                    _pathRemaps.Add(new PathRemap { OriginalPath = p, Replacement = null });
                }
            }

            _pathRemaps.RemoveAll(r => r.Replacement == null && !unresolvedSet.Contains(r.OriginalPath));
        }

        Dictionary<string, Transform> BuildRemapDict()
        {
            var dict = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (var r in _pathRemaps)
            {
                if (!string.IsNullOrEmpty(r.OriginalPath) && r.Replacement != null)
                    dict[r.OriginalPath] = r.Replacement;
            }
            return dict;
        }

        void DrawMaterialSuffixRemaps()
        {
            // Mirror of DrawRemaps for the material-suffix case. The accumulated suffix list is the source
            // of truth; visible rows are the subset that's still currently unresolved on the items.
            var unresolvedSet = new HashSet<string>(VixxyMigrationDiscovery.CollectUnresolvedMaterialSuffixes(_items), StringComparer.Ordinal);
            var visible = _materialSuffixRemaps.Where(r => unresolvedSet.Contains(r.SourceSuffix)).ToList();
            var active = _materialSuffixRemaps.Where(r => r.Replacement != null && !unresolvedSet.Contains(r.SourceSuffix)).ToList();

            if (visible.Count == 0 && active.Count == 0) return;

            EditorGUILayout.BeginHorizontal();
            var headerLabel = active.Count > 0
                ? $"Material Suffix Remappings ({visible.Count} unresolved, {active.Count} applied)"
                : $"Material Suffix Remappings ({visible.Count})";
            _materialRemapsFoldout = EditorGUILayout.Foldout(_materialRemapsFoldout, headerLabel, true, EditorStyles.foldoutHeader);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (!_materialRemapsFoldout)
            {
                EditorGUILayout.Space();
                return;
            }

            EditorGUILayout.HelpBox(
                "Source-animator property names that end in a suffix (e.g. '_Jacket' in '_UDIMDiscardRow0_0_Jacket') which doesn't exist on the target material's shader. Drop a Material from the renderer's slots — the migration will use that Material's name as the new suffix and re-resolve. One mapping fixes every property with the same source suffix.",
                MessageType.None);

            bool changed = false;

            EditorGUI.indentLevel++;
            foreach (var remap in visible)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("_" + remap.SourceSuffix, GUILayout.MinWidth(160));
                EditorGUILayout.LabelField("→", GUILayout.Width(16));

                EditorGUI.BeginChangeCheck();
                remap.Replacement = (Material)EditorGUILayout.ObjectField(remap.Replacement, typeof(Material), allowSceneObjects: false);
                if (EditorGUI.EndChangeCheck()) changed = true;

                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.indentLevel--;

            if (active.Count > 0)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Applied (resolving — clear to re-edit):", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                foreach (var remap in active)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("_" + remap.SourceSuffix + "  →  _" + (remap.Replacement != null ? remap.Replacement.name : "<none>"),
                        EditorStyles.miniLabel, GUILayout.MinWidth(200));

                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ObjectField(remap.Replacement, typeof(Material), allowSceneObjects: false);
                    }
                    if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(48)))
                    {
                        remap.Replacement = null;
                        changed = true;
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUI.indentLevel--;
            }

            if (changed)
            {
                VixxyMigrationDiscovery.ReResolveTargets(_items, _avatarRoot, BuildRemapDict(), BuildMaterialSuffixRemapDict(), BuildPropertyRemapDict());
                SyncMaterialSuffixRemapsWithUnresolved();
                SyncRemapsWithUnresolvedPaths();
                SyncPropertyRemapsWithUnresolved();
            }

            EditorGUILayout.Space();
        }

        void SyncMaterialSuffixRemapsWithUnresolved()
        {
            // Add new entries for newly-unresolved suffixes; preserve assigned entries even after they
            // resolve (they keep working in the background); only prune empty entries that no longer
            // appear in the unresolved set. Same logic as path remaps.
            var unresolved = VixxyMigrationDiscovery.CollectUnresolvedMaterialSuffixes(_items);
            var unresolvedSet = new HashSet<string>(unresolved, StringComparer.Ordinal);
            var tracked = new HashSet<string>(_materialSuffixRemaps.Select(r => r.SourceSuffix), StringComparer.Ordinal);

            foreach (var s in unresolved)
            {
                if (!tracked.Contains(s))
                {
                    _materialSuffixRemaps.Add(new MaterialSuffixRemap { SourceSuffix = s, Replacement = null });
                }
            }

            _materialSuffixRemaps.RemoveAll(r => r.Replacement == null && !unresolvedSet.Contains(r.SourceSuffix));
        }

        Dictionary<string, Material> BuildMaterialSuffixRemapDict()
        {
            // Map source suffix → user-dropped Material. The discovery layer inspects the material's
            // shader for properties matching the source's base name pattern and uses whichever it finds —
            // smarter than blindly assuming Material.name is the new suffix (which fails when the
            // material name and the lock-renamed property suffix don't align, e.g. Material "Hair" with
            // property "_MainHueShift_HairLight").
            var dict = new Dictionary<string, Material>(StringComparer.Ordinal);
            foreach (var r in _materialSuffixRemaps)
            {
                if (string.IsNullOrEmpty(r.SourceSuffix)) continue;
                if (r.Replacement == null) continue;
                dict[r.SourceSuffix] = r.Replacement;
            }
            return dict;
        }

        void DrawPropertyRemaps()
        {
            // Per-property full-name override UI. Used for Poi A-marked source properties (no strippable
            // suffix) whose name on the user's avatar is just different from what the source animator
            // wrote to (e.g. source "_HueShift", target "_MainHueShift").
            var unresolvedSet = new HashSet<string>(VixxyMigrationDiscovery.CollectUnresolvedPropertyNames(_items), StringComparer.Ordinal);
            var visible = _propertyRemaps.Where(r => unresolvedSet.Contains(r.SourceProperty)).ToList();
            var active = _propertyRemaps.Where(r => !string.IsNullOrEmpty(r.TargetProperty) && !unresolvedSet.Contains(r.SourceProperty)).ToList();

            if (visible.Count == 0 && active.Count == 0) return;

            EditorGUILayout.BeginHorizontal();
            var headerLabel = active.Count > 0
                ? $"Property Remappings ({visible.Count} unresolved, {active.Count} applied)"
                : $"Property Remappings ({visible.Count})";
            _propertyRemapsFoldout = EditorGUILayout.Foldout(_propertyRemapsFoldout, headerLabel, true, EditorStyles.foldoutHeader);
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();

            if (!_propertyRemapsFoldout)
            {
                EditorGUILayout.Space();
                return;
            }

            EditorGUILayout.HelpBox(
                "Source-animator property names that don't exist on the target material's shader. Type the actual property name as declared on the current material — useful when the property has been renamed, when the shader differs from the one the source animator was authored against, or when the source name simply doesn't exist on the current shader.",
                MessageType.None);

            bool changed = false;

            EditorGUI.indentLevel++;
            foreach (var remap in visible)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(remap.SourceProperty, GUILayout.MinWidth(160));
                EditorGUILayout.LabelField("→", GUILayout.Width(16));

                EditorGUI.BeginChangeCheck();
                remap.TargetProperty = EditorGUILayout.TextField(remap.TargetProperty);
                if (EditorGUI.EndChangeCheck()) changed = true;

                EditorGUILayout.EndHorizontal();
            }
            EditorGUI.indentLevel--;

            if (active.Count > 0)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Applied (resolving — clear to re-edit):", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                foreach (var remap in active)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(remap.SourceProperty + "  →  " + remap.TargetProperty,
                        EditorStyles.miniLabel, GUILayout.MinWidth(280));

                    if (GUILayout.Button("Clear", EditorStyles.miniButton, GUILayout.Width(48)))
                    {
                        remap.TargetProperty = null;
                        changed = true;
                    }
                    EditorGUILayout.EndHorizontal();
                }
                EditorGUI.indentLevel--;
            }

            if (changed)
            {
                VixxyMigrationDiscovery.ReResolveTargets(_items, _avatarRoot, BuildRemapDict(), BuildMaterialSuffixRemapDict(), BuildPropertyRemapDict());
                SyncPropertyRemapsWithUnresolved();
                SyncMaterialSuffixRemapsWithUnresolved();
                SyncRemapsWithUnresolvedPaths();
            }

            EditorGUILayout.Space();
        }

        void SyncPropertyRemapsWithUnresolved()
        {
            // Same accumulating-list pattern as the path/suffix remap UIs: add new rows for newly-
            // unresolved property names; preserve assigned rows even after they resolve so they keep
            // working in the background; only prune empty rows that are no longer unresolved.
            var unresolved = VixxyMigrationDiscovery.CollectUnresolvedPropertyNames(_items);
            var unresolvedSet = new HashSet<string>(unresolved, StringComparer.Ordinal);
            var tracked = new HashSet<string>(_propertyRemaps.Select(r => r.SourceProperty), StringComparer.Ordinal);

            foreach (var p in unresolved)
            {
                if (!tracked.Contains(p))
                {
                    _propertyRemaps.Add(new PropertyRemap { SourceProperty = p, TargetProperty = null });
                }
            }

            _propertyRemaps.RemoveAll(r => string.IsNullOrEmpty(r.TargetProperty) && !unresolvedSet.Contains(r.SourceProperty));
        }

        Dictionary<string, string> BuildPropertyRemapDict()
        {
            // Map source full property name → target full property name. Discovery uses this as a direct
            // override before any suffix logic.
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in _propertyRemaps)
            {
                if (string.IsNullOrEmpty(r.SourceProperty)) continue;
                if (string.IsNullOrEmpty(r.TargetProperty)) continue;
                dict[r.SourceProperty] = r.TargetProperty;
            }
            return dict;
        }
    }
}
