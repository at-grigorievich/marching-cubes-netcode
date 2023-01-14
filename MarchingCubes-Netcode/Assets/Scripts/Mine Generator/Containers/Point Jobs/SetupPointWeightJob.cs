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
        [ReadOnly] public float RadiusWithError;
        [ReadOnly] public float SecondRadius;

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
                
                var distance = Vector3.Distance(center, pointPos);
                
                var onRadius = distance <= Radius;
                var onSecRadius = distance <= RadiusWithError;
                var onThirdRadius = distance <= SecondRadius;

                if (onRadius)
                {
                    selectedPoint.Density = 1f;
                    selectedPoint.IsAvailable = true;
                }
                else if(onSecRadius)
                {
                    selectedPoint.Density += noise;
                    selectedPoint.IsAvailable = true;
                }
                else if (onThirdRadius)
                {
                    selectedPoint.IsAvailable = true;
                }
            }

            Data[index] = selectedPoint;
        }
    }

}