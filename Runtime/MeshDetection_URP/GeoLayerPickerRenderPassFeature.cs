using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
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
        private bool m_IsDirty = true;

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

        #region Inspector 配置参数

        [Header("拾取材质与尺寸")] 
        public Material pickMaterial;
        public Vector2Int pickRTSize = new Vector2Int(64, 64);

        [Header("采样区域设置")] 
        public Rect sampleRect = new Rect(0f, 0f, 1f, 1f);

        [Header("读取频率控制")] 
        [Range(0.01f, 1.0f)]
        public float readbackInterval = 0.1f;
        
        [Header("调试设置")]
        public bool enableDebugLog = false;

        #endregion

        // 核心修改：全面使用 RTHandle 替代 RenderTexture
        private RTHandle m_PickRTHandle; 
        private RTHandle m_ReadbackBufferHandle; 
        
        private float m_TimeSinceLastReadback = 0f;
        private bool m_IsReadbackInProgress = false;
        private int m_ReadbackFrameCounter = 0; 

        class CustomRenderPass : ScriptableRenderPass
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

                // 在 CommandBuffer 执行后，发起回读请求（读取隔离缓冲区）
                if (requestReadback && passReadbackBuffer != null)
                {
                    AsyncGPUReadback.Request(passReadbackBuffer.rt, 0, TextureFormat.RGBAFloat,
                        GeoLayerPickerRenderPassFeature.Instance.OnReadbackComplete);
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
            if (m_PickRTHandle != null) { m_PickRTHandle.Release(); m_PickRTHandle = null; }
            if (m_ReadbackBufferHandle != null) { m_ReadbackBufferHandle.Release(); m_ReadbackBufferHandle = null; }

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
                if(enableDebugLog) Debug.Log($"[GeoLayerPicker] 数组重新初始化，长度: {requiredLength}");
            }
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_ScriptablePass == null || pickMaterial == null) return;

            if (m_IsDirty)
            {
                RebuildDrawList();
                m_IsDirty = false;
                if(enableDebugLog) Debug.Log($"[GeoLayerPicker] DrawList 重建完成，包含 {m_DrawList.Count} 个 SubMesh。");
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

            m_ScriptablePass.passMaterial = pickMaterial;
            m_ScriptablePass.passRTHandle = m_PickRTHandle;
            m_ScriptablePass.passReadbackBuffer = m_ReadbackBufferHandle; 
            m_ScriptablePass.passDrawList = m_DrawList;
            m_ScriptablePass.passSampleRect = sampleRect;
            m_ScriptablePass.requestReadback = shouldRead;

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
            int globalIDCounter = 1;

            foreach (var mf in m_GeoMeshes)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                for (int sub = 0; sub < mf.sharedMesh.subMeshCount; sub++)
                {
                    m_DrawList.Add(new SubMeshDrawData
                    {
                        mesh = mf.sharedMesh,
                        matrix = mf.transform.localToWorldMatrix,
                        subMeshIndex = sub,
                        uniqueMatID = globalIDCounter++
                    });
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
            if (Instance == this) Instance = null;
        }
    }
}
#endif