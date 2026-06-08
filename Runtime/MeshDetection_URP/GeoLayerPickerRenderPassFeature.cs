using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;
using Unity.Collections;
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
        /// <summary> 材质/SubMesh 的唯一 ID，0 表示背景 </summary>
        public int matID;

        /// <summary> 该像素点对应的世界坐标 </summary>
        public Vector3 worldPos;

        /// <summary> 是否拾取到了有效的地质层 </summary>
        public bool isValid;
    }

    /// <summary>
    /// 地质层拾取 RenderFeature。
    /// 负责在 URP 管线中注入自定义 RenderPass，管理模型，并自动在后台降频读取、解码 GPU 数据。
    /// </summary>
    public class GeoLayerPickerRenderPassFeature : ScriptableRendererFeature
    {
        #region 静态单例与外部管理 API

        /// <summary>
        /// 当前激活的 Feature 实例
        /// </summary>
        public static GeoLayerPickerRenderPassFeature Instance { get; private set; }

        private readonly List<MeshFilter> m_GeoMeshes = new List<MeshFilter>();
        private readonly List<SubMeshDrawData> m_DrawList = new List<SubMeshDrawData>();
        private bool m_IsDirty = true;

        /// <summary> 添加需要检查的场景模型对象 </summary>
        public void AddGeoMesh(MeshFilter meshFilter)
        {
            if (meshFilter != null && !m_GeoMeshes.Contains(meshFilter))
            {
                m_GeoMeshes.Add(meshFilter);
                m_IsDirty = true;
            }
        }

        /// <summary> 批量添加需要检查的场景模型对象 </summary>
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

        /// <summary> 移除指定的场景模型对象 </summary>
        public void RemoveGeoMesh(MeshFilter meshFilter)
        {
            if (m_GeoMeshes.Remove(meshFilter)) m_IsDirty = true;
        }

        /// <summary> 清空所有已添加的场景模型对象 </summary>
        public void ClearGeoMeshes()
        {
            if (m_GeoMeshes.Count > 0)
            {
                m_GeoMeshes.Clear();
                m_IsDirty = true;
            }
        }

        /// <summary> 获取当前所有已添加的场景模型对象（只读） </summary>
        public IReadOnlyList<MeshFilter> GetGeoMeshes() => m_GeoMeshes;

        #endregion

        #region 数据访问 API (供外部 UI 或逻辑调用)

        // 内部维护的解码后的数据数组 (大小等于 pickRTSize.x * pickRTSize.y)
        private PickedGeoData[] m_DecodedDataArray;

        /// <summary>
        /// 获取当前帧已解码的拾取数据数组。
        /// 数组按行优先排列 (Index = y * width + x)。
        /// </summary>
        public IReadOnlyList<PickedGeoData> GetDecodedData() => m_DecodedDataArray;

        /// <summary>
        /// 获取一列的数据
        /// </summary>
        /// <param name="column"></param>
        /// <param name="listData"></param>
        public void GetColumnData(int column, List<PickedGeoData> listData)
        {
            listData.Clear();
            column = MacroMath.ClampToInt(column, 0, pickRTSize.x);
            for (int i = 0; i < pickRTSize.y; i++)
            {
                listData.Add(m_DecodedDataArray[pickRTSize.x * i + column]);
            }
        }

        /// <summary>
        /// 根据归一化的屏幕坐标 (0~1) 获取拾取数据。
        /// 会自动映射到 sampleRect 和 pickRTSize 范围内。
        /// </summary>
        /// <param name="normalizedScreenPos">归一化屏幕坐标 (左下角为 0,0)</param>
        /// <param name="data">输出的拾取数据</param>
        /// <returns>是否成功获取（坐标是否在采样区域内）</returns>
        public bool TryGetPickedData(Vector2 normalizedScreenPos, out PickedGeoData data)
        {
            data = default;
            if (m_DecodedDataArray == null || m_DecodedDataArray.Length == 0) return false;

            // 1. 将屏幕坐标转换为相对于 sampleRect 的局部坐标 (0~1)
            float localX = (normalizedScreenPos.x - sampleRect.x) / sampleRect.width;
            float localY = (normalizedScreenPos.y - sampleRect.y) / sampleRect.height;

            // 2. 检查是否超出了采样区域
            if (localX < 0f || localX > 1f || localY < 0f || localY > 1f) return false;

            // 3. 映射到 RT 的像素坐标
            int pixelX = Mathf.Clamp(Mathf.FloorToInt(localX * pickRTSize.x), 0, pickRTSize.x - 1);
            int pixelY = Mathf.Clamp(Mathf.FloorToInt(localY * pickRTSize.y), 0, pickRTSize.y - 1);

            // 4. 获取数据
            int index = pixelY * pickRTSize.x + pixelX;
            data = m_DecodedDataArray[index];
            return true;
        }

        #endregion

        #region Inspector 配置参数

        [Header("拾取材质与尺寸")] [Tooltip("用于渲染地质层 ID 和坐标的 HDR 材质球")]
        public Material pickMaterial;

        [Tooltip("渲染目标纹理的尺寸，越小性能越好（例如 64x64）")]
        public Vector2Int pickRTSize = new Vector2Int(64, 64);

        [Header("采样区域设置")] [Tooltip("定义屏幕上的采样区域 (归一化坐标 0~1)。\n例如: X=0.25, Y=0.25, W=0.5, H=0.5 表示只渲染屏幕正中间一半的区域。")]
        public Rect sampleRect = new Rect(0f, 0f, 1f, 1f);

        [Header("读取频率控制")] [Tooltip("每隔多少秒从 GPU 读取一次数据 (例如 0.1 秒 = 10 FPS)")] [Range(0.01f, 1.0f)]
        public float readbackInterval = 0.1f;

        #endregion

        private RenderTexture m_PickRT;
        private float m_TimeSinceLastReadback = 0f;
        private bool m_IsReadbackInProgress = false;

        /// <summary>
        /// 自定义 RenderPass
        /// </summary>
        class CustomRenderPass : ScriptableRenderPass
        {
            public Material passMaterial;
            public RenderTexture passRT;
            public List<SubMeshDrawData> passDrawList;
            public Rect passSampleRect;
            public bool requestReadback; // 标记本帧是否需要触发异步读取

            private int m_MatIDProperty = Shader.PropertyToID("_MatID");

            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                if (passRT != null)
                {
                    ConfigureTarget(new RenderTargetIdentifier(passRT));
                    // 清屏为 0,0,0,0 (背景 ID 为 0)
                    ConfigureClear(ClearFlag.All, Color.clear);
                }
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                if (passMaterial == null || passRT == null || passDrawList == null || passDrawList.Count == 0)
                    return;

                CommandBuffer cmd = CommandBufferPool.Get("GeoLayerPickBuffer");

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
                cmd.SetViewport(new Rect(0, 0, passRT.width, passRT.height));

                for (int i = 0; i < passDrawList.Count; i++)
                {
                    var data = passDrawList[i];
                    cmd.SetGlobalInt(m_MatIDProperty, data.uniqueMatID);
                    cmd.DrawMesh(data.mesh, data.matrix, passMaterial, data.subMeshIndex);
                }

                cmd.SetViewProjectionMatrices(viewMatrix, renderingData.cameraData.GetProjectionMatrix());
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);

                // 【核心】：如果计时器到了，在这里发起异步 GPU 回读
                if (requestReadback)
                {
                    AsyncGPUReadback.Request(passRT, 0, TextureFormat.RGBAFloat,
                        GeoLayerPickerRenderPassFeature.Instance.OnReadbackComplete);
                }
            }

            public override void OnCameraCleanup(CommandBuffer cmd)
            {
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

            if (m_PickRT != null) m_PickRT.Release();
            m_PickRT = new RenderTexture(pickRTSize.x, pickRTSize.y, 0, RenderTextureFormat.ARGBFloat)
            {
                filterMode = FilterMode.Point
            };
            m_PickRT.Create();

            // 初始化解码数组
            m_DecodedDataArray = new PickedGeoData[pickRTSize.x * pickRTSize.y];
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (m_ScriptablePass == null || pickMaterial == null) return;

            if (m_IsDirty)
            {
                RebuildDrawList();
                m_IsDirty = false;
            }

            // 降频计时器逻辑
            m_TimeSinceLastReadback += Time.deltaTime;

            if (m_IsReadbackInProgress && m_TimeSinceLastReadback > 1.0f)
            {
                Debug.LogWarning("[GeoLayerPicker] AsyncGPUReadback 超时，强制重置状态。");
                m_IsReadbackInProgress = false;
            }

            bool shouldRead = false;
            if (m_TimeSinceLastReadback >= readbackInterval && !m_IsReadbackInProgress)
            {
                m_TimeSinceLastReadback = 0f;
                m_IsReadbackInProgress = true;
                shouldRead = true;
            }

            m_ScriptablePass.passMaterial = pickMaterial;
            m_ScriptablePass.passRT = m_PickRT;
            m_ScriptablePass.passDrawList = m_DrawList;
            m_ScriptablePass.passSampleRect = sampleRect;
            m_ScriptablePass.requestReadback = shouldRead;

            renderer.EnqueuePass(m_ScriptablePass);
        }

        /// <summary>
        /// 异步回读完成回调 (在后台线程或主线程延迟执行，不阻塞渲染)
        /// </summary>
        private void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            m_IsReadbackInProgress = false;

            if (request.hasError || m_DecodedDataArray == null) 
            {
                Debug.LogError("[GeoLayerPicker] Readback 发生错误或数组为空！");
                return;
            }

            // 【核心修复】：使用 GetData<Color> 获取数据
            var rawData = request.GetData<Color>();
            if (rawData.Length == 0) return;
            
            int width = m_PickRT.width;
            int height = m_PickRT.height;
    
            // 【核心修复】：不要依赖 rawData.Length 作为遍历上限，
            // 而是以我们目标数组 m_DecodedDataArray 的长度为准！
            int count = m_DecodedDataArray.Length; 

            // DirectX 平台下，异步回读的 Y 轴是翻转的
            bool flipY = SystemInfo.graphicsUVStartsAtTop; 

            for (int i = 0; i < count; i++)
            {
                // 计算目标像素的坐标
                int x = i % width;
                int y = i / width;

                // 处理 DirectX 的 Y 轴翻转
                int sourceY = flipY ? (height - 1 - y) : y;
        
                // 计算源数据在 rawData 中的真实索引
                int sourceIndex = sourceY * width + x;

                // 【安全防线】：防止源索引超出 rawData 的实际物理长度
                if (sourceIndex < 0 || sourceIndex >= rawData.Length) 
                {
                    m_DecodedDataArray[i] = new PickedGeoData { isValid = false };
                    continue;
                }

                Color pixel = rawData[sourceIndex];
        
                // 你的 Shader 输出是 float4(worldPos.xyz, matID)
                // 在标准 RGBA 映射下，A 通道就是 ID
                if (pixel.a > 0.1f) 
                {
                    m_DecodedDataArray[i] = new PickedGeoData
                    {
                        isValid = true,
                        matID = Mathf.RoundToInt(pixel.a),
                        worldPos = new Vector3(pixel.r, pixel.g, pixel.b)
                    };
                }
                else
                {
                    m_DecodedDataArray[i] = new PickedGeoData { isValid = false, matID = 0 };
                }
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
            if (m_PickRT != null && m_PickRT.IsCreated()) m_PickRT.Release();
            if (Instance == this) Instance = null;
        }
    }
}
#endif