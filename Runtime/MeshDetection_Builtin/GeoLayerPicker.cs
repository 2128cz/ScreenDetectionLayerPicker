using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using XericLibrary.Runtime.Type;

namespace Deconstruction.ScreenDetection.MeshDetection
{
    /// <summary>
    /// 在屏幕上截取模型的表面属性
    /// 
    /// </summary>
    public class GeoLayerPicker : MonoBehaviour
    {
        public Camera targetCamera;
        public Material pickMaterial;
        public MeshFilter[] geoMeshes; // 外部只需拖入地质层网格

        public Vector2Int pickRTSize = new Vector2Int(64, 64);
        public Bool2 pickRTAdapt = new Bool2(false, true);

        private RenderTexture pickRT;
        private Texture2D readTex;
        private CommandBuffer cmd;
        private float nextCheckTime;

        private static readonly int MatID = Shader.PropertyToID("_MatID");

        // 【核心】缓存展平后的绘制数据
        private struct SubMeshDrawData
        {
            public Mesh mesh;
            public Matrix4x4 matrix;
            public int subMeshIndex;
            public int uniqueMatID; // 分配给这个插槽的唯一ID
        }

        private List<SubMeshDrawData> drawList = new List<SubMeshDrawData>();

        void Start()
        {
            // 1. 展平所有网格的 SubMesh
            if (geoMeshes != null)
            {
                int globalIDCounter = 1; // ID从1开始，0留给背景
                foreach (var mf in geoMeshes)
                {
                    if (mf == null || mf.sharedMesh == null) continue;

                    Mesh mesh = mf.sharedMesh;
                    Matrix4x4 matrix = mf.transform.localToWorldMatrix;

                    // 遍历这个网格上的每一个材质插槽
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    {
                        drawList.Add(new SubMeshDrawData
                        {
                            mesh = mesh,
                            matrix = matrix,
                            subMeshIndex = sub,
                            uniqueMatID = globalIDCounter++
                        });
                    }
                }
            }

            // 2. 初始化 RT 和 CommandBuffer
            pickRT = new RenderTexture(pickRTSize.x, pickRTSize.y, 0, RenderTextureFormat.ARGB32);
            pickRT.Create();
            readTex = new Texture2D(pickRTSize.x, pickRTSize.y, TextureFormat.RGBA32, false);

            cmd = new CommandBuffer();
            cmd.name = "GeoLayerPickBuffer";
            targetCamera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, cmd);
        }

        void Update()
        {
            // if (Time.time < nextCheckTime) return;
            // nextCheckTime = Time.time + 0.2f;

            cmd.Clear();

            // 视口设置
            var screenSize = new Vector2(Screen.width, Screen.height);
            var screenStart = (screenSize - pickRTSize) / 2;
            Rect pickRect = new Rect(pickRTAdapt.X * screenStart.x, pickRTAdapt.Y * screenStart.y, pickRTAdapt.x ? Screen.width : pickRTSize.x,
                pickRTAdapt.x ? Screen.height : pickRTSize.y);
            cmd.SetViewport(pickRect);
            cmd.SetRenderTarget(pickRT);
            cmd.ClearRenderTarget(true, true, Color.clear);

            // 【核心】遍历展平后的 SubMesh 列表进行绘制
            for (int i = 0; i < drawList.Count; i++)
            {
                var data = drawList[i];
                pickMaterial.SetInt(MatID, data.uniqueMatID);

                // 使用带 submeshIndex 参数的 DrawMesh 重载
                cmd.DrawMesh(data.mesh, data.matrix, pickMaterial, data.subMeshIndex);
            }

            // 读取像素
            RenderTexture.active = pickRT;
            readTex.ReadPixels(new Rect(0, 0, pickRTSize.x, pickRTSize.y), 0, 0);
            readTex.Apply();
            RenderTexture.active = null;

            ProcessPickedData();
        }

        void ProcessPickedData()
        {
            Color32[] pixels = readTex.GetPixels32();
            // 解码 pixels 中的 uniqueMatID 和 WorldPos
            // 根据 uniqueMatID 反查地质层信息并更新 UI...
        }
    }
}