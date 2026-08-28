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
    // Material is instanced per-component in Awake (see _materialInstance),
    // not used directly -- otherwise every renderer sharing the same
    // prefab's Material asset would stomp on each other's buffer binding,
    // clip range, and shader.
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
        static readonly int ObjectToWorldId = Shader.PropertyToID("_ObjectToWorld");
        static readonly int SpeedMinId = Shader.PropertyToID("_SpeedMin");
        static readonly int SpeedMaxId = Shader.PropertyToID("_SpeedMax");

        GraphicsBuffer _cellBuffer;
        GraphicsBuffer _commandBuffer;
        Bounds _localBounds;  // centered at origin — cell.Position is stored relative to this
        Bounds _worldBounds;  // _localBounds transformed by _objectToWorld, kept in sync with it
        Matrix4x4 _objectToWorld = Matrix4x4.identity;
        int _cellCount;
        bool _isVisible = true;
        // Raw (unnormalized) speed range, so the shader can normalize
        // length(cell.velocity) to 0..1 for its own _VelocityClipMin/Max filter
        // the same way Temperature/Pressure are normalized here on the CPU.
        float _speedMin;
        float _speedMax;

        // Tracked separately from the public Material field so OnDestroy only
        // ever destroys the instance THIS component created -- not whatever a
        // caller might reassign the field to later.
        Material _materialInstance;

        void Awake()
        {
            // Per-instance copy, not the shared prefab/scene Material asset
            // directly -- see the class doc comment.
            if (Material != null)
            {
                _materialInstance = new Material(Material);
                Material = _materialInstance;
            }
        }

        void OnDestroy()
        {
            ReleaseBuffers();
            // Instances created via `new Material(...)` in Awake aren't
            // scene/project assets -- Unity won't reclaim them on its own
            // when this GameObject is destroyed, so this would otherwise
            // leak one Material per instance for the rest of the session.
            if (_materialInstance != null)
                Destroy(_materialInstance);
        }

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

        public float GetMaterialFloat(string property)
        {
            return Material != null ? Material.GetFloat(property) : 0f;
        }

        public void SetShader(Shader shader)
        {
            if (Material != null && shader != null)
                Material.shader = shader;
        }

        public Shader GetShader()
        {
            return Material != null ? Material.shader : null;
        }

        // No-op here: this renders a single static snapshot, not a sequence
        // of frames — there's nothing to interpolate between. Only
        // VtkFrameSequencePlayer's implementation does anything.
        public void SetInterpolation(bool enabled)
        {
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

            var speeds = new float[_cellCount];
            for (int i = 0; i < _cellCount; i++)
                speeds[i] = velocities[i].magnitude;
            (_speedMin, _speedMax) = MinMax(speeds, _cellCount);

            // Two passes: centroids/bounds first, then Position is stored
            // relative to bounds.center — this renderer's own Transform is
            // what actually places the (now-centered) data in the world, via
            // _ObjectToWorld in Update(), rather than baking raw VTK world
            // coordinates directly into the buffer.
            var centroids = new Vector3[_cellCount];
            var bounds = new Bounds(data.Points.Length > 0 ? data.Points[0] : Vector3.zero, Vector3.zero);
            for (int i = 0; i < _cellCount; i++)
            {
                centroids[i] = ComputeCentroid(data.Points, data.Cells[i]);
                bounds.Encapsulate(centroids[i]);
            }
            _localBounds = new Bounds(Vector3.zero, bounds.size);

            var instances = new CellInstance[_cellCount];
            for (int i = 0; i < _cellCount; i++)
            {
                instances[i] = new CellInstance
                {
                    Position = centroids[i] - bounds.center,
                    Temperature = (temperatures[i] - tempMin) / tempRange,
                    Velocity = velocities[i],
                    Pressure = (pressures[i] - pressMin) / pressRange
                };
            }

            // Force a recompute on the next Update() even if the transform
            // itself hasn't moved since last Set() — _localBounds just changed.
            transform.hasChanged = true;

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

            // The expensive part — recomputing the matrix/world bounds from
            // the Transform — only happens when it's actually moved. The
            // Material calls below still run every frame regardless: this
            // uses Graphics.Render*Indirect (immediate-mode draw calls, no
            // persistent Renderer component backing them), so the buffer/
            // matrix/speed-range bindings have to be pushed onto Material
            // fresh before every draw call this component issues, not just
            // once after Set().
            if (transform.hasChanged)
            {
                _objectToWorld = transform.localToWorldMatrix;
                _worldBounds = TransformBounds(_objectToWorld, _localBounds);
                transform.hasChanged = false;
            }

            Material.SetBuffer(CellBufferId, _cellBuffer);
            Material.SetMatrix(ObjectToWorldId, _objectToWorld);
            Material.SetFloat(SpeedMinId, _speedMin);
            Material.SetFloat(SpeedMaxId, _speedMax);

            var renderParams = new RenderParams(Material)
            {
                worldBounds = _worldBounds,
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

        // Conservative world-space AABB of a local AABB under an arbitrary
        // (rotated/scaled) matrix: transform each half-extent axis and sum
        // absolute components, rather than transforming and re-encapsulating
        // all 8 corners.
        static Bounds TransformBounds(Matrix4x4 matrix, Bounds localBounds)
        {
            Vector3 center = matrix.MultiplyPoint3x4(localBounds.center);
            Vector3 extents = localBounds.extents;

            Vector3 axisX = matrix.MultiplyVector(new Vector3(extents.x, 0, 0));
            Vector3 axisY = matrix.MultiplyVector(new Vector3(0, extents.y, 0));
            Vector3 axisZ = matrix.MultiplyVector(new Vector3(0, 0, extents.z));

            Vector3 newExtents = new Vector3(
                Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
                Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
                Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z));

            return new Bounds(center, newExtents * 2f);
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
    }
}