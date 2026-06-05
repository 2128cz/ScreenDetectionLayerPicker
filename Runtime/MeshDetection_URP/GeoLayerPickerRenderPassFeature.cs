using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

#if UNITY_2022_1_OR_NEWER // urp 13
namespace XericLibrary.PostProcess
{
    // 定义数据结构，用来存储展平后的 SubMesh 信息
    [System.Serializable]
    public struct SubMeshDrawData
    {
        public Mesh mesh;
        public Matrix4x4 matrix;
        public int subMeshIndex;
        public int uniqueMatID;
    }

    public class GeoLayerPickerRenderPassFeature : ScriptableRendererFeature
    {
        
        // 在 Feature 外部类中，添加 Inspector 面板需要的配置参数
        [Header("拾取设置")]
        public Material pickMaterial;
        public MeshFilter[] geoMeshes;
        public Vector2Int pickRTSize = new Vector2Int(64, 64);

        // 内部持有的资源
        private RenderTexture m_PickRT;
        private List<SubMeshDrawData> m_DrawList = new List<SubMeshDrawData>();

        class GeoLayerPickerRenderPass : ScriptableRenderPass
        {
            public Material passMaterial;
            public RenderTexture passRT;
            public List<SubMeshDrawData> passDrawList;
            
            private int m_MatIDProperty = Shader.PropertyToID("_MatID");
            
            public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
            {
                // 在 URP 中，必须在这里告诉管线我们要渲染到哪张 RT 上
                if (passRT != null)
                {
                    ConfigureTarget(new RenderTargetIdentifier(passRT));
                    ConfigureClear(ClearFlag.All, Color.clear);
                }
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                // 安全检查
                if (passMaterial == null || passRT == null || passDrawList == null || passDrawList.Count == 0) 
                    return;

                // 从对象池获取 CommandBuffer (性能更好)
                CommandBuffer cmd = CommandBufferPool.Get("GeoLayerPickBuffer");

                // 在 URP 13 中，绝对不能在 Execute 里用 cmd.SetRenderTarget()。必须重写 OnCameraSetup，使用 ConfigureTarget() 告诉 URP 这个 Pass 的输出目标是你创建的 m_PickRT。
                // 设置视口为 RT 的尺寸 (64x64)
                cmd.SetViewport(new Rect(0, 0, passRT.width, passRT.height));

                // 【核心】遍历绘制每一个 SubMesh
                for (int i = 0; i < passDrawList.Count; i++)
                {
                    var data = passDrawList[i];
                    cmd.SetGlobalInt(m_MatIDProperty, data.uniqueMatID);
                    // 注意第四个参数 submeshIndex，这是多材质插槽的关键
                    cmd.DrawMesh(data.mesh, data.matrix, passMaterial, data.subMeshIndex);
                }

                // 提交命令给 GPU 执行
                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }

            public override void OnCameraCleanup(CommandBuffer cmd)
            {
            }
        }

        GeoLayerPickerRenderPass m_ScriptablePass;

        public override void Create()
        {
            m_ScriptablePass = new GeoLayerPickerRenderPass();

            m_ScriptablePass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;

            // 【步骤 4】在 Create 中初始化 RT，并展平 SubMesh 数据
            if (m_PickRT != null) m_PickRT.Release();
            m_PickRT = new RenderTexture(pickRTSize.x, pickRTSize.y, 0, RenderTextureFormat.ARGB32);
            m_PickRT.Create();

            RebuildDrawList();
        }

        
        // 提供一个公开方法，方便运行时动态刷新网格列表
        public void RebuildDrawList()
        {
            m_DrawList.Clear();
            if (geoMeshes == null) 
                return;
            
            int globalIDCounter = 1; // ID 从 1 开始，0 留给背景
            foreach (var mf in geoMeshes)
            {
                if (mf == null || mf.sharedMesh == null) continue;

                // 遍历网格的每一个材质插槽 (SubMesh)
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

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // 将外部数据传递给内部的 Pass，并注入管线
            if (m_ScriptablePass != null)
            {
                m_ScriptablePass.passMaterial = pickMaterial;
                m_ScriptablePass.passRT = m_PickRT;
                m_ScriptablePass.passDrawList = m_DrawList;
                
                renderer.EnqueuePass(m_ScriptablePass);
            }
        }
        
        
        // 暴露 RT 给外部读取像素用
        public RenderTexture GetPickRT() => m_PickRT;
    }
}
#endif