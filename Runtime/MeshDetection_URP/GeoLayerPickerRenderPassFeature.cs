using System;
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
    /// 单行上的材质段信息
    /// </summary>
    public struct MaterialSegment
    {
        public int rowIndex;
        public int colStart;
        public int colEnd;
        public int matID;
        public int length => colEnd - colStart + 1;
    }

    /// <summary>
    /// 材质孤岛数据结构
    /// 表示一组相连的同材质像素区域
    /// </summary>
    public class MaterialIsland
    {
        public int matID;
        public List<MaterialSegment> segments = new List<MaterialSegment>();
        public int totalPixelCount;
        public Vector2 centerPixelPos;
        public Vector3 centerWorldPos;
        public bool isValid => matID > 0 && segments.Count > 0;

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

        public Vector2 GetCenterRectLocation()
        {
            var feature = GeoLayerPickerRenderPassFeature.Instance;
            if (feature == null)
                return Vector2.zero;

            float texWidth = feature.pickRTSize.x;
            float texHeight = feature.pickRTSize.y;

            float normalizedX = centerPixelPos.x / texWidth;
            float normalizedY = centerPixelPos.y / texHeight;

            return new Vector2(normalizedX, normalizedY);
        }

        public Vector2 GetCenterRectLocation(RectTransform rectTransform)
        {
            var normalized = GetCenterRectLocation();
            
            Vector2 rectSize = rectTransform.rect.size;
            float x = rectTransform.rect.x + normalized.x * rectSize.x;
            float y = rectTransform.rect.y + normalized.y * rectSize.y;

            return new Vector2(x, y);
        }

        public Vector2 GetCenterRectAnchoredPosition(RectTransform rectTransform)
        {
            var normalized = GetCenterRectLocation();

            Vector2 pivot = rectTransform.pivot;
            Vector2 rectSize = rectTransform.rect.size;

            float anchoredX = (normalized.x - pivot.x) * rectSize.x;
            float anchoredY = (normalized.y - pivot.y) * rectSize.y;

            return new Vector2(anchoredX, anchoredY);
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

        #region 材质孤岛统计 API

        private List<MaterialIsland> m_MaterialIslands = new List<MaterialIsland>();
        private bool m_IslandsDirty = true;

        public IReadOnlyList<MaterialIsland> GetMaterialIslands()
        {
            UpdateMaterialIslands();
            return m_MaterialIslands;
        }

        public List<MaterialIsland> GetMaterialIslandsByMatID(int matID)
        {
            UpdateMaterialIslands();
            return m_MaterialIslands.Where(island => island.matID == matID).ToList();
        }

        public int GetIslandCount()
        {
            UpdateMaterialIslands();
            return m_MaterialIslands.Count;
        }

        public void MarkIslandsDirty()
        {
            m_IslandsDirty = true;
        }

        private void UpdateMaterialIslands()
        {
            if (!m_IslandsDirty && m_MaterialIslands.Count > 0) return;
            if (m_DecodedDataArray == null || m_DecodedDataArray.Length == 0)
            {
                m_MaterialIslands.Clear();
                m_IslandsDirty = false;
                return;
            }

            FindMaterialIslands();
            m_IslandsDirty = false;

            if (enableDebugLog)
            {
                Debug.Log($"[GeoLayerPicker] 材质孤岛统计完成，共 {m_MaterialIslands.Count} 个孤岛");
                foreach (var island in m_MaterialIslands)
                {
                    Debug.Log($"  MatID {island.matID} ({island.GetMaterialName()}): {island.totalPixelCount} 像素, 中心: {island.centerWorldPos}");
                }
            }
        }

        private void FindMaterialIslands()
        {
            m_MaterialIslands.Clear();
            
            int width = pickRTSize.x;
            int height = pickRTSize.y;

            List<List<MaterialSegment>> rowSegments = new List<List<MaterialSegment>>();
            for (int y = 0; y < height; y++)
            {
                var segments = ExtractRowSegments(y, width);
                rowSegments.Add(segments);
            }

            List<List<MaterialIsland>> islandGroups = new List<List<MaterialIsland>>();
            for (int matID = 1; matID <= m_IDToMaterial.Count; matID++)
            {
                var islands = BuildIslandsForMaterial(matID, rowSegments, width, height);
                islandGroups.Add(islands);
            }

            foreach (var islands in islandGroups)
            {
                m_MaterialIslands.AddRange(islands);
            }
        }

        private List<MaterialSegment> ExtractRowSegments(int rowIndex, int width)
        {
            List<MaterialSegment> segments = new List<MaterialSegment>();
            if (m_DecodedDataArray == null) return segments;

            int col = 0;
            while (col < width)
            {
                int index = rowIndex * width + col;
                if (index >= m_DecodedDataArray.Length) break;

                PickedGeoData pixel = m_DecodedDataArray[index];
                
                if (!pixel.isValid || pixel.matID <= 0)
                {
                    col++;
                    continue;
                }

                int startCol = col;
                int matID = pixel.matID;

                while (col < width)
                {
                    int checkIndex = rowIndex * width + col;
                    if (checkIndex >= m_DecodedDataArray.Length) break;
                    
                    PickedGeoData checkPixel = m_DecodedDataArray[checkIndex];
                    if (!checkPixel.isValid || checkPixel.matID != matID)
                    {
                        break;
                    }
                    col++;
                }

                segments.Add(new MaterialSegment
                {
                    rowIndex = rowIndex,
                    colStart = startCol,
                    colEnd = col - 1,
                    matID = matID
                });
            }

            return segments;
        }

        private List<MaterialIsland> BuildIslandsForMaterial(int targetMatID, List<List<MaterialSegment>> rowSegments, int width, int height)
        {
            List<MaterialIsland> islands = new List<MaterialIsland>();

            for (int y = 0; y < height; y++)
            {
                if (y >= rowSegments.Count) break;

                foreach (var currentSegment in rowSegments[y].Where(s => s.matID == targetMatID))
                {
                    List<MaterialIsland> connectedIslands = new List<MaterialIsland>();

                    if (y > 0 && y - 1 < rowSegments.Count)
                    {
                        foreach (var island in islands)
                        {
                            foreach (var existingSegment in island.segments)
                            {
                                if (existingSegment.rowIndex == y - 1)
                                {
                                    if (SegmentsOverlap(currentSegment, existingSegment))
                                    {
                                        connectedIslands.Add(island);
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (connectedIslands.Count == 0)
                    {
                        MaterialIsland newIsland = new MaterialIsland
                        {
                            matID = targetMatID
                        };
                        newIsland.segments.Add(currentSegment);
                        islands.Add(newIsland);
                    }
                    else if (connectedIslands.Count == 1)
                    {
                        connectedIslands[0].segments.Add(currentSegment);
                    }
                    else
                    {
                        MaterialIsland mergedIsland = new MaterialIsland
                        {
                            matID = targetMatID
                        };

                        foreach (var islandToMerge in connectedIslands)
                        {
                            mergedIsland.segments.AddRange(islandToMerge.segments);
                            islands.Remove(islandToMerge);
                        }
                        mergedIsland.segments.Add(currentSegment);
                        islands.Add(mergedIsland);
                    }
                }
            }

            CalculateIslandProperties(islands, width, height);

            return islands;
        }

        private bool SegmentsOverlap(MaterialSegment a, MaterialSegment b)
        {
            return !(a.colEnd < b.colStart - 1 || a.colStart > b.colEnd + 1);
        }

        private void CalculateIslandProperties(List<MaterialIsland> islands, int width, int height)
        {
            foreach (var island in islands)
            {
                island.totalPixelCount = island.segments.Sum(s => s.length);

                if (island.segments.Count == 0)
                {
                    island.centerPixelPos = Vector2.zero;
                    island.centerWorldPos = Vector3.zero;
                    continue;
                }

                int minRow = island.segments.Min(s => s.rowIndex);
                int maxRow = island.segments.Max(s => s.rowIndex);
                float avgRow = (minRow + maxRow) * 0.5f;

                MaterialSegment longestSegment = island.segments.OrderByDescending(s => s.length).First();
                float avgCol = (longestSegment.colStart + longestSegment.colEnd) * 0.5f;

                island.centerPixelPos = new Vector2(avgCol, avgRow);

                int centerX = Mathf.Clamp(Mathf.RoundToInt(avgCol), 0, width - 1);
                int centerY = Mathf.Clamp(Mathf.RoundToInt(avgRow), 0, height - 1);
                int centerIndex = centerY * width + centerX;

                if (centerIndex >= 0 && centerIndex < m_DecodedDataArray.Length)
                {
                    island.centerWorldPos = m_DecodedDataArray[centerIndex].worldPos;
                }
                else
                {
                    island.centerWorldPos = Vector3.zero;
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
                if (passMaterial != null && passRTHandle != null && passDrawList != null && passDrawList.Count > 0)
                {
                    CommandBuffer cmd = CommandBufferPool.Get("GeoLayerPickBuffer");

                    cmd.SetRenderTarget(passRTHandle);
                    cmd.ClearRenderTarget(true, true, new Color(0, 0, 0, 0));

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

                    MaterialPropertyBlock drawMpb = new MaterialPropertyBlock();
                    
                    for (int i = 0; i < passDrawList.Count; i++)
                    {
                        var data = passDrawList[i];
                        
                        drawMpb.SetInt(m_MatIDProperty, data.uniqueMatID);
                        cmd.DrawMesh(data.mesh, data.matrix, passMaterial, data.subMeshIndex, 0, drawMpb);
                    }
                    
                    cmd.SetViewProjectionMatrices(viewMatrix, renderingData.cameraData.GetProjectionMatrix());

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
                }

                if (drawDebugOverlay && debugOverlayMaterial != null && passRTHandle != null)
                {
                    var feature = GeoLayerPickerRenderPassFeature.Instance;
                    if (feature == null) return;

                    RTHandle currentColorTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
                    CommandBuffer debugCmd = CommandBufferPool.Get("GeoPickerDebugOverlay");

                    MaterialPropertyBlock debugMpb = new MaterialPropertyBlock();
                    debugMpb.SetTexture(DebugMainTex, passRTHandle); 
                    
                    debugMpb.SetVector(DebugRectProp, new Vector4(
                        feature.sampleRect.x,
                        feature.sampleRect.y,
                        feature.sampleRect.width,
                        feature.sampleRect.height
                    ));

                    CoreUtils.SetRenderTarget(debugCmd, currentColorTarget);

                    debugCmd.DrawProcedural(
                        Matrix4x4.identity,
                        debugOverlayMaterial,
                        0,
                        MeshTopology.Triangles,
                        3,
                        1,
                        debugMpb
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

            MarkIslandsDirty();
        }

        /// <summary>
        /// 重建模型绘制列表
        /// </summary>
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

                    if (material == null)
                    {
                        continue;
                    }

                    int matID;
                    if (!m_MaterialToID.TryGetValue(material, out matID))
                    {
                        matID = globalIDCounter++;
                        m_MaterialToID[material] = matID;
                        m_IDToMaterial[matID] = material;
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