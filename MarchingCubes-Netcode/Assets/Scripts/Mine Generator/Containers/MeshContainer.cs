using System.Collections.Generic;
using UnityEngine;

namespace MineGenerator.Containers
{
    public class MeshContainer
    {
        private readonly List<Vector3> _vertices;
        private readonly List<int> _triangles;

        private readonly MeshFilter _meshFilter;
        
        private readonly Mesh _mesh;

        public int TrianglesCount => _triangles.Count;
        
        public MeshContainer(MeshFilter meshFilter)
        {
            _vertices = new List<Vector3>();
            _triangles = new List<int>();

            _meshFilter = meshFilter;
            _mesh = new Mesh();
        }

        public void UpdateMesh(int oldCount)
        {
            if (TrianglesCount == oldCount) return;
            
            _mesh.SetVertices(_vertices);
            _mesh.SetTriangles(_triangles,0);
            _mesh.RecalculateNormals();
            
            _meshFilter.mesh = _mesh;
        }
        
        public void ClearMesh()
        {
            _mesh.Clear();
            _meshFilter.mesh = _mesh;
            
            _vertices.Clear();
            _triangles.Clear();
        }
        
        public void AddTriangle(Vector3 a, Vector3 b, Vector3 c) {
            int triIndex = _triangles.Count;
            
            _vertices.Add(a);
            _vertices.Add(b);
            _vertices.Add(c);
            
            _triangles.Add(triIndex);
            _triangles.Add(triIndex + 1);
            _triangles.Add(triIndex + 2);
        }
    }
}