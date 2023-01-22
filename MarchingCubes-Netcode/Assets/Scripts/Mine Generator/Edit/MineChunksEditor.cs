using UnityEditor;
using UnityEngine;

namespace MineGenerator
{
    [ExecuteInEditMode]
    public class MineChunksEditor : MonoBehaviour
    {
        public float Radius;
        public bool AllowModify;
        
        private void OnDrawGizmos()
        {
            RaycastHit hit;

            Ray ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
            
            if (Physics.Raycast(ray, out hit))
            {
                if (hit.collider.TryGetComponent(out MineBezierChunk chunk))
                {
                    Gizmos.color = Color.yellow;
                    Gizmos.DrawWireSphere(hit.point,Radius);
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