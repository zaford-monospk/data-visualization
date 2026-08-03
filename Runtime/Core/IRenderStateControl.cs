using UnityEngine;

namespace Monospark
{
    public interface IRenderStateControl
    {
        public void SetVisibility(bool isVisible);
        public float SetMaterialFloat(string property,float value);
        public float GetMaterialFloat(string property);
        public void SetShader(Shader shader);
        public Shader GetShader();
    }
}
