using Unity.Burst;
using UnityEngine;
using Unity.Collections;
using Unity.Jobs;

namespace MineGenerator.Containers
{
    [BurstCompile]
    public struct SetupPointWeightJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<Vector3> CurvePoints;
        public NativeArray<PointData> Data;
        
        [ReadOnly] public int GridSize;

        [ReadOnly] public float NoiseScale;
        [ReadOnly] public float NoiseAmplitude;

        [ReadOnly] public float Radius;

        public void Execute(int index)
        {
            PointData selectedPoint = Data[index];
            var pointPos = selectedPoint.Position;
            
            var noiseX = pointPos.x / GridSize * NoiseScale;
            var noiseY = pointPos.y / GridSize * NoiseScale;
            var noiseZ = pointPos.z / GridSize * NoiseScale;
            
            float noise = Noise.PerlinNoise3D(noiseX, noiseY, noiseZ) * NoiseAmplitude;
            
            for (int i = 0; i < CurvePoints.Length; i++)
            {
                var center = CurvePoints[i];
                
                float distance = (center.x - pointPos.x) * (center.x - pointPos.x) +
                                 (center.y - pointPos.y) * (center.y - pointPos.y) + 
                                 (center.z - pointPos.z) * (center.z - pointPos.z);

                var onRadius = distance <= Radius;
                var onSecRadius = distance <= Radius + 1.365f * Radius;

                if (onRadius)
                {
                    selectedPoint.Density = 1f;
                    selectedPoint.IsLocked = true;
                }
                else if(onSecRadius)
                {
                    var delta = Mathf.Exp(-distance);
                    selectedPoint.Density += delta + noise;
                }
            }

            Data[index] = selectedPoint;
        }
    }

}