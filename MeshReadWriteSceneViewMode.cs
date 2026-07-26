using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class MeshReadWriteSceneViewMode : ScriptableSingleton<MeshReadWriteSceneViewMode>
{
    private const string ModeName = "Mesh Read/Write";
    private const string ModeSection = "Debug";

    private static readonly int DebugColorId = Shader.PropertyToID("_DebugColor");
    private static readonly Color ReadableColor = new Color(0.15f, 0.9f, 0.3f, 1f);
    private static readonly Color NotReadableColor = new Color(0.95f, 0.2f, 0.15f, 1f);
    private static readonly Color UnsupportedColor = new Color(0.45f, 0.45f, 0.45f, 1f);

    private const string RuntimeShaderSource = @"
Shader ""Hidden/URP/SceneView/MeshReadWriteRuntime""
{
    Properties
    {
        _DebugColor (""Debug Color"", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags { ""RenderPipeline"" = ""UniversalPipeline"" }

        Pass
        {
            Name ""MeshReadWrite""
            Tags { ""LightMode"" = ""UniversalForward"" }
            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex Vertex
            #pragma fragment Fragment

            #include ""Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl""

            CBUFFER_START(UnityPerMaterial)
                float4 _DebugColor;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            Varyings Vertex(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS);
                return output;
            }

            half4 Fragment(Varyings input) : SV_Target
            {
                return _DebugColor;
            }
            ENDHLSL
        }
    }
}";

    private readonly Dictionary<Renderer, MaterialPropertyBlock> _originalBlocks =
        new Dictionary<Renderer, MaterialPropertyBlock>();
    private readonly HashSet<SceneView> _activeViews = new HashSet<SceneView>();

    private Shader _runtimeShader;
    private Shader _originalDebugShader;
    private UniversalRenderPipelineDebugShaders _debugShaders;
    private UniversalRenderPipelineDebugDisplaySettings _debugSettings;
    private DebugVertexAttributeMode _originalVertexAttributeMode;
    private bool _debugShaderInstalled;
    private bool _refreshRequested = true;
    private bool _debugBlocksApplied;

    [InitializeOnLoadMethod]
    private static void Initialize()
    {
        _ = instance;
    }

    private void OnEnable()
    {
        SceneView.AddCameraMode(ModeName, ModeSection);
        SceneView.duringSceneGui += OnSceneGUI;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
        EditorApplication.hierarchyChanged += RequestRefresh;
        EditorApplication.projectChanged += RequestRefresh;
        Undo.undoRedoPerformed += RequestRefresh;
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        EditorApplication.hierarchyChanged -= RequestRefresh;
        EditorApplication.projectChanged -= RequestRefresh;
        Undo.undoRedoPerformed -= RequestRefresh;

        _activeViews.Clear();
        StopDebugMode();
    }

    private void RequestRefresh()
    {
        _refreshRequested = true;
        if (_activeViews.Count > 0)
        {
            SceneView.RepaintAll();
        }
    }

    private void OnSceneGUI(SceneView view)
    {
        if (IsActive(view))
        {
            _activeViews.Add(view);
            EnsureDebugShaderInstalled();

            if (_refreshRequested || !_debugBlocksApplied)
            {
                ApplyDebugBlocks();
            }

            return;
        }

        _activeViews.Remove(view);
        RemoveClosedViews();
        if (_activeViews.Count == 0)
        {
            StopDebugMode();
        }
    }

    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!_debugShaderInstalled)
        {
            return;
        }

        if (_activeViews.Count > 0 && camera.cameraType == CameraType.SceneView)
        {
            if (_refreshRequested || !_debugBlocksApplied)
            {
                ApplyDebugBlocks();
            }

            _debugSettings.materialSettings.vertexAttributeDebugMode = DebugVertexAttributeMode.Texcoord0;
        }
        else
        {
            _debugSettings.materialSettings.vertexAttributeDebugMode = DebugVertexAttributeMode.None;
        }
    }

    private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (_debugShaderInstalled)
        {
            _debugSettings.materialSettings.vertexAttributeDebugMode = DebugVertexAttributeMode.None;
        }
    }

    private void EnsureDebugShaderInstalled()
    {
        if (_debugShaderInstalled)
        {
            return;
        }

        if (!GraphicsSettings.TryGetRenderPipelineSettings<UniversalRenderPipelineDebugShaders>(out UniversalRenderPipelineDebugShaders debugShaders))
        {
            return;
        }

        _debugSettings = UniversalRenderPipelineDebugDisplaySettings.Instance;
        _debugShaders = debugShaders;
        _originalDebugShader = _debugShaders.debugReplacementPS;
        _originalVertexAttributeMode = _debugSettings.materialSettings.vertexAttributeDebugMode;
        _runtimeShader = ShaderUtil.CreateShaderAsset(RuntimeShaderSource, true);
        if (_runtimeShader == null)
        {
            _debugSettings = null;
            _debugShaders = null;
            return;
        }

        _runtimeShader.hideFlags = HideFlags.HideAndDontSave;
        _debugShaders.debugReplacementPS = _runtimeShader;
        InvalidateRenderers();
        _debugShaderInstalled = true;
    }

    private void StopDebugMode()
    {
        RestoreDebugBlocks();

        if (_debugSettings != null)
        {
            _debugSettings.materialSettings.vertexAttributeDebugMode = _originalVertexAttributeMode;
        }

        if (_debugShaderInstalled && _debugShaders != null)
        {
            _debugShaders.debugReplacementPS = _originalDebugShader;
            InvalidateRenderers();
        }

        if (_runtimeShader != null)
        {
            Object.DestroyImmediate(_runtimeShader);
        }

        _runtimeShader = null;
        _debugShaders = null;
        _debugSettings = null;
        _debugShaderInstalled = false;
    }

    private static void InvalidateRenderers()
    {
        if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipelineAsset))
        {
            return;
        }

        foreach (ScriptableRendererData rendererData in pipelineAsset.rendererDataList)
        {
            if (rendererData != null)
            {
                rendererData.SetDirty();
            }
        }
    }

    private void RemoveClosedViews()
    {
        _activeViews.RemoveWhere(view => view == null);
    }

    private static bool IsActive(SceneView view)
    {
        SceneView.CameraMode cameraMode = view.cameraMode;
        return cameraMode.drawMode == DrawCameraMode.UserDefined &&
            cameraMode.name == ModeName &&
            cameraMode.section == ModeSection;
    }

    private void ApplyDebugBlocks()
    {
        RestoreDebugBlocks();

        Renderer[] renderers = Object.FindObjectsByType<Renderer>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None);
        foreach (Renderer renderer in renderers)
        {
            if (!IsSceneRenderer(renderer))
            {
                continue;
            }

            Color color = UnsupportedColor;
            if (TryGetMesh(renderer, out Mesh mesh))
            {
                color = mesh.isReadable ? ReadableColor : NotReadableColor;
            }

            CaptureAndApply(renderer, color);
        }

        _debugBlocksApplied = true;
        _refreshRequested = false;
    }

    private static bool IsSceneRenderer(Renderer renderer)
    {
        return renderer != null &&
            renderer.gameObject.scene.IsValid() &&
            renderer.gameObject.scene.isLoaded &&
            renderer.gameObject.activeInHierarchy;
    }

    private static bool TryGetMesh(Renderer renderer, out Mesh mesh)
    {
        if (renderer is SkinnedMeshRenderer skinnedRenderer)
        {
            mesh = skinnedRenderer.sharedMesh;
            return mesh != null;
        }

        if (renderer is MeshRenderer)
        {
            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
            mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            return mesh != null;
        }

        mesh = null;
        return false;
    }

    private void CaptureAndApply(Renderer renderer, Color color)
    {
        if (!_originalBlocks.ContainsKey(renderer))
        {
            MaterialPropertyBlock originalBlock = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(originalBlock);
            _originalBlocks.Add(renderer, originalBlock);
        }

        MaterialPropertyBlock debugBlock = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(debugBlock);
        debugBlock.SetColor(DebugColorId, color);
        renderer.SetPropertyBlock(debugBlock);
    }

    private void RestoreDebugBlocks()
    {
        foreach (KeyValuePair<Renderer, MaterialPropertyBlock> entry in _originalBlocks)
        {
            if (entry.Key != null)
            {
                entry.Key.SetPropertyBlock(entry.Value);
            }
        }

        _originalBlocks.Clear();
        _debugBlocksApplied = false;
    }
}
