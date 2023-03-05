using UnityEditor;
using UnityEngine;

namespace MineGenerator
{
    [CustomEditor(typeof(ChunkBezierGenerator),true)]
    public class ChunkBezierGeneratorEditor: Editor
    {
#if UNITY_EDITOR
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            if (GUILayout.Button("Generate mine"))
            {
                ((ChunkBezierGenerator)target).CreateChunks();
            }
        }
#endif
    }
}