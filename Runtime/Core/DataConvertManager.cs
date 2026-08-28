using UnityEngine;

namespace Monospark
{
    public class DataConvertManager : MonoBehaviour
    {
        public void GetMap<T>(DataConverter.OnProcessTex3DData tex3DData, string filepath, DataPathMode pathMode = DataPathMode.Disk) where T : DataConverter, new()
        {
            var converter = new T();
            converter.InitFromPath(filepath, pathMode);
            converter.BuildData(tex3DData);
        }

        public void GetMap<T>(DataConverter.OnProcessBufferData tex3DData, string filepath, DataPathMode pathMode = DataPathMode.Disk) where T : DataConverter, new()
        {
            var converter = new T();
            converter.InitFromPath(filepath, pathMode);
            converter.BuildData(tex3DData);
        }

        public void GetMap<T>(DataConverter.OnProcessFrameSequenceData frameSequenceData, string filepath, DataPathMode pathMode = DataPathMode.Disk) where T : DataConverter, new()
        {
            var converter = new T();
            converter.InitFromPath(filepath, pathMode);
            converter.BuildData(frameSequenceData);
        }

        // Same as GetMap<T>(OnProcessTex3DData, ...), but for a converter the
        // caller already constructed -- e.g. to configure it first
        // (VtkUnstructuredGridReader.WorldUp) or to read a converter-specific
        // result afterward (VtkFrameReader.DataSize) that OnProcessTex3DData's
        // (Progress, Texture3D) signature has no room for.
        public void GetMap(DataConverter.OnProcessTex3DData tex3DData, string filepath, DataConverter converter, DataPathMode pathMode = DataPathMode.Disk)
        {
            converter.InitFromPath(filepath, pathMode);
            converter.BuildData(tex3DData);
        }

        // Same as above, for the OnProcessBufferData overload.
        public void GetMap(DataConverter.OnProcessBufferData bufferData, string filepath, DataConverter converter, DataPathMode pathMode = DataPathMode.Disk)
        {
            converter.InitFromPath(filepath, pathMode);
            converter.BuildData(bufferData);
        }

        // Same as above, for the OnProcessTex2DData overload (VtkFrameReader's
        // direct-2D-slice output -- see its BuildData(OnProcessTex2DData)).
        public void GetMap(DataConverter.OnProcessTex2DData tex2DData, string filepath, DataConverter converter, DataPathMode pathMode = DataPathMode.Disk)
        {
            converter.InitFromPath(filepath, pathMode);
            converter.BuildData(tex2DData);
        }
    }
}
