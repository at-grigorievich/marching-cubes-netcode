using System;
using UnityEditor;
using UnityEngine;

namespace MineGenerator.Data
{
    public enum MineType
    {
        Cubic = 1,
        Rounded = 2
    }
    
    [Serializable]
    public sealed class GridData
    {
        [field: Header("Количество чанков")]
        [field: SerializeField] public Vector3Int ChunksCount { get; private set; }
        [field: Header("Параметры сетки")]
        [field: SerializeField] public int GridSize { get; private set; } = 40;
        [field: SerializeField] public float DeltaStep { get; private set; } = 1;
    }

    [Serializable]
    public sealed class NoiseData
    {
        [field: Header("Параметры шума")]
        [field: SerializeField] public float NoiseScale { get; private set; } = 10f;
        [field: SerializeField] public float NoiseAmplitude { get; private set; } = 0.4f;
    }

    [Serializable]
    public sealed class MineTunnelData
    {
        [field: Header("Параметры туннеля")]
        [field: SerializeField] public float Radius = 4f;
        [field: SerializeField] public float SecondRadius = 8f;
        [field: SerializeField] public float RadiusError = 0.3f;
        [field: Space(5)]
        [field: SerializeField] public MineType MineType { get; private set; } = MineType.Cubic;
        
        public float RadiusWithError => Radius+RadiusError;
    }
    
    [CreateAssetMenu(fileName = "new Chunk Parameters", menuName = "Mine Generator/Chunk Parameters", order = 0)]
    public class ChunkData : ScriptableSingleton<ChunkData>
    {
        [field: SerializeField] public float IsoLevel { get; private set; } = 0.5f;
        [field: Space(5)]
        [field: SerializeField] public GridData GridParameters { get; private set; }
        [field: SerializeField] public NoiseData NoiseParameters { get; private set; }
        [field: SerializeField] public MineTunnelData TunnelParameters { get; private set; }

        public int GridSize => GridParameters.GridSize;
        public float DeltaStep => GridParameters.DeltaStep;
        public float ChunkStep => GridSize * DeltaStep;
        
        public Vector3Int ChunksCount => GridParameters.ChunksCount;

        public int ChunksCountX => ChunksCount.x -1;
        public int ChunksCountY => ChunksCount.y - 1;
        public int ChunksCountZ => ChunksCount.z -1;

        public Vector3 ChunksCenter =>
            new Vector3(ChunksCountX * ChunkStep/2f, ChunksCountY * ChunkStep/2f + ChunkStep/4f, ChunksCountZ * ChunkStep/2f);
        public Vector3 ChunksSize =>
            new Vector3(ChunksCountX * ChunkStep, ChunksCountY * ChunkStep + ChunkStep/2f, ChunksCountZ * ChunkStep);

        public Vector2 GetCornersX()
        {
            var xBot = ChunksCenter.x - ChunksSize.x / 2f;
            var xTop = ChunksCenter.x + ChunksSize.x / 2f;

            return new Vector2(xBot, xTop);
        }
        public Vector2 GetCornersY()
        {
            var yBot = ChunksCenter.y - ChunksSize.y / 2f;
            var yTop = ChunksCenter.y + ChunksSize.y / 2f;

            return new Vector2(yBot, yTop);
        }
        
        public Vector2 GetCornersZ()
        {
            var zBot = ChunksCenter.z - ChunksSize.z / 2f;
            var zTop = ChunksCenter.z + ChunksSize.z / 2f;

            return new Vector2(zBot, zTop);
        }
    }
}