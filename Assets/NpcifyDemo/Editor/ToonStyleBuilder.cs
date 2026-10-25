using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using VRM10.MToon10;

namespace NpcifyDemo.Editor
{
    /// <summary>
    /// Gives the demo an anime look: cel-shaded materials for the level and the player,
    /// ink lines on every edge, MToon outlines on the VRM characters, and a clean color grade.
    /// The VRM characters keep their own MToon materials.
    /// </summary>
    public static class ToonStyleBuilder
    {
        private const string RenderingFolder = "Assets/NpcifyDemo/Rendering";
        private const string MaterialsFolder = RenderingFolder + "/Materials";
        // Copies of the replaced Lit materials. They keep the Lit shader variants in the build
        // for the PBR materials of a VRM. "Always Included Shaders" would compile every variant.
        private const string LitVariantsFolder = RenderingFolder + "/Resources/LitVariants";
        private const string InkMaterialPath = RenderingFolder + "/InkOutline.mat";
        private const string VolumeProfilePath = RenderingFolder + "/ToonVolume.asset";
        private const string StencilMaterialPath = RenderingFolder + "/CharacterStencil.mat";
        private const string InkFeatureName = "Npcify Ink Outline";
        private const string CharacterInkFeatureName = "Npcify Ink Outline (characters)";
        private const string CharacterStencilFeatureName = "Npcify Character Stencil";

        /// <summary>The layer of the VRM characters (<see cref="NpcifyNpc.CharacterLayerName"/>).</summary>
        public const int CharacterLayer = 8;
        private const string MToonOutlineFeatureName = "MToon Outline";

        private static readonly string[] RendererPaths =
        {
            "Assets/SourceFiles/Settings/PC_Renderer.asset",
            "Assets/SourceFiles/Settings/Mobile_Renderer.asset",
        };

        private static readonly HashSet<string> ConvertedShaders = new HashSet<string>
        {
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Simple Lit",
            "Universal Render Pipeline/Complex Lit",
            "Universal Render Pipeline/Baked Lit",
        };

        /// <summary>Converts the materials of the open scene and adds the global toon volume.</summary>
        public static void ApplyToScene()
        {
            System.IO.Directory.CreateDirectory(MaterialsFolder);
            var toonShader = Shader.Find("NpcifyDemo/Toon Lit");
            var converted = new Dictionary<Material, Material>();
            foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var materials = renderer.sharedMaterials;
                var changed = false;
                for (var i = 0; i < materials.Length; i++)
                {
                    var source = materials[i];
                    if (source == null || !ConvertedShaders.Contains(source.shader.name))
                    {
                        continue;
                    }
                    if (!converted.TryGetValue(source, out var toon))
                    {
                        toon = MakeToonMaterial(source, toonShader);
                        converted[source] = toon;
                        KeepLitVariants(source);
                    }
                    materials[i] = toon;
                    changed = true;
                }
                if (changed)
                {
                    renderer.sharedMaterials = materials;
                    EditorUtility.SetDirty(renderer);
                }
            }

            var volume = new GameObject("Toon Volume").AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 10f;
            volume.sharedProfile = MakeVolumeProfile();
            Debug.Log($"[Npcify demo] Toon style: {converted.Count} material(s) converted");
        }

        /// <summary>Adds the ink outline and MToon outline passes to the URP renderers.</summary>
        public static void ConfigureRenderers()
        {
            var inkMaterial = AssetDatabase.LoadAssetAtPath<Material>(InkMaterialPath);
            if (inkMaterial == null)
            {
                inkMaterial = new Material(Shader.Find("NpcifyDemo/Ink Outline"));
                AssetDatabase.CreateAsset(inkMaterial, InkMaterialPath);
            }
            // The shader defaults are the settings. Keep the asset (the renderers refer to it).
            inkMaterial.CopyPropertiesFromMaterial(new Material(inkMaterial.shader));
            EditorUtility.SetDirty(inkMaterial);

            var stencilMaterial = AssetDatabase.LoadAssetAtPath<Material>(StencilMaterialPath);
            if (stencilMaterial == null)
            {
                stencilMaterial = new Material(Shader.Find("NpcifyDemo/Character Stencil"));
                AssetDatabase.CreateAsset(stencilMaterial, StencilMaterialPath);
            }
            NameCharacterLayer();

            foreach (var path in RendererPaths)
            {
                var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(path);
                if (data == null)
                {
                    continue;
                }
                foreach (var feature in data.rendererFeatures)
                {
                    // Ambient occlusion makes soft gray corners, which is not a cel look.
                    if (feature is ScreenSpaceAmbientOcclusion)
                    {
                        feature.SetActive(false);
                    }
                }

                // 1. Characters write stencil 1, so that the ink passes can tell them apart.
                var stencil = GetOrAddFeature<RenderObjects>(data, CharacterStencilFeatureName);
                stencil.settings.Event = RenderPassEvent.AfterRenderingOpaques;
                stencil.settings.filterSettings.RenderQueueType = RenderQueueType.Opaque;
                stencil.settings.filterSettings.LayerMask = 1 << CharacterLayer;
                stencil.settings.overrideMode = RenderObjects.RenderObjectsSettings.OverrideMaterialMode.Material;
                stencil.settings.overrideMaterial = stencilMaterial;
                stencil.settings.overrideMaterialPassIndex = 0;

                // 2. Ink lines: depth and normal edges on the level, depth edges only on the
                //    characters (normal edges draw a line on every hair strand and face shape).
                ConfigureInk(GetOrAddFeature<FullScreenPassRendererFeature>(data, InkFeatureName), inkMaterial, 0);
                ConfigureInk(GetOrAddFeature<FullScreenPassRendererFeature>(data, CharacterInkFeatureName), inkMaterial, 1);

                GetOrAddFeature<MToonOutlineRenderFeature>(data, MToonOutlineFeatureName);
                EditorUtility.SetDirty(data);
            }
            AssetDatabase.SaveAssets();
        }

        private static void KeepLitVariants(Material source)
        {
            System.IO.Directory.CreateDirectory(LitVariantsFolder);
            var sourcePath = AssetDatabase.GetAssetPath(source);
            var copyPath = $"{LitVariantsFolder}/{source.name}.mat";
            if (!string.IsNullOrEmpty(sourcePath) && AssetDatabase.LoadAssetAtPath<Material>(copyPath) == null)
            {
                AssetDatabase.CopyAsset(sourcePath, copyPath);
            }
        }

        private static Material MakeToonMaterial(Material source, Shader toonShader)
        {
            var path = $"{MaterialsFolder}/{source.name}_Toon.mat";
            var toon = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (toon == null)
            {
                toon = new Material(toonShader);
                AssetDatabase.CreateAsset(toon, path);
            }
            toon.shader = toonShader;
            if (source.HasProperty("_BaseMap"))
            {
                toon.SetTexture("_BaseMap", source.GetTexture("_BaseMap"));
                toon.SetTextureScale("_BaseMap", source.GetTextureScale("_BaseMap"));
                toon.SetTextureOffset("_BaseMap", source.GetTextureOffset("_BaseMap"));
            }
            if (source.HasProperty("_BaseColor"))
            {
                toon.SetColor("_BaseColor", source.GetColor("_BaseColor"));
            }
            if (source.HasProperty("_EmissionColor") && source.IsKeywordEnabled("_EMISSION"))
            {
                toon.SetColor("_EmissionColor", source.GetColor("_EmissionColor"));
            }
            EditorUtility.SetDirty(toon);
            return toon;
        }

        private static VolumeProfile MakeVolumeProfile()
        {
            // Made again on each build, so that the values below are the settings.
            AssetDatabase.DeleteAsset(VolumeProfilePath);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, VolumeProfilePath);

            var tonemapping = profile.Add<Tonemapping>(true);
            tonemapping.mode.Override(TonemappingMode.Neutral);
            var color = profile.Add<ColorAdjustments>(true);
            color.saturation.Override(18f);
            color.contrast.Override(6f);
            color.postExposure.Override(-0.1f);
            var bloom = profile.Add<Bloom>(true);
            bloom.threshold.Override(1.2f);
            bloom.intensity.Override(0.35f);
            bloom.scatter.Override(0.6f);
            var vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0f);
            foreach (var component in profile.components)
            {
                AssetDatabase.AddObjectToAsset(component, profile);
            }
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();
            return profile;
        }

        private static T GetOrAddFeature<T>(ScriptableRendererData data, string name) where T : ScriptableRendererFeature
        {
            foreach (var feature in data.rendererFeatures)
            {
                if (feature is T typed && feature.name == name)
                {
                    return typed;
                }
            }
            var created = ScriptableObject.CreateInstance<T>();
            created.name = name;
            AddFeature(data, created);
            return created;
        }

        private static void ConfigureInk(FullScreenPassRendererFeature ink, Material material, int passIndex)
        {
            ink.injectionPoint = FullScreenPassRendererFeature.InjectionPoint.BeforeRenderingPostProcessing;
            ink.requirements = ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal;
            ink.fetchColorBuffer = true;
            // The passes test the character stencil.
            ink.bindDepthStencilAttachment = true;
            ink.passMaterial = material;
            ink.passIndex = passIndex;
        }

        private static void NameCharacterLayer()
        {
            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("layers");
            layers.GetArrayElementAtIndex(CharacterLayer).stringValue = NpcifyNpc.CharacterLayerName;
            tagManager.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AddFeature(ScriptableRendererData data, ScriptableRendererFeature feature)
        {
            AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
            var serialized = new SerializedObject(data);
            var features = serialized.FindProperty("m_RendererFeatures");
            features.InsertArrayElementAtIndex(features.arraySize);
            features.GetArrayElementAtIndex(features.arraySize - 1).objectReferenceValue = feature;
            var map = serialized.FindProperty("m_RendererFeatureMap");
            map.InsertArrayElementAtIndex(map.arraySize);
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
