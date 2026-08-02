using System.Collections.Generic;
using UnityEngine;

namespace Monospark
{
    public abstract class DataUnit
    {

    }

    // One CELLS + CELL_TYPES entry: the point indices making up a cell and its
    // VTK cell-shape id (e.g. 12 = VTK_HEXAHEDRON, the only type room.vtk uses).
    public struct VtkCell
    {
        public int Type;
        public int[] PointIndices;
    }

    // Raw parsed contents of a legacy ASCII VTK UNSTRUCTURED_GRID file such as
    // Assets/Resources/Data/room.vtk: POINTS, CELLS/CELL_TYPES, and every
    // CELL_DATA field (SCALARS Temperature(C)/Pressure(Pa), VECTORS Velocity(m/s)).
    public class VtkUnstructuredGridData : DataUnit
    {
        public string Title;
        public Vector3[] Points;
        public VtkCell[] Cells;
        public Dictionary<string, float[]> CellScalars = new Dictionary<string, float[]>();
        public Dictionary<string, Vector3[]> CellVectors = new Dictionary<string, Vector3[]>();
    }

    // One frame of a voxelized temperature time-sequence: dims.x*dims.y*dims.z
    // bytes, 0 = solid/empty voxel, 1..255 = temperature linearly encoded
    // across [VtkFrameSequenceData.TempMin, TempMax].
    public class VoxelTemperatureFrame
    {
        public byte[] Voxels;

        public bool TryGetTemperature01(int voxelIndex, out float normalized01)
        {
            byte raw = Voxels[voxelIndex];
            if (raw == 0)
            {
                normalized01 = 0f;
                return false;
            }
            normalized01 = (raw - 1) / 254f;
            return true;
        }
    }

    // A voxelized time-sequence produced by Test/Generatives/make_frames.py
    // from a source VtkUnstructuredGridData (e.g. room.vtk): frames.raw +
    // frames_meta.json. Only temperature survives the voxelization — there's
    // no per-frame cell connectivity, pressure, or velocity to carry over.
    public class VtkFrameSequenceData : DataUnit
    {
        public Vector3Int Dims;
        public float VoxelSize;
        public Vector3 BoundsMin;
        public Vector3 BoundsMax;
        public float TempMin;
        public float TempMax;
        public float Fps;
        public float Duration;
        public int RackCount;
        public string Source;
        public VoxelTemperatureFrame[] Frames;
    }
}
