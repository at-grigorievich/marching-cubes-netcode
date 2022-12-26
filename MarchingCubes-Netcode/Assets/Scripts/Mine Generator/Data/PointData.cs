using UnityEngine;

namespace MineGenerator
{
    public struct PointData
    {
        public Vector3 Position;

        public float Density
        {
            get => _density;
            set
            {
                if (IsLocked) return;
                _density = value;
            }
        }

        public bool IsLocked;

        private float _density;

        public PointData(Vector3 pos, float dens)
        {
            Position = pos;
            _density = dens;

            IsLocked = false;
        }
    }
}
