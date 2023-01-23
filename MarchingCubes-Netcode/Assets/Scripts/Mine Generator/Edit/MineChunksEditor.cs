using Mine_Generator.Data;
using MineGenerator.Interfaces;
using UnityEditor;
using UnityEngine;

namespace MineGenerator
{
    [ExecuteInEditMode]
    public class MineChunksEditor : MonoBehaviour
    {
        public float Radius;
        public float Intensity;
        
        public bool AllowModify;

        private void OnDrawGizmos()
        {
            RaycastHit hit;

            Ray ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
            
            if (Physics.Raycast(ray, out hit))
            {
                if (hit.transform.TryGetComponent(out IWeightEditable weightEditor))
                {
                    Gizmos.color = !AllowModify ? Color.yellow : Color.red;
                    Gizmos.DrawWireSphere(hit.point,Radius);

                    if (AllowModify)
                    {
                        weightEditor.UpdateWeight(new WeightModifyData(hit.point,Radius,Intensity));
                    }
                }
            }
            
#if UNITY_EDITOR
            // Ensure continuous Update calls.
            if (!Application.isPlaying)
            {
                UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
                UnityEditor.SceneView.RepaintAll();
            }
#endif
        }
    }
}