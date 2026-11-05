using UnityEditor;
using UnityEngine;

namespace Plyry.Unity.Editor
{
    [CustomEditor(typeof(PlyryGenerationTarget))]
    public sealed class PlyryGenerationTargetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            GUILayout.Space(6f);
            if (!GUILayout.Button("Generate New Stable Target ID")) return;

            var generationTarget = (PlyryGenerationTarget)target;
            Undo.RecordObject(generationTarget, "Generate Plyry target ID");
            generationTarget.GenerateNewStableTargetId();
            EditorUtility.SetDirty(generationTarget);
        }
    }
}
