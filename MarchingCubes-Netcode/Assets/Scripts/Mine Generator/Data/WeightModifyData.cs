using UnityEngine;

namespace Mine_Generator.Data
{
    public enum ModifyType
    {
        Decrease = 1, // выкапывает
        Increase = 2// наращивает
    }
    
    public readonly struct WeightModifyData
    {
        public Vector3 ModifyCenter { get; }
        public float ModifyRadius { get; }
        public float ModifyIntensity { get; }

        public ModifyType TypeOfModify { get; }
        
        public WeightModifyData(Vector3 center, float r, float intensity, ModifyType modifyType)
        {
            ModifyCenter = center;
            ModifyRadius = r;
            ModifyIntensity = intensity;

            TypeOfModify = modifyType;
        }
    }
}