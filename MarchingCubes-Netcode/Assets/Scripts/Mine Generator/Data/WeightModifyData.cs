using UnityEngine;

namespace Mine_Generator.Data
{
    public readonly struct WeightModifyData
    {
        public Vector3 ModifyCenter { get; }
        public float ModifyRadius { get; }
        public float ModifyIntensity { get; }

        public WeightModifyData(Vector3 center, float r, float intensity)
        {
            ModifyCenter = center;
            ModifyRadius = r;
            ModifyIntensity = intensity;
        }
    }
}