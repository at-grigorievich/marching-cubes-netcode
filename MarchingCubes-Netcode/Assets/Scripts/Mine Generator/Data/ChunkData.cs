using System;
using UnityEngine;

namespace MineGenerator.Data
{
    [Serializable]
    public sealed class GridData
    {
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
        
        public float RadiusWithError => Radius+RadiusError;
    }
    
    [CreateAssetMenu(fileName = "new Chunk Parameters", menuName = "Mine Generator/Chunk Parameters", order = 0)]
    public class ChunkData : ScriptableObject
    {
        [field: SerializeField] public float IsoLevel { get; private set; } = 0.5f;
        [field: Space(5)]
        [field: SerializeField] public GridData GridParameters { get; private set; }
        [field: SerializeField] public NoiseData NoiseParameters { get; private set; }
        [field: SerializeField] public MineTunnelData TunnelParameters { get; private set; }
    }
}