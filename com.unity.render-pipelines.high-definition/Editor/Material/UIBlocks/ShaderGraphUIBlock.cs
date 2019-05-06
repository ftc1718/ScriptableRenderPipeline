using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering.HDPipeline;

namespace UnityEditor.Experimental.Rendering.HDPipeline
{
    public class ShaderGraphUIBlock : MaterialUIBlock
    {
        [Flags]
        public enum Features
        {
            MotionVector            = 1 << 0,
            EmissionGI              = 1 << 1,
            DiffusionProfileAsset   = 1 << 2,
            Unlit                   = MotionVector | EmissionGI,
            All                     = ~0,
        }

        protected static class Styles
        {
            public static readonly string header = "Shader Graph";
        }

        Expandable  m_ExpandableBit;
        Features    m_Features;

        public ShaderGraphUIBlock(Expandable expandableBit = Expandable.ShaderGraph, Features features = Features.All)
        {
            m_ExpandableBit = expandableBit;
            m_Features = features;
        }

        public override void LoadMaterialProperties() {}

        public override void OnGUI()
        {
            using (var header = new MaterialHeaderScope(Styles.header, (uint)m_ExpandableBit, materialEditor))
            {
                if (header.expanded)
                    DrawShaderGraphGUI();
            }
        }

        void DrawShaderGraphGUI()
        {
            materialEditor.PropertiesDefaultGUI(properties);

            if ((m_Features & Features.EmissionGI) != 0)
                DrawEmissionGI();

            if ((m_Features & Features.MotionVector) != 0)
                DrawMotionVectorToggle();

            if ((m_Features & Features.DiffusionProfileAsset) != 0)
                DrawDiffusionProfileUI();
        }

        void DrawEmissionGI()
        {
            if (materialEditor.EmissionEnabledProperty())
            {
                materialEditor.LightmapEmissionFlagsProperty(MaterialEditor.kMiniTextureFieldLabelIndentLevel, true, true);
            }
        }

        void DrawMotionVectorToggle()
        {
            // I absolutely don't know what this is meant to do
            const string materialTag = "MotionVector";
            foreach (var material in materials)
            {
                string tag = material.GetTag(materialTag, false, "Nothing");
                if (tag == "Nothing")
                {
                    material.SetShaderPassEnabled(HDShaderPassNames.s_MotionVectorsStr, false);
                    material.SetOverrideTag(materialTag, "User");
                }
            }

            // If using multi-select, apply toggled material to all materials.
            bool enabled = ((Material)materialEditor.target).GetShaderPassEnabled(HDShaderPassNames.s_MotionVectorsStr);
            EditorGUI.BeginChangeCheck();
            enabled = EditorGUILayout.Toggle("Motion Vector For Vertex Animation", enabled);
            if (EditorGUI.EndChangeCheck())
            {
                foreach (var obj in materialEditor.targets)
                {
                    var material = (Material)obj;
                    material.SetShaderPassEnabled(HDShaderPassNames.s_MotionVectorsStr, enabled);
                }
            }
        }

        void DrawDiffusionProfileUI()
        {
            if (DiffusionProfileMaterialUI.IsSupported(materialEditor))
                DiffusionProfileMaterialUI.OnGUI(FindProperty("_DiffusionProfileAsset"), FindProperty("_DiffusionProfileHash"));
        }
    }
}