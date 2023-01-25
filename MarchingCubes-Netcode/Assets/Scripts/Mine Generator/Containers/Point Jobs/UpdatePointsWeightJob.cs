using Mine_Generator.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

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
                
                if(!selected.IsAvailable) continue;
                if (!MathfHelper.IsPointInRadius(selected.Position, ModifyData.ModifyCenter, ModifyData.ModifyRadius, out float distance)) continue;

                switch (ModifyData.TypeOfModify)
                {
                    case ModifyType.Decrease:
                        DecreaseWeight(i,selected, distance);
                        break;
                    case ModifyType.Increase:
                        IncreaseWeight(i,selected,distance);
                        break;
                }
            }
        }

        private void IncreaseWeight(int index, PointData selected, float distance)
        {
            float density = selected.Density - ModifyData.ModifyIntensity/(distance*distance);
                
            selected.Density = Mathf.Clamp01(density);
            Data[index] = selected;
        }

        private void DecreaseWeight(int index, PointData selected, float distance)
        {
            if(selected.IsCorner) return;
            
            float density = selected.Density + ModifyData.ModifyIntensity/(distance*distance);
                
            selected.Density = Mathf.Clamp01(density);
            Data[index] = selected;
        }
    }
}