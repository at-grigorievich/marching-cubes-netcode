using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace MineGenerator.Containers
{
    [BurstCompile]
    public struct CreatePointsJob: IJob
    {
        [WriteOnly] public NativeArray<PointData> Data;

        [ReadOnly] public int GridSize;
        [ReadOnly] public float DeltaStep;
        [ReadOnly] public Vector3 WorldDelta;
        
        public void Execute()
        {
            int index = 0;
            
            for (int x = 0; x < GridSize; x++)
            {
                for (int y = 0; y < GridSize; y++)
                {
                    for (int z = 0; z < GridSize; z++)
                    {
                        var pos = new Vector3(x*DeltaStep, y*DeltaStep, z*DeltaStep);
                        pos += WorldDelta;

                        Data[index] = new PointData(pos, 0f);

                        index++;
                    }
                }
            }
        }
    }
}