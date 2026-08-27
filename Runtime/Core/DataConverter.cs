using System.IO;
using UnityEngine;

namespace Monospark
{
    // Which of DataConverter's Init family a dataPath argument should be routed
    // through -- lets a caller (e.g. CFDFactory.CreateVolumeStatic) pick the
    // source kind with one flag instead of juggling separate Create.../CreateXFromStreamingAssets
    // overloads. See DataConverter.InitFromPath.
    public enum DataPathMode
    {
        Disk,            // path is used as-is -- Init(path).
        StreamingAssets, // path is resolved against Application.streamingAssetsPath -- InitFromStreamingAssets(path).
        Addressable,     // path is an Addressable address/key -- InitFromAddressable(path). Currently only VtkFrameReader's BuildData honors AddressableKey.
    }

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

        // Address/key of an Addressable asset to load instead of a plain
        // FilePath -- set by InitFromAddressable. Only VtkFrameReader currently
        // reads this; every other converter still expects Init/InitFromStreamingAssets.
        public string AddressableKey { get; private set; }

        // Marks this instance to load its data through Unity's Addressable Asset
        // System instead of plain File I/O -- unlike InitFromStreamingAssets,
        // this reaches content packed into a remote/local Addressable bundle on
        // every platform (including Android/WebGL, where StreamingAssets isn't a
        // reachable folder). `address` is the key/label the asset was given in
        // the Addressables Groups window, not a filesystem path.
        public void InitFromAddressable(string address)
        {
            AddressableKey = address;
        }

        // Dispatches to Init/InitFromStreamingAssets/InitFromAddressable based
        // on mode -- lets a caller hold a single string + DataPathMode pair
        // (e.g. a factory method's dataPath/pathMode arguments) instead of
        // picking which Init* overload to call itself.
        public void InitFromPath(string path, DataPathMode mode)
        {
            switch (mode)
            {
                case DataPathMode.StreamingAssets:
                    InitFromStreamingAssets(path);
                    break;
                case DataPathMode.Addressable:
                    InitFromAddressable(path);
                    break;
                default:
                    Init(path);
                    break;
            }
        }

        public abstract void BuildData(OnProcessTex3DData callback);
        public abstract void BuildData(OnProcessBufferData callback);
        public abstract void BuildData(OnProcessFrameSequenceData callback);
    }
}
