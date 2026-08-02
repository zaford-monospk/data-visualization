using UnityEngine;

namespace Monospark
{
    public abstract class DataConverter
    {
        public class Progress
        {
            public eStatus Status;
            public float ProgressValue;
        }
        public enum eStatus
        {
            ONPROGRESS,
            SUCCESS,
            ERROR,
        }
        
        public delegate void OnProcessTex3DData(Progress progress,Texture3D texture3D);
        public delegate void OnProcessBufferData(Progress progress,VtkUnstructuredGridData buffer);
        public delegate void OnProcessFrameSequenceData(Progress progress,VtkFrameSequenceData sequence);

        public string FilePath { get; private set; }

        public void Init(string filePath)
        {
            FilePath = filePath;
        }

        public abstract void BuildData(OnProcessTex3DData callback);
        public abstract void BuildData(OnProcessBufferData callback);
        public abstract void BuildData(OnProcessFrameSequenceData callback);
    }
}
