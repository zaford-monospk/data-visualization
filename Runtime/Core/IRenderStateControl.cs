using UnityEngine;

namespace Monospark
{
    public interface IRenderStateControl
    {
        public void SetVisibility(bool isVisible);
        public float SetMaterialFloat(string property,float value);
    }
}
