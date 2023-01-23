using Mine_Generator.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace MineGenerator.Containers
{
    [BurstCompile]
    public struct UpdatePointsWeightJob: IJob
    {
        // Точка и радиус, где нужно обновить вес точек
        [ReadOnly] public WeightModifyData ModifyData;
        
        public NativeArray<PointData> Data;

        public void Execute()
        {
            for (int i = 0; i < Data.Length; i++)
            {
                PointData selected = Data[i];
                
                if(!selected.IsGround) continue;
                if(!MathfHelper.IsPointInRadius(selected.Position,ModifyData.ModifyCenter,ModifyData.ModifyRadius)) continue;

                selected.Density += ModifyData.ModifyIntensity;
                Data[i] = selected;
            }
        }
    }
}