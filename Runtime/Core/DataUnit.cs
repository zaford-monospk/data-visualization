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
}
