using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Monospark
{
    // Reads a voxelized temperature time-sequence produced by
    // Test/Generatives/make_frames.py: <FilePath>/frames.raw
    // (uint8[frames][nz][ny][nx]) + <FilePath>/frames_meta.json. FilePath
    // (set via DataConverter.Init) is the directory containing both files,
    // not a single file, unlike DataConverter's other implementations.
    public class VtkFrameSequenceReader : DataConverter
    {
        const string RawFileName = "frames.raw";
        const string MetaFileName = "frames_meta.json";

        VtkFrameSequenceData _data;

        // This reader produces a voxelized time sequence, not a single
        // Texture3D or unstructured-grid snapshot — that's VtkUnstructuredGridReader's job.
        public override void BuildData(OnProcessTex3DData callback)
        {
            throw new NotSupportedException(
                $"{nameof(VtkFrameSequenceReader)} does not produce a Texture3D directly; " +
                "convert a VoxelTemperatureFrame yourself, or use VtkUnstructuredGridReader.");
        }

        public override void BuildData(OnProcessBufferData callback)
        {
            throw new NotSupportedException(
                $"{nameof(VtkFrameSequenceReader)} does not produce a {nameof(VtkUnstructuredGridData)}; " +
                "use VtkUnstructuredGridReader instead.");
        }

        // Runs the blocking file read on a background thread so callers (e.g. a
        // MonoBehaviour's Start) never stall a frame, then hands the resulting
        // VtkFrameSequenceData back through the callback per DataConverter's async contract.
        public override async void BuildData(OnProcessFrameSequenceData callback)
        {
            try
            {
                callback?.Invoke(new Progress { Status = eStatus.ONPROGRESS, ProgressValue = 0f }, null);

                await EnsureParsedAsync();

                callback?.Invoke(new Progress { Status = eStatus.SUCCESS, ProgressValue = 1f }, _data);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                callback?.Invoke(new Progress { Status = eStatus.ERROR, ProgressValue = 0f }, null);
            }
        }

        // Only actually parses once; repeat calls reuse the cached result.
        Task EnsureParsedAsync()
        {
            if (_data != null)
                return Task.CompletedTask;

            string metaPath = Path.Combine(FilePath, MetaFileName);
            string rawPath = Path.Combine(FilePath, RawFileName);

            if (!File.Exists(metaPath))
                throw new FileNotFoundException($"Frame sequence metadata not found: {metaPath}", metaPath);
            if (!File.Exists(rawPath))
                throw new FileNotFoundException($"Frame sequence data not found: {rawPath}", rawPath);

            return Task.Run(() => Parse(metaPath, rawPath, CancellationToken.None));
        }

        void Parse(string metaPath, string rawPath, CancellationToken cancellationToken)
        {
            var meta = JsonUtility.FromJson<FrameSequenceMeta>(File.ReadAllText(metaPath));

            Debug.Log($"[VtkFrameSequenceReader] Reading '{rawPath}'\n" +
                       $"  Source : {meta.source}\n" +
                       $"  Dims   : {meta.dims[0]}x{meta.dims[1]}x{meta.dims[2]} @ {meta.voxelSize}m\n" +
                       $"  Frames : {meta.frames} ({meta.fps} fps, {meta.duration}s)\n" +
                       $"  Temp   : {meta.tempMin:F2}..{meta.tempMax:F2}\n" +
                       $"  Racks  : {meta.racks}");

            int nx = meta.dims[0], ny = meta.dims[1], nz = meta.dims[2];
            int frameSize = nx * ny * nz;
            long expectedLength = (long)frameSize * meta.frames;

            byte[] raw = File.ReadAllBytes(rawPath);
            if (raw.LongLength != expectedLength)
                throw new InvalidDataException(
                    $"{rawPath} is {raw.LongLength} bytes, expected {expectedLength} " +
                    $"({meta.frames} frames x {frameSize} voxels).");

            var frames = new VoxelTemperatureFrame[meta.frames];
            for (int f = 0; f < meta.frames; f++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var voxels = new byte[frameSize];
                Buffer.BlockCopy(raw, f * frameSize, voxels, 0, frameSize);
                frames[f] = new VoxelTemperatureFrame { Voxels = voxels };
            }

            Debug.Log($"[VtkFrameSequenceReader] Decoded {frames.Length} frames ({frameSize} voxels each)");

            _data = new VtkFrameSequenceData
            {
                Dims = new Vector3Int(nx, ny, nz),
                VoxelSize = meta.voxelSize,
                BoundsMin = new Vector3(meta.bboxMin[0], meta.bboxMin[1], meta.bboxMin[2]),
                BoundsMax = new Vector3(meta.bboxMax[0], meta.bboxMax[1], meta.bboxMax[2]),
                TempMin = meta.tempMin,
                TempMax = meta.tempMax,
                Fps = meta.fps,
                Duration = meta.duration,
                RackCount = meta.racks,
                Source = meta.source,
                Frames = frames
            };
        }

        [Serializable]
        class FrameSequenceMeta
        {
            public int[] dims;
            public float voxelSize;
            public float[] bboxMin;
            public float[] bboxMax;
            public float tempMin;
            public float tempMax;
            public int frames;
            public float fps;
            public float duration;
            public int racks;
            public string source;
        }
    }
}