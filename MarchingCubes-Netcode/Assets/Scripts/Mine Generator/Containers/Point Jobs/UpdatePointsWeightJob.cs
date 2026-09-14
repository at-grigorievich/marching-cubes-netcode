using Mine_Generator.Data;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace MineGenerator.Containers
{
    [BurstCompile]
    public struct UpdatePointsWeightJob: IJobParallelFor
    {
        // Точка и радиус, где нужно обновить вес точек
        [ReadOnly] public WeightModifyData ModifyData;
        
        public NativeArray<PointData> Data;
        
        public void Execute(int index)
        {
            PointData selected = Data[index];

            if(!selected.IsAvailable || selected.IsCorner) return;
            
            if (!MathfHelper.IsPointInRadius(selected.Position, 
                    ModifyData.ModifyCenter, ModifyData.ModifyRadius, out float distance)) return;

            switch (ModifyData.TypeOfModify) 
            {
                case ModifyType.Decrease:
                    DecreaseWeight(index,selected, distance);
                    break;
                case ModifyType.Increase:
                    IncreaseWeight(index,selected,distance);
                    break;
            }
        }

        private void IncreaseWeight(int index, PointData selected, float distance)
        {
            selected.Density = Mathf.Clamp01(selected.Density - ModifyData.ModifyIntensity * Falloff(distance));
            Data[index] = selected;
        }

        private void DecreaseWeight(int index, PointData selected, float distance)
        {
            selected.Density = Mathf.Clamp01(selected.Density + ModifyData.ModifyIntensity * Falloff(distance));
            Data[index] = selected;
        }

        /// <summary>Плавное затухание от центра кисти к краю: 1 в центре, 0 на радиусе.</summary>
        private float Falloff(float distance)
        {
            var t = 1f - Mathf.Clamp01(distance / Mathf.Max(ModifyData.ModifyRadius, 1e-4f));
            return t * t;
        }
    }
}