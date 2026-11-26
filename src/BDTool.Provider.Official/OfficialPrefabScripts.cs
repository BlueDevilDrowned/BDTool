using System.Text.Json;
using BDTool.Contracts.Errors;
using BDTool.Contracts.Operations;

namespace BDTool.Provider.Official;

/// <summary>
/// 官方 Pipeline 尚未提供 Prefab 根 activeSelf 的专用命令，因此通过其公开
/// `script eval` 命令执行固定、参数转义后的脚本来补齐现有 BDTool 操作契约。
/// 脚本不接受调用方任意代码，所有变量均先经过类型和资产路径校验。
/// </summary>
internal static class OfficialPrefabScripts
{
    public static string BuildSnapshotCode(string assetPath)
    {
        EnsurePrefabPath(assetPath);
        return
            "var path = " + Quote(assetPath) + ";\r\n"
            + "var asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);\r\n"
            + "if (asset == null) return new { ok = true, assetPath = path, exists = false, sha256 = \"\", rootObjectName = \"\", childCount = 0, unityVersion = Application.unityVersion };\r\n"
            + "var diskPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), path);\r\n"
            + "var bytes = System.IO.File.ReadAllBytes(diskPath);\r\n"
            + "string sha256;\r\n"
            + "using (var sha = System.Security.Cryptography.SHA256.Create()) { sha256 = System.BitConverter.ToString(sha.ComputeHash(bytes)).Replace(\"-\", \"\").ToLowerInvariant(); }\r\n"
            + "return new { ok = true, assetPath = path, exists = true, sha256, rootObjectName = asset.name, childCount = asset.transform.childCount, unityVersion = Application.unityVersion };";
    }

    public static string BuildSetActiveCode(
        string assetPath,
        string expectedSha256,
        bool isActive,
        bool dryRun)
    {
        EnsurePrefabPath(assetPath);
        if (!IsHexSha256(expectedSha256))
        {
            throw new ArgumentException("expectedSha256 必须是 64 位 SHA-256。");
        }

        return
            "var path = " + Quote(assetPath) + ";\r\n"
            + "var expected = " + Quote(expectedSha256.ToLowerInvariant()) + ";\r\n"
            + "var targetActive = " + BoolLiteral(isActive) + ";\r\n"
            + "var dryRun = " + BoolLiteral(dryRun) + ";\r\n"
            + "var diskPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), path);\r\n"
            + "Func<string> hash = () => { using (var sha = System.Security.Cryptography.SHA256.Create()) return System.BitConverter.ToString(sha.ComputeHash(System.IO.File.ReadAllBytes(diskPath))).Replace(\"-\", \"\").ToLowerInvariant(); };\r\n"
            + "if (!System.IO.File.Exists(diskPath)) return new { ok = false, errorCode = \"NOT_FOUND\", message = \"Prefab asset not found.\" };\r\n"
            + "var previous = hash();\r\n"
            + "if (!String.Equals(previous, expected, StringComparison.OrdinalIgnoreCase)) return new { ok = false, errorCode = \"PRECONDITION_FAILED\", message = \"Prefab SHA-256 changed before write.\" };\r\n"
            + "var root = PrefabUtility.LoadPrefabContents(path);\r\n"
            + "if (root == null) return new { ok = false, errorCode = \"NOT_FOUND\", message = \"Prefab contents could not be loaded.\" };\r\n"
            + "var wasActive = root.activeSelf;\r\n"
            + "var changed = wasActive != targetActive;\r\n"
            + "var finalActive = targetActive;\r\n"
            + "var saved = false;\r\n"
            + "var verified = false;\r\n"
            + "try { if (!dryRun && changed) { root.SetActive(targetActive); PrefabUtility.SaveAsPrefabAsset(root, path, out var success); saved = success; } }\r\n"
            + "finally { PrefabUtility.UnloadPrefabContents(root); }\r\n"
            + "var newSha = dryRun ? previous : hash();\r\n"
            + "if (!dryRun) { AssetDatabase.SaveAssets(); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate); var loaded = AssetDatabase.LoadAssetAtPath<GameObject>(path); finalActive = loaded != null ? loaded.activeSelf : wasActive; verified = loaded != null && loaded.activeSelf == targetActive; newSha = hash(); }\r\n"
            + "return new { ok = true, assetPath = path, previousSha256 = previous, newSha256 = newSha, wasActive, isActive = finalActive, changed, saved, dryRun, undoGroup = \"\", verified };";
    }

    private static void EnsurePrefabPath(string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath)
            || assetPath.Contains("..", StringComparison.Ordinal)
            || assetPath.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0
            || !assetPath.Replace('\\', '/').StartsWith("Assets/", StringComparison.Ordinal)
            || !assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("assetPath 必须是 Assets 下的单个 .prefab 资产路径。");
        }
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static string BoolLiteral(bool value) => value ? "true" : "false";

    private static bool IsHexSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    public static void EnsureSuccessfulResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("ok", out var ok)
            || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new OfficialPipelineException(
                ErrorCodes.ExecutionFailed,
                "Official Prefab 脚本没有返回有效 ok 字段。");
        }

        if (!ok.GetBoolean())
        {
            var code = result.TryGetProperty("errorCode", out var codeElement)
                && codeElement.ValueKind == JsonValueKind.String
                    ? codeElement.GetString() ?? ErrorCodes.ExecutionFailed
                    : ErrorCodes.ExecutionFailed;
            var message = result.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString() ?? "Official Prefab command failed."
                    : "Official Prefab command failed.";
            throw new OfficialPipelineException(code, message);
        }
    }
}
