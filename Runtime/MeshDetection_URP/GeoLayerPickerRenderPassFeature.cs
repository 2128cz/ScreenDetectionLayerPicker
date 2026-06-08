using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEngine.Experimental.Rendering;
using XericLibrary.Runtime.MacroLibrary;

#if UNITY_2022_1_OR_NEWER
namespace XericLibrary.PostProcess
{
    /// <summary>
    /// 用于存储展平后的 SubMesh 渲染数据
    /// </summary>
    [System.Serializable]
    public struct SubMeshDrawData
    {
        public Mesh mesh;
        public Matrix4x4 matrix;
        public int subMeshIndex;
        public int uniqueMatID;
        public Material material;
    }

    /// <summary>
    /// 解码后的地质层拾取数据
    /// </summary>
    [System.Serializable]
    public struct PickedGeoData
    {
        public int matID;
        public Vector3 worldPos;
        public bool isValid;

        public Material GetMaterial()
        {
            if (GeoLayerPickerRenderPassFeature.Instance != null)
            {
                return GeoLayerPickerRenderPassFeature.Instance.GetMaterialByID(matID);
            }
            return null;
        }

        public string GetMaterialName()
        {
            var mat = GetMaterial();
            return mat != null ? mat.name : "Unknown";
        }
    }

    /// <summary>
    /// 材质统计结果
    /// </summary>
    [System.Serializable]
    public struct MaterialStatResult
    {
        public int matID;
        public float location;
        public float percentage;
        public int pixelCount;

        public Material GetMaterial()
        {
            if (GeoLayerPickerRenderPassFeature.Instance != null)
            {
                return GeoLayerPickerRenderPassFeature.Instance.GetMaterialByID(matID);
            }
            return null;
        }

        public string GetMaterialName()
        {
            var mat = GetMaterial();
            return mat != null ? mat.name : "Unknown";
        }
    }

    /// <summary>
    /// 地质层拾取 RenderFeature。
    /// </summary>
    public class GeoLayerPickerRenderPassFeature : ScriptableRendererFeature
    {
        #region 静态单例与外部管理 API

        public static GeoLayerPickerRenderPassFeature Instance { get; private set; }

        private readonly List<MeshFilter> m_GeoMeshes = new List<MeshFilter>();
        private readonly List<SubMeshDrawData> m_DrawList = new List<SubMeshDrawData>();
        private readonly Dictionary<Material, int> m_MaterialToID = new Dictionary<Material, int>();
        private readonly Dictionary<int, Material> m_IDToMaterial = new Dictionary<int, Material>();
        private bool m_IsDirty = true;

        public IReadOnlyDictionary<Material, int> MaterialToID => m_MaterialToID;
        public IReadOnlyDictionary<int, Material> IDToMaterial => m_IDToMaterial;

        public Material GetMaterialByID(int matID)
        {
            if (m_IDToMaterial.TryGetValue(matID, out var material))
            {
                return material;
            }
            return null;
        }

        public int GetIDByMaterial(Material material)
        {
            if (material != null && m_MaterialToID.TryGetValue(material, out var id))
            {
                return id;
            }
            return -1;
        }

        public void AddGeoMesh(MeshFilter meshFilter)
        {
            if (meshFilter != null && !m_GeoMeshes.Contains(meshFilter))
            {
                m_GeoMeshes.Add(meshFilter);
                m_IsDirty = true;
            }
        }

        public void AddGeoMeshes(IEnumerable<MeshFilter> meshFilters)
        {
            if (meshFilters == null) return;
            foreach (var mf in meshFilters)
            {
                if (mf != null && !m_GeoMeshes.Contains(mf))
                {
                    m_GeoMeshes.Add(mf);
                    m_IsDirty = true;
                }
            }
        }

        public void RemoveGeoMesh(MeshFilter meshFilter)
        {
            if (m_GeoMeshes.Remove(meshFilter)) m_IsDirty = true;
        }

        public void ClearGeoMeshes()
        {
            if (m_GeoMeshes.Count > 0)
            {
                m_GeoMeshes.Clear();
                m_IsDirty = true;
            }
        }

        public IReadOnlyList<MeshFilter> GetGeoMeshes() => m_GeoMeshes;

        #endregion

        #region 数据访问 API

        private PickedGeoData[] m_DecodedDataArray;
        private int m_ValidDataCount = 0;

        public IReadOnlyList<PickedGeoData> GetDecodedData() => m_DecodedDataArray;

        public void GetColumnData(int column, List<PickedGeoData> listData)
        {
            listData.Clear();
            if (m_DecodedDataArray == null) return;
            column = MacroMath.ClampToInt(column, 0, pickRTSize.x);
            for (int i = 0; i < pickRTSize.y; i++)
            {
                listData.Add(m_DecodedDataArray[pickRTSize.x * i + column]);
            }
        }

        public bool TryGetPickedData(Vector2 normalizedScreenPos, out PickedGeoData data)
        {
            data = default;
            if (m_DecodedDataArray == null || m_DecodedDataArray.Length == 0) return false;

            float localX = (normalizedScreenPos.x - sampleRect.x) / sampleRect.width;
            float localY = (normalizedScreenPos.y - sampleRect.y) / sampleRect.height;

            if (localX < 0f || localX > 1f || localY < 0f || localY > 1f) return false;

            int pixelX = Mathf.Clamp(Mathf.FloorToInt(localX * pickRTSize.x), 0, pickRTSize.x - 1);
            int pixelY = Mathf.Clamp(Mathf.FloorToInt(localY * pickRTSize.y), 0, pickRTSize.y - 1);

            int index = pixelY * pickRTSize.x + pixelX;
            if (index < 0 || index >= m_DecodedDataArray.Length) return false;

            data = m_DecodedDataArray[index];
            return true;
        }

        public void ForceReadbackSync()
        {
            // 修复：使用 m_ReadbackBufferHandle 进行同步等待
            if (m_ReadbackBufferHandle != null)
            {
                AsyncGPUReadback.WaitAllRequests();
            }
        }

        #endregion
        
        #region 材质统计 API

        public void GetVerticalMaterialStats(List<MaterialStatResult> results, float startPos = 0f, float endPos = 1f)
        {
            results.Clear();
            if (m_DecodedDataArray == null || m_DecodedDataArray.Length == 0) return;

            startPos = Mathf.Clamp01(startPos);
            endPos = Mathf.Clamp01(endPos);
            if (startPos > endPos)
            {
                float temp = startPos;
                startPos = endPos;
                endPos = temp;
            }

            int startPixelY = Mathf.FloorToInt(startPos * (pickRTSize.y - 1));
            int endPixelY = Mathf.FloorToInt(endPos * (pickRTSize.y - 1));

            Dictionary<int, List<float>> matIDToPositions = new Dictionary<int, List<float>>();
            Dictionary<int, int> matIDToCount = new Dictionary<int, int>();
            int totalValidPixels = 0;

            for (int y = startPixelY; y <= endPixelY; y++)
            {
                for (int x = 0; x < pickRTSize.x; x++)
                {
                    int index = y * pickRTSize.x + x;
                    if (index < 0 || index >= m_DecodedDataArray.Length) continue;

                    var data = m_DecodedDataArray[index];
                    if (!data.isValid) continue;

                    float normalizedY = (float)y / (pickRTSize.y - 1);

                    if (!matIDToPositions.ContainsKey(data.matID))
                    {
                        matIDToPositions[data.matID] = new List<float>();
                        matIDToCount[data.matID] = 0;
                    }
                    matIDToPositions[data.matID].Add(normalizedY);
                    matIDToCount[data.matID]++;
                    totalValidPixels++;
                }
            }

            foreach (var kvp in matIDToPositions)
            {
                int matID = kvp.Key;
                var positions = kvp.Value;
                float avgLocation = positions.Average();
                float percentage = totalValidPixels > 0 ? (float)matIDToCount[matID] / totalValidPixels * 100f : 0f;

                results.Add(new MaterialStatResult
                {
                    matID = matID,
                    location = avgLocation,
                    percentage = percentage,
                    pixelCount = matIDToCount[matID]
                });
            }

            results.Sort((a, b) => a.location.CompareTo(b.location));
        }

        public void GetHorizontalMaterialStats(List<MaterialStatResult> results, float startPos = 0f, float endPos = 1f)
        {
            results.Clear();
            if (m_DecodedDataArray == null || m_DecodedDataArray.Length == 0) return;

            startPos = Mathf.Clamp01(startPos);
            endPos = Mathf.Clamp01(endPos);
            if (startPos > endPos)
            {
                float temp = startPos;
                startPos = endPos;
                endPos = temp;
            }

            int startPixelX = Mathf.FloorToInt(startPos * (pickRTSize.x - 1));
            int endPixelX = Mathf.FloorToInt(endPos * (pickRTSize.x - 1));

            Dictionary<int, List<float>> matIDToPositions = new Dictionary<int, List<float>>();
            Dictionary<int, int> matIDToCount = new Dictionary<int, int>();
            int totalValidPixels = 0;

            for (int x = startPixelX; x <= endPixelX; x++)
            {
                for (int y = 0; y < pickRTSize.y; y++)
                {
                    int index = y * pickRTSize.x + x;
                    if (index < 0 || index >= m_DecodedDataArray.Length) continue;

                    var data = m_DecodedDataArray[index];
                    if (!data.isValid) continue;

                    float normalizedX = (float)x / (pickRTSize.x - 1);

                    if (!matIDToPositions.ContainsKey(data.matID))
                    {
                        matIDToPositions[data.matID] = new List<float>();
                        matIDToCount[data.matID] = 0;
                    }
                    matIDToPositions[data.matID].Add(normalizedX);
                    matIDToCount[data.matID]++;
                    totalValidPixels++;
                }
            }

            foreach (var kvp in matIDToPositions)
            {
                int matID = kvp.Key;
                var positions = kvp.Value;
                float avgLocation = positions.Average();
                float percentage = totalValidPixels > 0 ? (float)matIDToCount[matID] / totalValidPixels * 100f : 0f;

                results.Add(new MaterialStatResult
                {
                    matID = matID,
                    location = avgLocation,
                    percentage = percentage,
                    pixelCount = matIDToCount[matID]
                });
            }

            results.Sort((a, b) => a.location.CompareTo(b.location));
        }

        public void GetDominantMaterialByVerticalSlice(List<MaterialStatResult> results, int sliceCount = 10, float startPos = 0f, float endPos = 1f)
        {
            results.Clear();
            if (m_DecodedDataArray == null || m_DecodedDataArray.Length == 0) return;

            sliceCount = Mathf.Max(1, sliceCount);
            startPos = Mathf.Clamp01(startPos);
            endPos = Mathf.Clamp01(endPos);
            if (startPos > endPos)
            {
                float temp = startPos;
                startPos = endPos;
                endPos = temp;
            }

            int startPixelY = Mathf.FloorToInt(startPos * (pickRTSize.y - 1));
            int endPixelY = Mathf.FloorToInt(endPos * (pickRTSize.y - 1));
            int totalHeight = endPixelY - startPixelY + 1;
            if (totalHeight <= 0) return;

            float sliceHeight = (float)totalHeight / sliceCount;

            for (int sliceIdx = 0; sliceIdx < sliceCount; sliceIdx++)
            {
                int sliceStartY = startPixelY + Mathf.FloorToInt(sliceIdx * sliceHeight);
                int sliceEndY = startPixelY + Mathf.FloorToInt((sliceIdx + 1) * sliceHeight) - 1;
                sliceEndY = Mathf.Min(sliceEndY, endPixelY);

                Dictionary<int, int> matIDCount = new Dictionary<int, int>();
                int sliceTotal = 0;

                for (int y = sliceStartY; y <= sliceEndY; y++)
                {
                    for (int x = 0; x < pickRTSize.x; x++)
                    {
                        int index = y * pickRTSize.x + x;
                        if (index < 0 || index >= m_DecodedDataArray.Length) continue;

                        var data = m_DecodedDataArray[index];
                        if (!data.isValid) continue;

                        if (!matIDCount.ContainsKey(data.matID))
                        {
                            matIDCount[data.matID] = 0;
                        }
                        matIDCount[data.matID]++;
                        sliceTotal++;
                    }
                }

                if (sliceTotal > 0)
                {
                    int dominantMatID = matIDCount.Aggregate((l, r) => l.Value > r.Value ? l : r).Key;
                    float sliceCenterY = (sliceStartY + sliceEndY) / 2f / (pickRTSize.y - 1);
                    float percentage = (float)matIDCount[dominantMatID] / sliceTotal * 100f;

                    results.Add(new MaterialStatResult
                    {
                        matID = dominantMatID,
                        location = sliceCenterY,
                        percentage = percentage,
                        pixelCount = matIDCount[dominantMatID]
                    });
                }
            }
        }

        public void GetDominantMaterialByHorizontalSlice(List<MaterialStatResult> results, int sliceCount = 10, float startPos = 0f, float endPos = 1f)
        {
            results.Clear();
            if (m_DecodedDataArray == null || m_DecodedDataArray.Length == 0) return;

            sliceCount = Mathf.Max(1, sliceCount);
            startPos = Mathf.Clamp01(startPos);
            endPos = Mathf.Clamp01(endPos);
            if (startPos > endPos)
            {
                float temp = startPos;
                startPos = endPos;
                endPos = temp;
            }

            int startPixelX = Mathf.FloorToInt(startPos * (pickRTSize.x - 1));
            int endPixelX = Mathf.FloorToInt(endPos * (pickRTSize.x - 1));
            int totalWidth = endPixelX - startPixelX + 1;
            if (totalWidth <= 0) return;

            float sliceWidth = (float)totalWidth / sliceCount;

            for (int sliceIdx = 0; sliceIdx < sliceCount; sliceIdx++)
            {
                int sliceStartX = startPixelX + Mathf.FloorToInt(sliceIdx * sliceWidth);
                int sliceEndX = startPixelX + Mathf.FloorToInt((sliceIdx + 1) * sliceWidth) - 1;
                sliceEndX = Mathf.Min(sliceEndX, endPixelX);

                Dictionary<int, int> matIDCount = new Dictionary<int, int>();
                int sliceTotal = 0;

                for (int x = sliceStartX; x <= sliceEndX; x++)
                {
                    for (int y = 0; y < pickRTSize.y; y++)
                    {
                        int index = y * pickRTSize.x + x;
                        if (index < 0 || index >= m_DecodedDataArray.Length) continue;

                        var data = m_DecodedDataArray[index];
                        if (!data.isValid) continue;

                        if (!matIDCount.ContainsKey(data.matID))
                        {
                            matIDCount[data.matID] = 0;
                        }
                        matIDCount[data.matID]++;
                        sliceTotal++;
                    }
                }

                if (sliceTotal > 0)
                {
                    int dominantMatID = matIDCount.Aggregate((l, r) => l.Value > r.Value ? l : r).Key;
                    float sliceCenterX = (sliceStartX + sliceEndX) / 2f / (pickRTSize.x - 1);
                    float percentage = (float)matIDCount[dominantMatID] / sliceTotal * 100f;

                    results.Add(new MaterialStatResult
                    {
                        matID = dominantMatID,
                        location = sliceCenterX,
                        percentage = percentage,
                        pixelCount = matIDCount[dominantMatID]
                    });
                }
            }
        }

        #endregion

        #region Inspector 配置参数

        [Header("拾取材质与尺寸")] public Material pickMaterial;
        public Vector2Int pickRTSize = new Vector2Int(64, 64);

        [Header("采样区域设置")] public Rect sampleRect = new Rect(0f, 0f, 1f, 1f);

        [Header("读取频率控制")] [Range(0.01f, 1.0f)]
        public float readbackInterval = 0.1f;

        [Header("调试设置")] public bool enableDebugLog = false;

        #endregion

        // 核心修改：全面使用 RTHandle 替代 RenderTexture
        private RTHandle m_PickRTHandle;
        private RTHandle m_ReadbackBufferHandle;

        private float m_TimeSinceLastReadback = 0f;
        private bool m_IsReadbackInProgress = false;
        private int m_ReadbackFrameCounter = 0;

        // 调试覆盖图相关
        private Material m_DebugOverlayMaterial;
        private static readonly int DebugRectProp = Shader.PropertyToID("_DebugRect");
        private static readonly int DebugMainTex = Shader.PropertyToID("_MainTex");

        class CustomRenderPass : ScriptableRenderPass
        {
            public Material passMaterial;
            public RTHandle passRTHandle;
            public RTHandle passReadbackBuffer;
            public List<SubMeshDrawData> passDrawList;
            public Rect passSampleRect;
            public bool requestReadback;

            // 屏幕调试 
            public bool drawDebugOverlay;
            public Material debugOverlayMaterial;
            public RTHandle cameraColorTarget; // 用于获取屏幕渲染目标

            private int m_MatIDProperty = Shader.PropertyToID("_MatID");

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (passMaterial == null || passRTHandle == null || passDrawList == null || passDrawList.Count == 0)
                    return;

                CommandBuffer cmd = CommandBufferPool.Get("GeoLayerPickBuffer");

                // 手动设置渲染目标并清理，避免 URP 隐式状态干扰
                cmd.SetRenderTarget(passRTHandle);
                cmd.ClearRenderTarget(true, true, Color.clear);

                Camera cam = renderingData.cameraData.camera;
                Matrix4x4 viewMatrix = cam.worldToCameraMatrix;
                Matrix4x4 projMatrix = cam.projectionMatrix;

                float ndcXMin = passSampleRect.x * 2f - 1f;
                float ndcXMax = (passSampleRect.x + passSampleRect.width) * 2f - 1f;
                float ndcYMin = passSampleRect.y * 2f - 1f;
                float ndcYMax = (passSampleRect.y + passSampleRect.height) * 2f - 1f;

                float scaleX = 2f / (ndcXMax - ndcXMin);
                float scaleY = 2f / (ndcYMax - ndcYMin);
                float offsetX = -1f - ndcXMin * scaleX;
                float offsetY = -1f - ndcYMin * scaleY;

                projMatrix.m00 *= scaleX;
                projMatrix.m11 *= scaleY;
                projMatrix.m02 += offsetX;
                projMatrix.m12 += offsetY;

                projMatrix = GL.GetGPUProjectionMatrix(projMatrix, true);
                cmd.SetViewProjectionMatrices(viewMatrix, projMatrix);
                cmd.SetViewport(new Rect(0, 0, passRTHandle.rt.width, passRTHandle.rt.height));

                for (int i = 0; i < passDrawList.Count; i++)
                {
                    var data = passDrawList[i];
                    cmd.SetGlobalInt(m_MatIDProperty, data.uniqueMatID);
                    cmd.DrawMesh(data.mesh, data.matrix, passMaterial, data.subMeshIndex);
                }

                cmd.SetViewProjectionMatrices(viewMatrix, renderingData.cameraData.GetProjectionMatrix());

                // 如果本帧需要回读，将数据安全地 Blit 到隔离缓冲区
                if (requestReadback && passReadbackBuffer != null)
                {
                    cmd.Blit(passRTHandle, passReadbackBuffer);
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);

                if (requestReadback && passReadbackBuffer != null)
                {
                    AsyncGPUReadback.Request(passReadbackBuffer.rt, 0, TextureFormat.RGBAFloat,
                        GeoLayerPickerRenderPassFeature.Instance.OnReadbackComplete);
                }

                // --- 绘制全屏调试图 ---
                if (drawDebugOverlay && debugOverlayMaterial != null && passRTHandle != null)
                {
                    var feature = GeoLayerPickerRenderPassFeature.Instance;
                    if (feature == null) return;

                    RTHandle currentColorTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
                    CommandBuffer debugCmd = CommandBufferPool.Get("GeoPickerDebugOverlay");

                    // 【核心 1】：使用 MaterialPropertyBlock 隔离参数
                    MaterialPropertyBlock mpb = new MaterialPropertyBlock();
                    
                    // 【修正 1】：直接使用字符串名称和 RTHandle 对象本身（RTHandle 隐式转为 Texture）
                    // 彻底避开 nameID (RenderTargetIdentifier) 的类型转换坑！
                    mpb.SetTexture(DebugMainTex, passRTHandle); 
                    
                    mpb.SetVector(DebugRectProp, new Vector4(
                        feature.sampleRect.x,
                        feature.sampleRect.y,
                        feature.sampleRect.width,
                        feature.sampleRect.height
                    ));

                    // 设置渲染目标为当前屏幕
                    CoreUtils.SetRenderTarget(debugCmd, currentColorTarget);

                    // 【修正 2】：DrawProcedural 必须显式传入 instanceCount (1) 作为第 6 个参数！
                    debugCmd.DrawProcedural(
                        Matrix4x4.identity,      // 1. matrix
                        debugOverlayMaterial,    // 2. material
                        0,                       // 3. shaderPass
                        MeshTopology.Triangles,  // 4. topology
                        3,                       // 5. vertexCount (全屏三角形)
                        1,                       // 6. instanceCount (必须显式写 1！)
                        mpb                      // 7. MaterialPropertyBlock
                    );

                    context.ExecuteCommandBuffer(debugCmd);
                    CommandBufferPool.Release(debugCmd);
                }
            }
        }

        private CustomRenderPass m_ScriptablePass;

        public override void Create()
        {
            Instance = this;
            m_IsDirty = true;

            m_ScriptablePass = new CustomRenderPass
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques
            };

            // 释放旧的 RTHandle
            if (m_PickRTHandle != null)
            {
                m_PickRTHandle.Release();
                m_PickRTHandle = null;
            }

            if (m_ReadbackBufferHandle != null)
            {
                m_ReadbackBufferHandle.Release();
                m_ReadbackBufferHandle = null;
            }

            // 分配新的 RTHandle
            m_PickRTHandle = RTHandles.Alloc(
                pickRTSize.x, pickRTSize.y,
                colorFormat: GraphicsFormat.R32G32B32A32_SFloat,
                filterMode: FilterMode.Point,
                name: "GeoLayerPickRT"
            );

            m_ReadbackBufferHandle = RTHandles.Alloc(
                pickRTSize.x, pickRTSize.y,
                colorFormat: GraphicsFormat.R32G32B32A32_SFloat,
                filterMode: FilterMode.Point,
                name: "GeoLayerReadbackBuffer"
            );

            int requiredLength = pickRTSize.x * pickRTSize.y;
            if (m_DecodedDataArray == null || m_DecodedDataArray.Length != requiredLength)
            {
                m_DecodedDataArray = new PickedGeoData[requiredLength];
                if (enableDebugLog) Debug.Log($"[GeoLayerPicker] 数组重新初始化，长度: {requiredLength}");
            }

            // 初始化调试材质
            if (m_DebugOverlayMaterial == null)
            {
                Shader overlayShader = Shader.Find("Hidden/XericLibrary/GeoLayerPicker/ScreenOverrideRect");
                if (overlayShader != null)
                {
                    m_DebugOverlayMaterial = CoreUtils.CreateEngineMaterial(overlayShader);
                }
                else
                {
                    Debug.LogError("[GeoLayerPicker] 找不到调试 Shader: Hidden/XericLibrary/GeoLayerPicker/ScreenOverrideRect");
                }
            }
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_ScriptablePass == null || pickMaterial == null) return;

            if (m_IsDirty)
            {
                RebuildDrawList();
                m_IsDirty = false;
                if (enableDebugLog) Debug.Log($"[GeoLayerPicker] DrawList 重建完成，包含 {m_DrawList.Count} 个 SubMesh。");
            }

            m_ReadbackFrameCounter++;
            if (m_IsReadbackInProgress && m_ReadbackFrameCounter > 60)
            {
                Debug.LogWarning("[GeoLayerPicker] AsyncGPUReadback 疑似卡死，强制重置并清理队列。");
                AsyncGPUReadback.WaitAllRequests();
                m_IsReadbackInProgress = false;
                m_ReadbackFrameCounter = 0;
            }

            m_TimeSinceLastReadback += Time.deltaTime;

            bool shouldRead = false;
            if (m_TimeSinceLastReadback >= readbackInterval && !m_IsReadbackInProgress)
            {
                m_TimeSinceLastReadback = 0f;
                m_IsReadbackInProgress = true;
                m_ReadbackFrameCounter = 0;
                shouldRead = true;
            }

            // 后处理参数
            m_ScriptablePass.passMaterial = pickMaterial;
            m_ScriptablePass.passRTHandle = m_PickRTHandle;
            m_ScriptablePass.passReadbackBuffer = m_ReadbackBufferHandle;
            m_ScriptablePass.passDrawList = m_DrawList;
            m_ScriptablePass.passSampleRect = sampleRect;
            m_ScriptablePass.requestReadback = shouldRead;

            // 配置调试覆盖图参数
            m_ScriptablePass.drawDebugOverlay = enableDebugLog; // 由 Inspector 开关控制
            m_ScriptablePass.debugOverlayMaterial = m_DebugOverlayMaterial;
            // m_ScriptablePass.cameraColorTarget = renderer.cameraColorTargetHandle; // 当前是注册阶段无法获取目标

            renderer.EnqueuePass(m_ScriptablePass);
        }

        private void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            m_IsReadbackInProgress = false;

            if (request.hasError || m_DecodedDataArray == null)
            {
                Debug.LogError("[GeoLayerPicker] Readback 发生错误或数组为空！");
                return;
            }

            var rawData = request.GetData<Color>();
            if (rawData.Length == 0) return;

            // 修复：使用 m_ReadbackBufferHandle.rt 获取宽高
            int width = m_ReadbackBufferHandle.rt.width;
            int height = m_ReadbackBufferHandle.rt.height;
            int count = m_DecodedDataArray.Length;

            bool flipY = SystemInfo.graphicsUVStartsAtTop;
            m_ValidDataCount = 0;

            for (int i = 0; i < count; i++)
            {
                int x = i % width;
                int y = i / width;

                int sourceY = flipY ? (height - 1 - y) : y;
                int sourceIndex = sourceY * width + x;

                if (sourceIndex < 0 || sourceIndex >= rawData.Length)
                {
                    m_DecodedDataArray[i] = new PickedGeoData { isValid = false };
                    continue;
                }

                Color pixel = rawData[sourceIndex];

                if (pixel.a > 0.5f)
                {
                    m_DecodedDataArray[i] = new PickedGeoData
                    {
                        isValid = true,
                        matID = Mathf.RoundToInt(pixel.a),
                        worldPos = new Vector3(pixel.r, pixel.g, pixel.b)
                    };
                    m_ValidDataCount++;
                }
                else
                {
                    m_DecodedDataArray[i] = new PickedGeoData { isValid = false, matID = 0 };
                }
            }

            if (enableDebugLog)
            {
                Debug.Log($"[GeoLayerPicker] 回读完成！总像素: {count}, 有效拾取像素: {m_ValidDataCount}");
            }
        }

        private void RebuildDrawList()
        {
            m_DrawList.Clear();
            m_MaterialToID.Clear();
            m_IDToMaterial.Clear();
            int globalIDCounter = 1;

            foreach (var mf in m_GeoMeshes)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                
                var renderer = mf.GetComponent<MeshRenderer>();
                Material[] materials = renderer != null ? renderer.sharedMaterials : null;

                for (int sub = 0; sub < mf.sharedMesh.subMeshCount; sub++)
                {
                    Material material = null;
                    if (materials != null && sub < materials.Length)
                    {
                        material = materials[sub];
                    }

                    int matID;
                    if (material != null)
                    {
                        if (!m_MaterialToID.TryGetValue(material, out matID))
                        {
                            matID = globalIDCounter++;
                            m_MaterialToID[material] = matID;
                            m_IDToMaterial[matID] = material;
                        }
                    }
                    else
                    {
                        matID = globalIDCounter++;
                    }

                    m_DrawList.Add(new SubMeshDrawData
                    {
                        mesh = mf.sharedMesh,
                        matrix = mf.transform.localToWorldMatrix,
                        subMeshIndex = sub,
                        uniqueMatID = matID,
                        material = material
                    });
                }
            }

            if (enableDebugLog)
            {
                Debug.Log($"[GeoLayerPicker] 材质映射重建完成，共 {m_MaterialToID.Count} 个材质");
                foreach (var kvp in m_MaterialToID)
                {
                    Debug.Log($"  ID {kvp.Value}: {kvp.Key.name}");
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            // 修复：正确释放 RTHandle 并置空
            if (m_PickRTHandle != null)
            {
                m_PickRTHandle.Release();
                m_PickRTHandle = null;
            }

            if (m_ReadbackBufferHandle != null)
            {
                m_ReadbackBufferHandle.Release();
                m_ReadbackBufferHandle = null;
            }

            // 释放调试材质
            if (m_DebugOverlayMaterial != null)
            {
                CoreUtils.Destroy(m_DebugOverlayMaterial);
                m_DebugOverlayMaterial = null;
            }

            if (Instance == this) Instance = null;
        }
    }
}
#endif