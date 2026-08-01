using UnityEngine;

namespace Monospark
{
    public class TestAction : MonoBehaviour
    {
        public string filePath;
        public Texture3D CreatedTexture;
        public MeshRenderer TargetCube;

        public VtkUnstructuredGridRenderer VtkRenderer;
        
        public void ReadTargetFile(){
            filePath = Application.dataPath +            
                       "/Resources/Data/room.vtk"; 
           gameObject.AddComponent<DataConvertManager>().GetMap<VtkUnstructuredGridReader>(OnProcessData,filePath);
        }
        
        public void ReadTargetFileForInstance(){
            filePath = Application.dataPath +            
                       "/Resources/Data/room.vtk"; 
            gameObject.AddComponent<DataConvertManager>().GetMap<VtkUnstructuredGridReader>(OnProcessDataBuffer,filePath);
        }

        private void OnProcessData(DataConverter.Progress processData,Texture3D texture3D)
        {
            Debug.Log(processData.Status);
            if (processData.Status == DataConverter.eStatus.SUCCESS)
            {
                CreatedTexture = texture3D;
                TargetCube.material.SetTexture("_Volume", CreatedTexture);
                TargetCube.transform.localScale = new Vector3(texture3D.width, texture3D.height, texture3D.depth);
            }
        }

        private void OnProcessDataBuffer(DataConverter.Progress processData, VtkUnstructuredGridData structeredbuffer)
        {
            if(processData.Status == DataConverter.eStatus.SUCCESS)
                VtkRenderer.Set(structeredbuffer);
        }
    }
}
