using UnityEditor;
using UnityEngine;

namespace MineGenerator
{
    [CustomEditor(typeof(MineBehaviour),true)]
    public class MineBehaviourEditor: Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (GUILayout.Button("Save"))
            {
                ((MineBehaviour)target).SaveMineMeshes();
            }
        }
    }
}