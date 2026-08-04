using System.IO;
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

        // Resolves relativePath against Application.streamingAssetsPath and
        // Inits with that — e.g. InitFromStreamingAssets("Data/room.vtk") for
        // a file at Assets/StreamingAssets/Data/room.vtk. Works on
        // Desktop/Editor/iOS, where StreamingAssets is a normal folder on
        // disk; on Android and WebGL it's packed inside the build (APK/JAR or
        // a virtual filesystem) and isn't reachable via plain File I/O, which
        // is what every current reader (StreamReader/File.*) uses — that would
        // need UnityWebRequest instead, a separate, larger change.
        public void InitFromStreamingAssets(string relativePath)
        {
            Init(Path.Combine(Application.streamingAssetsPath, relativePath));
        }

        public abstract void BuildData(OnProcessTex3DData callback);
        public abstract void BuildData(OnProcessBufferData callback);
        public abstract void BuildData(OnProcessFrameSequenceData callback);
    }
}
