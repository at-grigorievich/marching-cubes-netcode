using System;
using UnityEditor;
using UnityEngine;

namespace MineGenerator
{
    [CustomEditor(typeof(MineChunksEditor))]
    public class MineChunksEditorExtension: Editor
    {
        private MineChunksEditor editor;

        public override void OnInspectorGUI()
        {
            editor = target as MineChunksEditor;
            
            DrawDefaultInspector();

            editor!.AllowModify = false;
        }

        private void OnSceneGUI()
        {
            int id = GUIUtility.GetControlID(FocusType.Passive);
            HandleUtility.AddDefaultControl(id);

            if (Event.current.type == EventType.MouseDrag || Event.current.type == EventType.MouseDown)
            {
                if (Event.current.button == 0)
                {
                    editor.AllowModify = true;
                }
            }

            if (Event.current.type == EventType.MouseUp)
            {
                editor.AllowModify = false;
            }
        }
    }
}