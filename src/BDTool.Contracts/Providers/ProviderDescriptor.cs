using System.Collections.Generic;

namespace BDTool.Contracts.Providers
{
    /// <summary>
    /// Provider 的公开描述。业务层只读取能力与限制，不判断 Provider 品牌。
    /// </summary>
    public sealed class ProviderDescriptor
    {
        /// <summary>稳定 Provider ID，例如 akio、local、official。</summary>
        public string Id { get; set; } = string.Empty;

        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Provider 类别，例如 workspace、editor。</summary>
        public string Kind { get; set; } = string.Empty;

        /// <summary>Provider 实现的公共契约版本。</summary>
        public string ContractVersion { get; set; } = string.Empty;

        /// <summary>Provider 自身实现版本。</summary>
        public string ImplementationVersion { get; set; } = string.Empty;

        /// <summary>支持的 Unity 版本范围；Workspace Provider 可为空。</summary>
        public string UnityRange { get; set; } = string.Empty;

        /// <summary>能力 ID 到可用性的映射。</summary>
        public Dictionary<string, bool> Capabilities { get; set; } = new Dictionary<string, bool>();

        /// <summary>能力限制，例如最大超时、最大结果大小。</summary>
        public Dictionary<string, string> Limits { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>Provider 类别常量。</summary>
    public static class ProviderKinds
    {
        public const string Workspace = "workspace";
        public const string Editor = "editor";
    }

    /// <summary>首批已冻结的公共能力 ID。</summary>
    public static class CapabilityIds
    {
        public const string EditorStatus = "editor.status";
    }
}
