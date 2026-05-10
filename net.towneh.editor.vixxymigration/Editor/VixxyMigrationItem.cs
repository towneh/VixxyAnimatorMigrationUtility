using System.Collections.Generic;
using UnityEngine;

namespace Net.Towneh.Editor.VixxyMigration
{
    public enum MigrationCategory
    {
        Material,        // Item only writes material properties.
        Activation,      // Item only toggles GameObject active or component enable.
        Mixed,           // Item does both (e.g. UDIM tile discard + JiggleRig enable on the same toggle).
        Skipped          // Item cannot be migrated (no clips, no migratable curves, or explicitly excluded).
    }

    public enum MigrationStatus
    {
        Migratable,
        MigratableWithWarning,
        NotMigratable
    }

    public sealed class MigrationItem
    {
        public string ParameterName;
        public MigrationCategory Category;
        public MigrationStatus Status;
        public string SkipReason;
        public List<string> Warnings = new List<string>();
        public bool Selected;

        public List<MigrationCurve> Curves = new List<MigrationCurve>();
        public List<MigrationActivation> Activations = new List<MigrationActivation>();

        public float OffValue;
        public float OnValue;
    }

    public sealed class MigrationCurve
    {
        public string GameObjectPath;

        // PropertyName is the EFFECTIVE name to write at apply time. ResolveTargets rewrites it when a
        // user-provided remap (Material suffix / Property full-name) or the Poi StringTagMap auto-resolve
        // step finds a matching declared property. OriginalPropertyName preserves the source-animator
        // name verbatim so resolution stays idempotent across passes — the source intent can be recovered
        // and the remap UIs can show the user what the source asked for vs what the avatar actually has.
        public string PropertyName;
        public string OriginalPropertyName;

        public string ComponentTypeFullName;
        public float OffValue;
        public float OnValue;

        public Renderer ResolvedRenderer;
        public bool TargetMissing;
    }

    public sealed class MigrationActivation
    {
        public string OriginalGameObjectPath;
        public string OriginalComponentTypeName;
        public bool OffActive;
        public bool OnActive;

        public Component ResolvedComponent;
        public bool TargetMissing;
        public string ResolutionNote;
    }
}
