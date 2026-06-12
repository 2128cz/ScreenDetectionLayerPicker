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
    /// 摄像机匹配模式
    /// </summary>
    public enum CameraMatchMode
    {
        /// <summary> 通过 MainCamera 标签匹配 </summary>
        Tag,
        /// <summary> 通过名称匹配 "Main Camera" </summary>
        Name
    }
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

        public Vector3 minWorldPos;
        public Vector3 maxWorldPos;
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

            float screenX = feature.sampleRect.x + normalizedX * feature.sampleRect.width;
            float screenY = feature.sampleRect.y + normalizedY * feature.sampleRect.height;

            return new Vector2(screenX, screenY);
        }

        public Vector2 GetCenterRectLocation(RectTransform rectTransform)
        {
            var screenPos = GetCenterRectLocation();

            Vector2 rectSize = rectTransform.rect.size;
            float x = rectTransform.rect.x + screenPos.x * rectSize.x;
            float y = rectTransform.rect.y + screenPos.y * rectSize.y;

            return new Vector2(x, y);
        }

        public Vector2 GetCenterRectAnchoredPosition(RectTransform rectTransform)
        {
            var screenPos = GetCenterRectLocation();

            Vector2 pivot = rectTransform.pivot;
            Vector2 rectSize = rectTransform.rect.size;

            float anchoredX = (screenPos.x - pivot.x) * rectSize.x;
            float anchoredY = (screenPos.y - pivot.y) * rectSize.y;

            return new Vector2(anchoredX, anchoredY);
        }

        public Vector3 GetCenterWorldPosition()
        {
            return centerWorldPos;
        }

        public Vector2 GetScreenPosition(Camera camera = null)
        {
            if (camera == null)
            {
                camera = Camera.main;
            }

            if (camera == null)
                return Vector2.zero;

            return camera.WorldToScreenPoint(centerWorldPos);
        }

        public Vector2 GetViewportPosition(Camera camera = null)
        {
            if (camera == null)
            {
                camera = Camera.main;
            }

            if (camera == null)
                return Vector2.zero;

            return camera.WorldToViewportPoint(centerWorldPos);
        }

        public Bounds GetWorldBounds()
        {
            Bounds bounds = new Bounds(centerWorldPos, Vector3.zero);
            bounds.Encapsulate(minWorldPos);
            bounds.Encapsulate(maxWorldPos);
            return bounds;
        }

        public float GetWorldVolume()
        {
            Vector3 size = maxWorldPos - minWorldPos;
            return Mathf.Abs(size.x * size.y * size.z);
        }

        public float GetWorldArea()
        {
            Vector2 size = new Vector2(
                Mathf.Abs(maxWorldPos.x - minWorldPos.x),
                Mathf.Abs(maxWorldPos.z - minWorldPos.z)
            );
            return size.x * size.y;
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
            if (!isActive)
                return Array.Empty<MaterialIsland>();
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
                using (MacroMath.GetStringBuilder(out var sb))
                {
                    foreach (var island in m_MaterialIslands)
                        sb.Append(
                            $"  MatID {island.matID} ({island.GetMaterialName()}): {island.totalPixelCount} 像素, 中心: {island.centerWorldPos}");
                    Debug.Log($"[GeoLayerPicker] 材质孤岛统计完成，共 {m_MaterialIslands.Count} 个孤岛\n{sb.ToString()}");
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

        private List<MaterialIsland> BuildIslandsForMaterial(int targetMatID, List<List<MaterialSegment>> rowSegments,
            int width, int height)
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
                    island.minWorldPos = Vector3.zero;
                    island.maxWorldPos = Vector3.zero;
                    continue;
                }

                int minRow = island.segments.Min(s => s.rowIndex);
                int maxRow = island.segments.Max(s => s.rowIndex);
                float avgRow = (minRow + maxRow) * 0.5f;

                MaterialSegment longestSegment = island.segments.OrderByDescending(s => s.length).First();
                float avgCol = (longestSegment.colStart + longestSegment.colEnd) * 0.5f;

                island.centerPixelPos = new Vector2(avgCol, avgRow);

                Dictionary<int, int> rowPixelCounts = new Dictionary<int, int>();
                foreach (var seg in island.segments)
                {
                    if (!rowPixelCounts.ContainsKey(seg.rowIndex))
                    {
                        rowPixelCounts[seg.rowIndex] = 0;
                    }

                    rowPixelCounts[seg.rowIndex] += seg.length;
                }

                int maxRowIndex = rowPixelCounts.OrderByDescending(kvp => kvp.Value).First().Key;

                var segmentsInMaxRow = island.segments.Where(s => s.rowIndex == maxRowIndex).ToList();
                MaterialSegment maxSegment = segmentsInMaxRow.OrderByDescending(s => s.length).First();

                int centerX = (maxSegment.colStart + maxSegment.colEnd) / 2;
                int centerY = maxRowIndex;

                int centerIndex = centerY * width + centerX;
                if (centerIndex >= 0 && centerIndex < m_DecodedDataArray.Length)
                {
                    island.centerWorldPos = m_DecodedDataArray[centerIndex].worldPos;
                }
                else
                {
                    island.centerWorldPos = Vector3.zero;
                }

                island.minWorldPos = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                island.maxWorldPos = new Vector3(float.MinValue, float.MinValue, float.MinValue);

                foreach (var seg in island.segments)
                {
                    for (int col = seg.colStart; col <= seg.colEnd; col++)
                    {
                        int index = seg.rowIndex * width + col;
                        if (index < 0 || index >= m_DecodedDataArray.Length)
                            continue;

                        var pixelData = m_DecodedDataArray[index];
                        if (pixelData.isValid && pixelData.matID == island.matID)
                        {
                            island.minWorldPos = Vector3.Min(island.minWorldPos, pixelData.worldPos);
                            island.maxWorldPos = Vector3.Max(island.maxWorldPos, pixelData.worldPos);
                        }
                    }
                }
            }
        }

        #endregion

        #region Inspector 配置参数

        [Header("摄像机过滤")]
        [Tooltip("开启后只在 MainCamera 上执行，避免多摄像机 FOV 不一致导致结果错位。")]
        public bool onlyOnMainCamera = true;

        [Tooltip("MainCamera 匹配模式：Tag = 通过 MainCamera 标签匹配；Name = 通过名称匹配")]
        public CameraMatchMode cameraMatchMode = CameraMatchMode.Tag;

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

        class PickRenderPass : ScriptableRenderPass
        {
            public Material passMaterial;
            public RTHandle passRTHandle;
            public RTHandle passReadbackBuffer;
            public List<SubMeshDrawData> passDrawList;
            public Rect passSampleRect;
            public bool requestReadback;

            private int m_MatIDProperty = Shader.PropertyToID("_MatID");

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (passMaterial == null || passRTHandle == null || passDrawList == null || passDrawList.Count == 0)
                    return;

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

                // 使用 remap 矩阵将 NDC 子区域映射到 [-1, 1]
                // remap * proj 在 clip space 中做仿射变换：
                //   clipX' = remap.m00 * clipX + remap.m03 * clipW
                //   clipW' = clipW（不变）
                //   ndc'   = clipX'/clipW' = remap.m00 * ndc + remap.m03
                // 边界: ndcMin → -1, ndcMax → +1
                // 此法对透视投影和正交投影均正确
                Matrix4x4 remap = Matrix4x4.identity;
                remap.m00 = 2f / (ndcXMax - ndcXMin);
                remap.m03 = -(ndcXMax + ndcXMin) / (ndcXMax - ndcXMin);
                remap.m11 = 2f / (ndcYMax - ndcYMin);
                remap.m13 = -(ndcYMax + ndcYMin) / (ndcYMax - ndcYMin);

                projMatrix = remap * projMatrix;
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
        }

        class DebugOverlayRenderPass : ScriptableRenderPass
        {
            public RTHandle sourceRTHandle;
            public Material overlayMaterial;
            public Rect sampleRect;

            private static readonly int DebugRectProp = Shader.PropertyToID("_DebugRect");
            private static readonly int DebugMainTex = Shader.PropertyToID("_MainTex");
            private static bool s_HasLogged = false;

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (!s_HasLogged && GeoLayerPickerRenderPassFeature.Instance != null &&
                    GeoLayerPickerRenderPassFeature.Instance.enableDebugLog)
                {
                    Debug.Log($"[GeoLayerPicker] DebugOverlayRenderPass.Execute called" +
                    $"  overlayMaterial: {(overlayMaterial != null ? "OK" : "null")}" +
                    $"  sourceRTHandle: {(sourceRTHandle != null ? "OK" : "null")}" +
                    $"  sampleRect: {sampleRect}");
                    s_HasLogged = true;
                }

                if (overlayMaterial == null)
                {
                    if (GeoLayerPickerRenderPassFeature.Instance != null &&
                        GeoLayerPickerRenderPassFeature.Instance.enableDebugLog)
                    {
                        Debug.LogWarning("[GeoLayerPicker] DebugOverlayRenderPass: overlayMaterial is null");
                    }

                    return;
                }

                if (sourceRTHandle == null)
                {
                    if (GeoLayerPickerRenderPassFeature.Instance != null &&
                        GeoLayerPickerRenderPassFeature.Instance.enableDebugLog)
                    {
                        Debug.LogWarning("[GeoLayerPicker] DebugOverlayRenderPass: sourceRTHandle is null");
                    }

                    return;
                }

                RTHandle currentColorTarget = renderingData.cameraData.renderer.cameraColorTargetHandle;
                if (currentColorTarget == null)
                {
                    if (GeoLayerPickerRenderPassFeature.Instance != null &&
                        GeoLayerPickerRenderPassFeature.Instance.enableDebugLog)
                    {
                        Debug.LogWarning("[GeoLayerPicker] DebugOverlayRenderPass: currentColorTarget is null");
                    }

                    return;
                }

                CommandBuffer debugCmd = CommandBufferPool.Get("GeoPickerDebugOverlay");
                debugCmd.Clear();

                MaterialPropertyBlock debugMpb = new MaterialPropertyBlock();
                debugMpb.SetTexture(DebugMainTex, sourceRTHandle);

                debugMpb.SetVector(DebugRectProp, new Vector4(
                    sampleRect.x,
                    sampleRect.y,
                    sampleRect.width,
                    sampleRect.height
                ));

                CoreUtils.SetRenderTarget(debugCmd, currentColorTarget);
                debugCmd.SetViewport(new Rect(0, 0, renderingData.cameraData.camera.pixelWidth,
                    renderingData.cameraData.camera.pixelHeight));

                debugCmd.DrawProcedural(
                    Matrix4x4.identity,
                    overlayMaterial,
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

        private PickRenderPass m_PickPass;
        private DebugOverlayRenderPass m_DebugOverlayPass;

        public override void Create()
        {
            Instance = this;
            m_IsDirty = true;

            m_PickPass = new PickRenderPass
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques
            };

            m_DebugOverlayPass = new DebugOverlayRenderPass
            {
                renderPassEvent = RenderPassEvent.AfterRenderingTransparents
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
                    Debug.LogError(
                        "[GeoLayerPicker] 找不到调试 Shader: Hidden/XericLibrary/GeoLayerPicker/ScreenOverrideRect");
                }
            }
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            Camera currentCam = renderingData.cameraData.camera;

            // 如果开启 onlyOnMainCamera，只匹配目标摄像机
            if (onlyOnMainCamera && !IsMainCamera(currentCam))
                return;

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

            if (m_PickPass != null && pickMaterial != null)
            {
                m_PickPass.passMaterial = pickMaterial;
                m_PickPass.passRTHandle = m_PickRTHandle;
                m_PickPass.passReadbackBuffer = m_ReadbackBufferHandle;
                m_PickPass.passDrawList = m_DrawList;
                m_PickPass.passSampleRect = sampleRect;
                m_PickPass.requestReadback = shouldRead;
                renderer.EnqueuePass(m_PickPass);
            }

            if (m_DebugOverlayPass != null && enableDebugLog && m_DebugOverlayMaterial != null)
            {
                m_DebugOverlayPass.sourceRTHandle = m_PickRTHandle;
                m_DebugOverlayPass.overlayMaterial = m_DebugOverlayMaterial;
                m_DebugOverlayPass.sampleRect = sampleRect;
                renderer.EnqueuePass(m_DebugOverlayPass);
            }
        }

        /// <summary> 判断当前摄像机是否为目标摄像机 </summary>
        private bool IsMainCamera(Camera cam)
        {
            if (cam == null) return false;

            switch (cameraMatchMode)
            {
                case CameraMatchMode.Tag:
                    return cam.CompareTag("MainCamera");
                case CameraMatchMode.Name:
                    return cam.name == "Main Camera";
                default:
                    return false;
            }
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
            if (m_ReadbackBufferHandle == null)
            {
                Debug.LogWarning("[GeoLayerPicker] 更新时机不对！");
                return;
            }
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
                using (MacroMath.GetStringBuilder(out var sb))
                {
                    foreach (var kvp in m_MaterialToID)
                        sb.Append($"  ID {kvp.Value}: {kvp.Key.name}");
                    Debug.Log($"[GeoLayerPicker] 材质映射重建完成，共 {m_MaterialToID.Count} 个材质 \n{sb.ToString()}");
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