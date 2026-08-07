using UnityEngine;

namespace Monospark
{
    public class DataConvertManager : MonoBehaviour
    {
        public void GetMap<T>(DataConverter.OnProcessTex3DData tex3DData, string filepath) where T : DataConverter, new()
        {
            var converter = new T();
            converter.Init(filepath);
            converter.BuildData(tex3DData);
        }
        
        public void GetMap<T>(DataConverter.OnProcessBufferData tex3DData, string filepath) where T : DataConverter, new()
        {
            var converter = new T();
            converter.Init(filepath);
            converter.BuildData(tex3DData);
        }

        public void GetMap<T>(DataConverter.OnProcessFrameSequenceData frameSequenceData, string filepath) where T : DataConverter, new()
        {
            var converter = new T();
            converter.Init(filepath);
            converter.BuildData(frameSequenceData);
        }

        // Same as GetMap<T>(OnProcessTex3DData, ...), but for a converter the
        // caller already constructed -- e.g. to configure it first
        // (VtkUnstructuredGridReader.WorldUp) or to read a converter-specific
        // result afterward (VtkFrameReader.DataSize) that OnProcessTex3DData's
        // (Progress, Texture3D) signature has no room for.
        public void GetMap(DataConverter.OnProcessTex3DData tex3DData, string filepath, DataConverter converter)
        {
            converter.Init(filepath);
            converter.BuildData(tex3DData);
        }

        // Same as above, for the OnProcessBufferData overload.
        public void GetMap(DataConverter.OnProcessBufferData bufferData, string filepath, DataConverter converter)
        {
            converter.Init(filepath);
            converter.BuildData(bufferData);
        }
    }
}
