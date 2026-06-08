# Unity 屏幕空间后处理阶段检查器-模型数据提取器

依赖 [MaterialFunctionLibrary](https://gitea.xeric.cc/LiRuochen_WorkFlow/MaterialFunctionLibrary) 插件。

插件能力：

1. 快速抓取到一个物体上多个不同材质分类区域在屏幕特定区域内的占比，中心位置坐标等信息
2. 支持webgl平台

## 使用示例

``` csharp

public class GeoLayerPicker : MonoBehaviour
{
    public List<MeshFilter> MeshFilters;
    public void Start()
    {
        GeoLayerPickerRenderPassFeature.Instance?.AddGeoMeshes(MeshFilters); // 将模型设置到处理器中
    }
    public void LateUpdate()
    {
        var islands = GeoLayerPickerRenderPassFeature.Instance.GetMaterialIslands();
        foreach (var island in islands)
        {
            Debug.Log($"材质: {island.GetMaterialName()} (ID:{island.matID})");
            Debug.Log($"  像素数: {island.totalPixelCount}");
            Debug.Log($"  中心世界坐标: {island.centerWorldPos}");
            Debug.Log($"  中心像素坐标: {island.centerPixelPos}");
        }
    }
}

```

处理器中已经包含了必要的转换功能，比如 `island.GetCenterRectAnchoredPosition(rectTransform)` 就可以得到ui坐标。


