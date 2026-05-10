using System;
using System.Collections.Generic;
using HVR.Vixxy;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Net.Towneh.Editor.VixxyMigration
{
    public static class VixxyMigrationApply
    {
        const string UndoName = "Apply Vixxy Animator Migration";

        public static int Apply(IEnumerable<MigrationItem> items, AnimatorController controller, GameObject avatarRoot, Transform outputParent, string containerName)
        {
            if (avatarRoot == null) throw new ArgumentNullException(nameof(avatarRoot));
            if (outputParent == null) outputParent = avatarRoot.transform;

            Undo.IncrementCurrentGroup();
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(UndoName);

            var container = FindOrCreateChild(outputParent, containerName);

            var paramDefaults = BuildParameterDefaultMap(controller);

            int created = 0;
            foreach (var item in items)
            {
                if (!item.Selected) continue;
                if (item.Category == MigrationCategory.Skipped) continue;

                var hostGameObject = FindOrCreateChild(container.transform, EncodeGameObjectName(item.ParameterName));
                CreateControlOnHost(hostGameObject, item, paramDefaults);
                created++;
            }

            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.SetDirty(avatarRoot);
            return created;
        }

        static Dictionary<string, float> BuildParameterDefaultMap(AnimatorController controller)
        {
            var map = new Dictionary<string, float>(StringComparer.Ordinal);
            if (controller == null) return map;
            foreach (var p in controller.parameters)
            {
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: map[p.name] = p.defaultFloat; break;
                    case AnimatorControllerParameterType.Int: map[p.name] = p.defaultInt; break;
                    case AnimatorControllerParameterType.Bool: map[p.name] = p.defaultBool ? 1f : 0f; break;
                }
            }
            return map;
        }

        static string EncodeGameObjectName(string parameterName)
        {
            // Encode the full parameter path into a globally-unique leaf name so BasisAvatar's duplicate-
            // name validator passes — source params routinely repeat leaf names across categories
            // (Toggles/Jacket/Main and Toggles/Shorts/Main both end in "Main"). The user-named container
            // (default "HVR.Vixxy") provides the ecosystem namespace; the encoded leaves don't repeat it.
            //   "Toggles/Jacket/Main"  → "Toggles_Jacket_Main"
            //   "Cosmetics/Eye Hue"    → "Cosmetics_Eye_Hue"
            return parameterName.Replace('/', '_').Replace(' ', '_');
        }

        static GameObject FindOrCreateChild(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                var c = parent.GetChild(i);
                if (c.name == name) return c.gameObject;
            }
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, UndoName);
            Undo.SetTransformParent(go.transform, parent, UndoName);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go;
        }

        static void CreateControlOnHost(GameObject host, MigrationItem item, Dictionary<string, float> paramDefaults)
        {
            var menuItem = Undo.AddComponent<HVRVixxyMenuItem>(host);
            var control = Undo.AddComponent<HVRVixxyControl>(host);

            ConfigureMenuItem(menuItem, control, item);
            ConfigureControl(control, item, paramDefaults);
        }

        static void ConfigureMenuItem(HVRVixxyMenuItem menuItem, HVRVixxyControl control, MigrationItem item)
        {
            var so = new SerializedObject(menuItem);
            so.FindProperty("title").stringValue = PrettifyTitle(item.ParameterName);
            // Plain UseCustomTitle (no per-choice labels). We don't set choice titles either — there's no
            // reliable way to know whether an item is a binary toggle or a continuous slider, so picking
            // labels (Hidden/Visible vs Min/Max) would be misleading often enough that defaulting to
            // nothing is more honest. The user adjusts presentation and labels per-item afterwards.
            so.FindProperty("titleSelection").enumValueIndex = (int)HVRVixxyTitleSelection.UseCustomTitle;
            so.FindProperty("presentation").enumValueIndex = (int)HVRVixxyControlPresentation.Default;
            so.FindProperty("control").objectReferenceValue = control;
            so.ApplyModifiedProperties();
        }

        static void ConfigureControl(HVRVixxyControl control, MigrationItem item, Dictionary<string, float> paramDefaults)
        {
            var so = new SerializedObject(control);

            var choicesProp = so.FindProperty("choices");
            choicesProp.arraySize = 2;
            SetChoice(choicesProp.GetArrayElementAtIndex(0), item.OffValue);
            SetChoice(choicesProp.GetArrayElementAtIndex(1), item.OnValue);

            var defaultProp = so.FindProperty("defaultValue");
            defaultProp.floatValue = paramDefaults.TryGetValue(item.ParameterName, out var d) ? d : item.OnValue;

            so.FindProperty("networked").boolValue = true;
            so.FindProperty("advancedNetworking").enumValueIndex = (int)HVRVixxyNetworkingType.Automatic;
            so.FindProperty("onlyExecuteWhenEnabled").boolValue = false;

            ConfigureSubjects(so.FindProperty("subjects"), item);
            ConfigureActivations(so.FindProperty("activations"), item);

            so.ApplyModifiedProperties();
        }

        static void SetChoice(SerializedProperty choiceProp, float value)
        {
            choiceProp.FindPropertyRelative("title").stringValue = "";
            choiceProp.FindPropertyRelative("icon").objectReferenceValue = null;
            choiceProp.FindPropertyRelative("value").floatValue = value;
        }

        static void ConfigureSubjects(SerializedProperty subjectsProp, MigrationItem item)
        {
            var rendererBuckets = new Dictionary<Renderer, List<MigrationCurve>>();
            foreach (var curve in item.Curves)
            {
                if (curve.ResolvedRenderer == null) continue;
                if (!rendererBuckets.TryGetValue(curve.ResolvedRenderer, out var list))
                {
                    list = new List<MigrationCurve>();
                    rendererBuckets[curve.ResolvedRenderer] = list;
                }
                list.Add(curve);
            }

            subjectsProp.arraySize = rendererBuckets.Count;
            int subjectIdx = 0;
            foreach (var kvp in rendererBuckets)
            {
                var subjectProp = subjectsProp.GetArrayElementAtIndex(subjectIdx++);

                subjectProp.FindPropertyRelative("selection").enumValueIndex = (int)HVRVixxySelection.Normal;

                var targetsProp = subjectProp.FindPropertyRelative("targets");
                targetsProp.arraySize = 1;
                targetsProp.GetArrayElementAtIndex(0).objectReferenceValue = kvp.Key.gameObject;

                subjectProp.FindPropertyRelative("childrenOf").arraySize = 0;
                subjectProp.FindPropertyRelative("exceptions").arraySize = 0;

                var propertiesProp = subjectProp.FindPropertyRelative("properties");
                propertiesProp.arraySize = kvp.Value.Count;
                for (int i = 0; i < kvp.Value.Count; i++)
                {
                    var c = kvp.Value[i];
                    var propEl = propertiesProp.GetArrayElementAtIndex(i);

                    // Step 1: assign a fresh HVRVixxyPropertyFloat instance via managedReferenceValue so the
                    // SerializeReference field knows the polymorphic type. Don't rely on the C# object
                    // initializer here — the choices array in particular has a default initializer
                    // (`new T[2]`) on HVRVixxyProperty<T>, and Unity's SerializeReference round-trip can
                    // resurface those defaults instead of our overrides if we mix object-initializer
                    // assignment with SerializedProperty edits below.
                    propEl.managedReferenceValue = new HVRVixxyPropertyFloat();

                    // Step 2: set every serialized field through SerializedProperty. This is the
                    // canonical, documented path — the values land where Unity's Inspector reads them and
                    // can't be overwritten by the type's default field initializers on the next reload.
                    propEl.FindPropertyRelative("fullClassName").stringValue = c.ComponentTypeFullName;
                    propEl.FindPropertyRelative("variant").enumValueIndex = (int)HVRVixxyPropertyVariant.MaterialProperty;
                    propEl.FindPropertyRelative("propertyName").stringValue = c.PropertyName;

                    var choicesArrayProp = propEl.FindPropertyRelative("choices");
                    choicesArrayProp.arraySize = 2;
                    choicesArrayProp.GetArrayElementAtIndex(0).floatValue = c.OffValue;
                    choicesArrayProp.GetArrayElementAtIndex(1).floatValue = c.OnValue;
                }
            }
        }

        static void ConfigureActivations(SerializedProperty activationsProp, MigrationItem item)
        {
            var resolvable = new List<MigrationActivation>();
            foreach (var a in item.Activations)
            {
                if (a.ResolvedComponent != null) resolvable.Add(a);
            }

            activationsProp.arraySize = resolvable.Count;
            for (int i = 0; i < resolvable.Count; i++)
            {
                var a = resolvable[i];
                var el = activationsProp.GetArrayElementAtIndex(i);
                el.FindPropertyRelative("component").objectReferenceValue = a.ResolvedComponent;
                el.FindPropertyRelative("threshold").enumValueIndex = (int)ActivationThreshold.Blended;

                var choicesProp = el.FindPropertyRelative("choices");
                choicesProp.arraySize = 2;
                choicesProp.GetArrayElementAtIndex(0).boolValue = a.OffActive;
                choicesProp.GetArrayElementAtIndex(1).boolValue = a.OnActive;
            }
        }

        static string PrettifyTitle(string parameterName)
        {
            // Strip the leading category segment (e.g. "Toggles/", "Cosmetics/") from the menu title and
            // replace any remaining '/' separators with spaces so the result reads as a phrase rather than
            // a file path. "Toggles/Jacket/Main" → "Jacket Main"; "Cosmetics/Eye Hue" → "Eye Hue".
            // Leaves with the same name across sub-categories (Jacket Main vs Shorts Main) remain
            // distinguishable, but we don't look like we're directing the user at a Unix path.
            if (string.IsNullOrEmpty(parameterName)) return "";
            var slash = parameterName.IndexOf('/');
            var stripped = slash < 0 ? parameterName : parameterName.Substring(slash + 1);
            return stripped.Replace('/', ' ');
        }
    }
}
