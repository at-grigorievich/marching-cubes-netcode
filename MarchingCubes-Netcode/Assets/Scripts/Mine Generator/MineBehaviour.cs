using System.Collections.Generic;
using Mine_Generator.Data;
using MineGenerator.Interfaces;
using UnityEditor;
using UnityEngine;

namespace MineGenerator
{
    public class MineBehaviour : MonoBehaviour, IMineChunksCollectable, IWeightEditable
    {
        [SerializeField] private List<MineBezierChunk> mineChunks;

        public void AddMineChunks(params MineBezierChunk[] chunks)
        {
            var t = transform;

            mineChunks ??= new List<MineBezierChunk>();
            
            for (var i = 0; i < chunks.Length; i++)
            {
                chunks[i].transform.SetParent(t);
                chunks[i].name = $"chunk{i}";
                    
                mineChunks.Add(chunks[i]);
            }
        }
        
        public void SaveMineMeshes()
        {
#if UNITY_EDITOR
            var path = GetMeshAssetParentPath();

            var model = new Mesh();
            AssetDatabase.CreateAsset(model,path);
            
            for (var i = 0; i < mineChunks.Count; i++)
            {
                mineChunks[i].SaveMeshChunk(path,$"mesh{i}");
            }
            
            AssetDatabase.SaveAssets();
            PrefabUtility.SaveAsPrefabAssetAndConnect(gameObject, GetPrefabAssetParentPath(),
                InteractionMode.UserAction);
#endif
        }
        
        public string GetMeshAssetParentPath() => $"Assets/FBX/Mines/{transform.name}.asset";
        public string GetPrefabAssetParentPath() => $"Assets/Prefabs/Mines/{transform.name}.prefab";
        
        public void UpdateWeight(WeightModifyData modifyData)
        {
            for (var i = 0; i < mineChunks.Count; i++)
            {
                mineChunks[i].UpdateWeight(modifyData);
            }
        }
    }
}