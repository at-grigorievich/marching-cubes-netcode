using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using NaughtyBezierCurves;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using Color = UnityEngine.Color;

namespace Mine_Generator
{
    public class MineChunkGenerator : MonoBehaviour
    {
        public int gridSize;
        public float deltaStep;
        public float isoLevel;
        public float noiseScale;
        public float noiseAmplitude;

        public BezierCurve3D bezier;
        
        private PointData[,,] _points;

        public float radius;
        public float error;
        
        public float marchSpeedInSeconds = 0.5f;
        public MeshFilter MeshFilter;

        private Mesh _mesh;
        
        List<Vector3> vertices = new List<Vector3>();
        List<int> triangles = new List<int>();

        [ContextMenu("Create Mine chunk")]
        public void CreateMineChunk()
        {
            _points = new PointData[gridSize, gridSize, gridSize];
            for (int x = 0; x < gridSize; x++)
            {
                for (int y = 0; y < gridSize; y++)
                {
                    for (int z = 0; z < gridSize; z++)
                    {
                        var pos = new Vector3(x*deltaStep, y*deltaStep, z*deltaStep);

                        _points[x, y, z] = new PointData(pos, 0f);
                    }
                }
            }
        }

        [ContextMenu("Generate Circle")]
        public void GenerateCircle()
        {
            _mesh = new Mesh();
            MeshFilter.mesh = _mesh;
            MeshFilter.sharedMesh = _mesh;
            
            CreateMineChunk();

            //float centerValue = ((gridSize-1) * deltaStep) / 2f;
            //Vector3 center = Vector3.one * centerValue;
            
            
            
            float rMax = (radius+error) * (radius+error);

            for (int z = 0; z < gridSize; z++)
            {
                for (int y = 0; y < gridSize; y++)
                {
                    for (int x = 0; x < gridSize; x++)
                    {
                        var normalize = (z * deltaStep) / bezier.GetApproximateLength();
                        
                        if(normalize > 1f) continue;

                        var center = bezier.GetPoint(normalize);
                        
                        var point = _points[x, y, z];
                        var pointPos = point.Position;
                        
                        float distance = (center.x - pointPos.x) * (center.x - pointPos.x) +
                                         (center.y - pointPos.y) * (center.y - pointPos.y) + 
                                         (center.z - pointPos.z) * (center.z - pointPos.z);

                        var onRadius = distance < rMax;

                        if (onRadius)
                        {
                            _points[x, y, z].Density = 1f;
                        }
                        else
                        {
                            var delta = rMax/distance;
                            float noise = Noise.PerlinNoise3D((float)x / gridSize * noiseScale, (float)y / gridSize * noiseScale, (float)z / gridSize * noiseScale) * noiseAmplitude;

                            _points[x, y, z].Density = delta + noise;
                        }
                    }
                }
            }
            
            March();
        }

        Vector3 Interp(Vector3 edgeVertex1, float valueAtVertex1, Vector3 edgeVertex2, float valueAtVertex2) {
            return (edgeVertex1 + (isoLevel - valueAtVertex1) * (edgeVertex2 - edgeVertex1) / (valueAtVertex2 - valueAtVertex1));
        }
        
        void AddTriangle(Vector3 a, Vector3 b, Vector3 c) {
            int triIndex = triangles.Count;
            vertices.Add(a);
            vertices.Add(b);
            vertices.Add(c);
            triangles.Add(triIndex);
            triangles.Add(triIndex + 1);
            triangles.Add(triIndex + 2);
        }
        
        private void March()
        {
            for (int x = 0; x < gridSize - 1; x++)
            {
                for (int y = 0; y < gridSize - 1; y++)
                {
                    for (int z = 0; z < gridSize - 1; z++)
                    {
                        float[] cubeValues = new float[] {
                            _points[x, y, z + 1].Density,
                            _points[x + 1, y, z + 1].Density,
                            _points[x + 1, y, z].Density,
                            _points[x, y, z].Density,
                            _points[x, y + 1, z + 1].Density,
                            _points[x + 1, y + 1, z + 1].Density,
                            _points[x + 1, y + 1, z].Density,
                            _points[x, y + 1, z].Density
                        };
                        
                        Vector3 worldPos = new Vector3(x, y, z)*deltaStep;
                        
                        int cubeIndex = 0;
                        if (cubeValues[0] < isoLevel) cubeIndex |= 1;
                        if (cubeValues[1] < isoLevel) cubeIndex |= 2;
                        if (cubeValues[2] < isoLevel) cubeIndex |= 4;
                        if (cubeValues[3] < isoLevel) cubeIndex |= 8;
                        if (cubeValues[4] < isoLevel) cubeIndex |= 16;
                        if (cubeValues[5] < isoLevel) cubeIndex |= 32;
                        if (cubeValues[6] < isoLevel) cubeIndex |= 64;
                        if (cubeValues[7] < isoLevel) cubeIndex |= 128;
                        
                        int[] edges = MarchingCubesTables.triTable[cubeIndex];
                        int triCount = triangles.Count;
                        
                        for (int i = 0; edges[i] != -1; i += 3) {
                            int e00 = MarchingCubesTables.edgeConnections[edges[i]][0];
                            int e01 = MarchingCubesTables.edgeConnections[edges[i]][1];

                            int e10 = MarchingCubesTables.edgeConnections[edges[i + 1]][0];
                            int e11 = MarchingCubesTables.edgeConnections[edges[i + 1]][1];

                            int e20 = MarchingCubesTables.edgeConnections[edges[i + 2]][0];
                            int e21 = MarchingCubesTables.edgeConnections[edges[i + 2]][1];

                            Vector3 a = Interp(MarchingCubesTables.cubeCorners[e00]*deltaStep, cubeValues[e00], MarchingCubesTables.cubeCorners[e01]*deltaStep, cubeValues[e01]) + worldPos;
                            Vector3 b = Interp(MarchingCubesTables.cubeCorners[e10]*deltaStep, cubeValues[e10], MarchingCubesTables.cubeCorners[e11]*deltaStep, cubeValues[e11]) + worldPos;
                            Vector3 c = Interp(MarchingCubesTables.cubeCorners[e20]*deltaStep, cubeValues[e20], MarchingCubesTables.cubeCorners[e21]*deltaStep, cubeValues[e21]) + worldPos;

                            AddTriangle(c, b, a);
                        }
                        if (triCount != triangles.Count) {
                            _mesh.SetVertices(vertices);
                            _mesh.SetTriangles(triangles, 0);
                            _mesh.RecalculateNormals();
                            MeshFilter.mesh = _mesh;
                        }
                    }
                }
            }
        }

        private void OnDrawGizmosSelected()
        {
            if(_points == null) return;

            float centerValue = ((gridSize-1) * deltaStep) / 2f;
            Vector2 center = Vector2.one * centerValue;

#if UNITY_EDITOR
            Handles.color = Color.red;
            Handles.DrawWireDisc(new Vector3(center.x,center.y,0f),Vector3.forward,radius);
#endif

            for (int x = 0; x < gridSize; x++)
            {
                for (int y = 0; y < gridSize; y++)
                {
                    for (int z = 0; z < gridSize; z++)
                    {
                        Gizmos.color = _points[x, y, z].Density >= isoLevel ? Color.white : Color.black;
                        Gizmos.DrawCube(_points[x, y, z].Position,Vector3.one*.1f);
                    }  
                }
            }
        }
    }
}