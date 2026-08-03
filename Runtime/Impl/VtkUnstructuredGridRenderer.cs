using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace Monospark
{
    // Renders a VtkUnstructuredGridData as one point (glyph) per cell,
    // positioned at the cell centroid, colored by Temperature, sized by
    // Pressure, and oriented (local +Z) along Velocity, using Unity 6's
    // GPU-driven indirect draw APIs instead of per-instance C# transforms:
    // RenderPrimitivesIndirect for a raw point cloud (no Mesh — each point is
    // synthesized in the vertex shader from _CellBuffer via SV_VertexID) or
    // RenderMeshIndirect for an instanced mesh glyph per cell (SV_InstanceID
    // indexes _CellBuffer to offset/scale/rotate InstanceMesh per copy).
    public class VtkUnstructuredGridRenderer : StructuredBufferRenderer , IRenderStateControl
    {
        public enum eRenderType
        {
            PRIMITIVE,
            MESH
        }

        public eRenderType RenderType = eRenderType.PRIMITIVE;
        public string TemperatureField = "Temperature(C)";
        public string PressureField = "Pressure(Pa)";
        public string VelocityField = "Velocity(m/s)";
        public Material Material;
        public Mesh InstanceMesh; // only used when RenderType == MESH

        [StructLayout(LayoutKind.Sequential)]
        struct CellInstance
        {
            public Vector3 Position;
            public float Temperature; // normalized 0..1, drives color
            public Vector3 Velocity;  // raw velocity, drives glyph orientation
            public float Pressure;    // normalized 0..1, drives glyph size
        }

        const int CellInstanceStride = sizeof(float) * 8; // 2x (float3 + float)
        const int IndirectDrawArgsStride = sizeof(uint) * 4;
        const int IndirectDrawIndexedArgsStride = sizeof(uint) * 5;
        static readonly int CellBufferId = Shader.PropertyToID("_CellBuffer");

        GraphicsBuffer _cellBuffer;
        GraphicsBuffer _commandBuffer;
        Bounds _bounds;
        int _cellCount;
        bool _isVisible = true;

        // There's no Renderer component to toggle here — Update() issues the
        // indirect draw call directly — so visibility just gates that call.
        public void SetVisibility(bool isVisible)
        {
            _isVisible = isVisible;
        }

        // Returns the property's previous value, so callers can restore it later.
        public float SetMaterialFloat(string property, float value)
        {
            if (Material == null)
                return 0f;

            float previous = Material.GetFloat(property);
            Material.SetFloat(property, value);
            return previous;
        }

        public override void Set(DataUnit unit)
        {
            var data = (VtkUnstructuredGridData)unit;

            if (!data.CellScalars.TryGetValue(TemperatureField, out float[] temperatures))
                throw new KeyNotFoundException($"Scalar field '{TemperatureField}' was not found.");
            if (!data.CellScalars.TryGetValue(PressureField, out float[] pressures))
                throw new KeyNotFoundException($"Scalar field '{PressureField}' was not found.");
            if (!data.CellVectors.TryGetValue(VelocityField, out Vector3[] velocities))
                throw new KeyNotFoundException($"Vector field '{VelocityField}' was not found.");

            _cellCount = data.Cells.Length;

            (float tempMin, float tempMax) = MinMax(temperatures, _cellCount);
            (float pressMin, float pressMax) = MinMax(pressures, _cellCount);
            float tempRange = Mathf.Max(tempMax - tempMin, Mathf.Epsilon);
            float pressRange = Mathf.Max(pressMax - pressMin, Mathf.Epsilon);

            var instances = new CellInstance[_cellCount];
            var bounds = new Bounds(data.Points.Length > 0 ? data.Points[0] : Vector3.zero, Vector3.zero);
            for (int i = 0; i < _cellCount; i++)
            {
                Vector3 centroid = ComputeCentroid(data.Points, data.Cells[i]);
                instances[i] = new CellInstance
                {
                    Position = centroid,
                    Temperature = (temperatures[i] - tempMin) / tempRange,
                    Velocity = velocities[i],
                    Pressure = (pressures[i] - pressMin) / pressRange
                };
                bounds.Encapsulate(centroid);
            }
            _bounds = bounds;

            ReleaseBuffers();

            _cellBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _cellCount, CellInstanceStride);
            _cellBuffer.SetData(instances);

            _commandBuffer = RenderType == eRenderType.MESH
                ? BuildMeshIndirectArgs(InstanceMesh, (uint)_cellCount)
                : BuildPrimitiveIndirectArgs((uint)_cellCount);
        }

        void Update()
        {
            if (!_isVisible || _cellBuffer == null || _commandBuffer == null || Material == null)
                return;

            Material.SetBuffer(CellBufferId, _cellBuffer);

            var renderParams = new RenderParams(Material)
            {
                worldBounds = _bounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false
            };

            if (RenderType == eRenderType.MESH)
            {
                if (InstanceMesh != null)
                    Graphics.RenderMeshIndirect(renderParams, InstanceMesh, _commandBuffer);
            }
            else
            {
                Graphics.RenderPrimitivesIndirect(renderParams, MeshTopology.Points, _commandBuffer);
            }
        }

        static GraphicsBuffer BuildMeshIndirectArgs(Mesh mesh, uint instanceCount)
        {
            var args = new GraphicsBuffer.IndirectDrawIndexedArgs[1]
            {
                new GraphicsBuffer.IndirectDrawIndexedArgs
                {
                    indexCountPerInstance = mesh != null ? mesh.GetIndexCount(0) : 0,
                    instanceCount = instanceCount,
                    startIndex = 0,
                    baseVertexIndex = 0,
                    startInstance = 0
                }
            };

            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, IndirectDrawIndexedArgsStride);
            buffer.SetData(args);
            return buffer;
        }

        static GraphicsBuffer BuildPrimitiveIndirectArgs(uint cellCount)
        {
            // No mesh at all: one instance drawing cellCount synthesized vertices,
            // each a point whose position/value the shader pulls from _CellBuffer[SV_VertexID].
            var args = new GraphicsBuffer.IndirectDrawArgs[1]
            {
                new GraphicsBuffer.IndirectDrawArgs
                {
                    vertexCountPerInstance = cellCount,
                    instanceCount = 1,
                    startVertex = 0,
                    startInstance = 0
                }
            };

            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, IndirectDrawArgsStride);
            buffer.SetData(args);
            return buffer;
        }

        static (float min, float max) MinMax(float[] values, int count)
        {
            float min = float.MaxValue;
            float max = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                min = Mathf.Min(min, values[i]);
                max = Mathf.Max(max, values[i]);
            }
            return (min, max);
        }

        static Vector3 ComputeCentroid(Vector3[] points, VtkCell cell)
        {
            Vector3 sum = Vector3.zero;
            int[] indices = cell.PointIndices;
            for (int i = 0; i < indices.Length; i++)
                sum += points[indices[i]];
            return sum / indices.Length;
        }

        void ReleaseBuffers()
        {
            _cellBuffer?.Release();
            _cellBuffer = null;
            _commandBuffer?.Release();
            _commandBuffer = null;
        }

        void OnDestroy()
        {
            ReleaseBuffers();
        }
    }
}