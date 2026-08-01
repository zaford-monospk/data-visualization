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
    }
}
